using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MixCut.Services.AI;

/// <summary>
/// OpenAI 兼容 API 客户端（同时支持 Claude 原生 Messages API）。
/// 对应 macOS 版 OpenAICompatibleClient。每次调用由 <see cref="AIProviderManager"/> 动态创建，
/// 以便设置变更立即生效。
/// </summary>
public sealed class OpenAICompatibleClient : IAiProvider
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 按 provider 设置 max_tokens 上限。对齐 macOS 版 commit 3406d61 的「四层防御」之一：
    /// 默认 4096 经常装不下 4-5 个策略 / 长视频的分镜列表，导致 finish_reason=length 截断。
    /// </summary>
    private static readonly Dictionary<AIProviderType, int> MaxTokensByProvider = new()
    {
        [AIProviderType.Qwen] = 8192,
        [AIProviderType.Deepseek] = 8192,
        [AIProviderType.Minimax] = 16384,
        [AIProviderType.Claude] = 16384,
        [AIProviderType.ClaudeRelay] = 16384,
        [AIProviderType.Custom] = 8192,
    };

    private static int MaxTokensFor(AIProviderType p) =>
        MaxTokensByProvider.GetValueOrDefault(p, 8192);

    // 共享 HttpClient，避免 socket 耗尽；单次请求超时由 CancellationToken 控制。
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AIProviderType _providerType;
    private readonly string _apiKey;
    private readonly string _modelName;
    private readonly string _baseUrl;
    private readonly ILogger _logger;

    public OpenAICompatibleClient(
        AIProviderType providerType,
        string apiKey,
        string modelName,
        string baseUrl,
        ILogger logger)
    {
        _providerType = providerType;
        _apiKey = apiKey;
        _modelName = modelName;
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        _logger = logger;
    }

    public async Task<T> GenerateJsonAsync<T>(string prompt, CancellationToken cancellationToken = default)
    {
        var text = await SendRequestAsync(prompt, cancellationToken, jsonMode: true);
        if (TryParseJson<T>(text, out var first, out _))
        {
            return first!;
        }

        // 自动重试一次：上一次没吐合法 JSON，追加更强硬的「只输出 JSON」指令再请求一次。
        // 多数 OpenAI 兼容模型只是偶发把 prompt 当对话回复，二次强约束往往能纠正 —— 比直接判失败、
        // 让用户手动重试体验好得多（对齐 §最高原则：每个 >1s 的失败都要有兜底，而非把锅甩给用户）。
        _logger.LogWarning("AI 首次未按 JSON 返回，追加强约束指令自动重试一次…");
        var retryText = await SendRequestAsync(prompt + StrictJsonRetrySuffix, cancellationToken, jsonMode: true);
        if (TryParseJson<T>(retryText, out var second, out var retryJson))
        {
            _logger.LogInformation("AI 二次强约束重试解析成功。");
            return second!;
        }

        // 两次都失败：完整转储 + 详细日志 + 完整人话错误（展示模型实际返回内容，便于用户判断换哪个模型）。
        DumpFailedJson(retryJson);
        _logger.LogError("JSON 解析失败（两次请求 + 三层修复均失败），完整响应见 logs/ai-fail/。");
        _logger.LogError("原始响应前 800 字符: {Json}", Truncate(retryJson, 800));
        throw AIProviderException.JsonParsingFailed(BuildJsonParseErrorDetail(retryJson));
    }

    /// <summary>
    /// 三层 JSON 解析防御（直接解 → 截断修复 → 错误位置救援），全部失败返回 false。
    /// 抽成独立方法以便「自动重试一次」复用同一套解析逻辑。<paramref name="jsonStr"/> 输出清洗后的
    /// JSON 文本，供失败时转储/报错使用。
    /// </summary>
    private bool TryParseJson<T>(string text, out T? result, out string jsonStr)
    {
        // 预清洗：移除 ASCII 控制字符 + BOM，避免 MiniMax 等模型的 JSON 污染。
        jsonStr = JsonTruncationRepair.Sanitize(ExtractJson(text));

        // 第一次：直接解。
        try
        {
            var r = JsonSerializer.Deserialize<T>(jsonStr, JsonOptions);
            if (r is not null) { result = r; return true; }
        }
        catch (JsonException) { /* 落到下面的修复尝试 */ }

        // 第二次：尝试 JsonTruncationRepair 修复后再解（救回被 max_tokens 截断的对象/数组）。
        var repaired = JsonTruncationRepair.Repair(jsonStr);
        if (!ReferenceEquals(repaired, jsonStr) && repaired != jsonStr)
        {
            try
            {
                var r = JsonSerializer.Deserialize<T>(repaired, JsonOptions);
                if (r is not null)
                {
                    _logger.LogInformation("JSON 截断修复成功：原长 {Orig} → 修复后 {New}",
                        jsonStr.Length, repaired.Length);
                    result = r; return true;
                }
            }
            catch (JsonException) { /* 落到第三次救援 */ }
        }

        // 第三次：用 JsonException 拿到精确错误位置，截到错误前最后一个完整 `},` 再修复重试。
        // 对齐 Mac truncateAtJSONError，能救回某 segment 中间吐脏字符的情况。
        var rescued = TruncateAtJsonError(jsonStr);
        if (rescued is not null && rescued != jsonStr)
        {
            try
            {
                var r = JsonSerializer.Deserialize<T>(rescued, JsonOptions);
                if (r is not null)
                {
                    _logger.LogInformation("JSON 错误位置救援成功：原长 {Orig} → 救援后 {New}",
                        jsonStr.Length, rescued.Length);
                    result = r; return true;
                }
            }
            catch (JsonException) { /* 落到最终错误 */ }
        }

        result = default;
        return false;
    }

    /// <summary>二次重试时追加到 prompt 末尾的强约束指令（比常驻 system prompt 更直白、点名上次失败）。</summary>
    private const string StrictJsonRetrySuffix =
        "\n\n【严格要求·重要】你上一次的输出无法被 JSON 解析器解析。本次回复必须【只】包含一个合法的 JSON 值"
        + "（对象 {} 或数组 []），禁止任何前言/后语/解释/markdown 代码围栏(```)/注释。"
        + "整段回复要能被 JSON.parse 直接解析，否则视为失败。";

    /// <summary>构造给用户看的「AI 没按 JSON 返回」人话错误：展示模型实际返回开头，引导换模型，不暴露原始报错码。</summary>
    private static string BuildJsonParseErrorDetail(string jsonStr)
    {
        if (string.IsNullOrWhiteSpace(jsonStr))
        {
            return "AI 返回了空内容（可能被服务端安全策略拦截或账号额度不足）。请稍后重试，或在「设置」里更换模型。";
        }
        var snippet = Truncate(jsonStr.Trim().Replace("\r", " ").Replace("\n", " "), 200);
        return $"AI 没有按要求返回 JSON（已自动重试一次仍失败）。它实际返回的开头是：「{snippet}」。"
             + "通常是该模型不擅长结构化输出 —— 建议在「设置」里换一个更强的模型"
             + "（如 DeepSeek / Claude Sonnet / Qwen-Max）后重试。完整响应已保存到日志备查。";
    }

    /// <summary>
    /// 第三道防御：当 JSON 解析失败时，用 JsonException 拿到错误的精确字符位置，
    /// 截到错误前最后一个完整 `},` 再用 JsonTruncationRepair 补齐闭合，让 AI 至少返回的前几个完整对象不被丢弃。
    /// 对齐 Mac truncateAtJSONError。
    /// </summary>
    private static string? TruncateAtJsonError(string jsonStr)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            return null; // 能解析就不需要救
        }
        catch (JsonException ex)
        {
            // BytePositionInLine 只在 LineNumber=0 时是绝对位置；多行时需重新算字符偏移。
            // 简化：直接按 line/col 计算近似 char position。
            var bytePos = ex.BytePositionInLine ?? -1;
            var lineNum = ex.LineNumber ?? -1;
            if (bytePos < 0 || lineNum < 0) return null;

            int errorPos;
            if (lineNum == 0)
            {
                errorPos = (int)bytePos;
            }
            else
            {
                // 按 \n 数到第 lineNum 行后再加 bytePos
                var lineStart = 0;
                for (var i = 0; i < lineNum && lineStart >= 0; i++)
                {
                    var idx = jsonStr.IndexOf('\n', lineStart);
                    if (idx < 0) { lineStart = -1; break; }
                    lineStart = idx + 1;
                }
                if (lineStart < 0) return null;
                errorPos = lineStart + (int)bytePos;
            }
            if (errorPos <= 1 || errorPos > jsonStr.Length) return null;

            // 取错误位置之前的字符串，找最后一个 `},` 作为安全切点
            var prefix = jsonStr.Substring(0, errorPos);
            var lastSafeIdx = -1;
            for (var i = 1; i < prefix.Length; i++)
            {
                if (prefix[i - 1] == '}' && prefix[i] == ',')
                {
                    lastSafeIdx = i - 1;
                }
            }
            if (lastSafeIdx < 0) return null;

            var trimmed = prefix.Substring(0, lastSafeIdx + 1);
            // 加 `,` 让 repair 把数组/对象闭合
            return JsonTruncationRepair.Repair(trimmed + ",");
        }
    }

    /// <summary>把解析失败的完整 JSON 转储到 logs/ai-fail/ 目录，便于事后诊断。对齐 Mac dumpFailedJSON。</summary>
    private static void DumpFailedJson(string jsonStr)
    {
        try
        {
            var dir = System.IO.Path.Combine(Utilities.AppPaths.LogDirectory, "ai-fail");
            System.IO.Directory.CreateDirectory(dir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var file = System.IO.Path.Combine(dir, $"ai-fail-{timestamp}.json");
            System.IO.File.WriteAllText(file, jsonStr, Encoding.UTF8);
        }
        catch
        {
            // 转储失败不影响主流程
        }
    }

    public Task<string> GenerateTextAsync(string prompt, CancellationToken cancellationToken = default) =>
        SendRequestAsync(prompt, cancellationToken, jsonMode: false);

    private async Task<string> SendRequestAsync(string prompt, CancellationToken cancellationToken, bool jsonMode)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogError("API Key 为空！provider={Provider}", _providerType.DisplayName());
            throw AIProviderException.ApiKeyNotConfigured(_providerType);
        }

        if (string.IsNullOrEmpty(_baseUrl) || !_baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("baseURL 无效！provider={Provider}, url={Url}", _providerType.DisplayName(), _baseUrl);
            throw AIProviderException.RequestFailed(
                $"{_providerType.DisplayName()} 网关地址未配置或格式错误，请在设置中填写");
        }

        _logger.LogInformation("发送请求: provider={Provider}, model={Model}, prompt长度={Len}",
            _providerType.DisplayName(), _modelName, prompt.Length);

        Exception? lastError = null;
        var isClaude = _providerType.IsClaudeNative();

        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    var delay = BaseRetryDelay * (1 << attempt);
                    await Task.Delay(delay, cancellationToken);
                    _logger.LogInformation("重试第 {Attempt} 次...", attempt);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(RequestTimeout);

                var url = isClaude ? $"{_baseUrl}/messages" : $"{_baseUrl}/chat/completions";
                using var request = new HttpRequestMessage(HttpMethod.Post, url);

                if (isClaude)
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                }

                var body = BuildRequestBody(prompt, jsonMode);

                request.Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, timeoutCts.Token);
                var data = await response.Content.ReadAsStringAsync(timeoutCts.Token);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    lastError = AIProviderException.RateLimited();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    // QW-14：某些 API 网关的错误响应体会回显 Authorization header（含 Bearer token）。
                    // 写日志 / 抛给上层前先脱敏，避免密钥泄漏到日志文件或冒泡到用户可见的错误信息。
                    _logger.LogError("HTTP {Code}: {Body}", code, Redact(Truncate(data, 500)));
                    // HTTP 402 Payment Required = 账户余额不足。协议语义明确，必须单独翻译成「去充值」，
                    // 不能混进笼统的「检查配置」里。部分网关把余额不足塞进 400/403 响应体（如 DeepSeek
                    // 的 "Insufficient Balance"），所以同时按响应体文本兜底识别。
                    if (code == 402 || data.Contains("insufficient balance", StringComparison.OrdinalIgnoreCase))
                    {
                        throw AIProviderException.InsufficientBalance();
                    }
                    // 其余 4xx（429 已在上面单独处理）是确定性客户端错误（模型名/接口地址/参数配置错），
                    // 重试 3 次也是同样结果 → 立即抛不可重试异常，避免用户配错后干等 ~14s 指数退避。
                    if (code is >= 400 and < 500)
                    {
                        throw AIProviderException.ClientError(code, Redact(Truncate(data, 200)));
                    }
                    throw AIProviderException.RequestFailed(
                        $"HTTP {code}: {Redact(Truncate(data, 200))}");
                }

                return isClaude ? ParseClaudeResponse(data) : ParseOpenAIResponse(data);
            }
            catch (AIProviderException ex)
            {
                lastError = ex;
                if (ex.Kind is AIProviderErrorKind.ApiKeyNotConfigured
                    or AIProviderErrorKind.ClientError
                    or AIProviderErrorKind.InsufficientBalance)
                {
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                var msg = ex.ToString().ToLowerInvariant();
                if (msg.Contains("429") || msg.Contains("rate"))
                {
                    lastError = AIProviderException.RateLimited();
                }
            }
        }

        throw lastError ?? AIProviderException.RequestFailed("未知错误");
    }

    // ---- 按提供商定制请求体（不同模型的 JSON 强制机制不同）----

    /// <summary>
    /// 强制 JSON 输出的系统提示。Claude/Gemini/不支持 response_format 的模型用这个兜底。
    /// </summary>
    private const string JsonSystemPrompt =
        "You are a strict JSON-only responder. " +
        "Output ONLY a single valid JSON value (object or array). " +
        "No markdown code fences (no ```), no explanations, no commentary, no leading or trailing text. " +
        "Your entire response must be directly parseable by a JSON parser. " +
        "如果请求要求结构化输出，仅输出合法 JSON；不要任何额外文字。";

    private object BuildRequestBody(string prompt, bool jsonMode)
    {
        return _providerType switch
        {
            // Claude 原生 API：用顶层 system 字段，不支持 response_format
            AIProviderType.Claude => BuildClaudeNativeBody(prompt, jsonMode),

            // 国内转发网关（OpenAI 协议）：根据实际选的模型决定能否带 response_format
            AIProviderType.ClaudeRelay => BuildOpenAICompatBody(
                prompt, jsonMode, addResponseFormat: jsonMode && RelayPlatformSupportsResponseFormat()),

            // 千问、MiniMax、DeepSeek、自定义：OpenAI 兼容，均支持 response_format
            AIProviderType.Qwen or AIProviderType.Minimax or AIProviderType.Deepseek or AIProviderType.Custom
                => BuildOpenAICompatBody(prompt, jsonMode, addResponseFormat: jsonMode),

            _ => BuildOpenAICompatBody(prompt, jsonMode, addResponseFormat: false),
        };
    }

    /// <summary>Claude（Anthropic 原生 API）请求体。</summary>
    private object BuildClaudeNativeBody(string prompt, bool jsonMode)
    {
        var maxTokens = MaxTokensFor(_providerType);
        if (jsonMode)
        {
            return new
            {
                model = _modelName,
                max_tokens = maxTokens,
                system = JsonSystemPrompt,
                temperature = 0.3,
                messages = new[] { new { role = "user", content = prompt } },
            };
        }
        return new
        {
            model = _modelName,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = prompt } },
        };
    }

    /// <summary>OpenAI 兼容请求体（千问/MiniMax/DeepSeek/转发网关/自定义）。</summary>
    private object BuildOpenAICompatBody(string prompt, bool jsonMode, bool addResponseFormat)
    {
        var maxTokens = MaxTokensFor(_providerType);
        if (!jsonMode)
        {
            return new
            {
                model = _modelName,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.7,
                max_tokens = maxTokens,
            };
        }

        var messages = new object[]
        {
            new { role = "system", content = JsonSystemPrompt },
            new { role = "user", content = prompt },
        };

        if (addResponseFormat)
        {
            return new
            {
                model = _modelName,
                messages,
                temperature = 0.3,
                max_tokens = maxTokens,
                response_format = new { type = "json_object" },
            };
        }
        return new
        {
            model = _modelName,
            messages,
            temperature = 0.3,
            max_tokens = maxTokens,
        };
    }

    /// <summary>转发网关下，根据实际选的模型判断是否带 response_format。</summary>
    private bool RelayPlatformSupportsResponseFormat()
    {
        var m = _modelName.ToLowerInvariant();
        // Claude / Gemini 经 OpenAI 兼容网关时多数不识别 response_format（甚至会 400）
        if (m.StartsWith("claude", StringComparison.Ordinal))
        {
            return false;
        }
        if (m.StartsWith("gemini", StringComparison.Ordinal))
        {
            return false;
        }
        // GPT / o3 / 其他 OpenAI 系模型支持
        return true;
    }

    private string ParseClaudeResponse(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
            // 检测 stop_reason="max_tokens"，提示用户输出被截断。
            if (doc.RootElement.TryGetProperty("stop_reason", out var sr) &&
                sr.GetString() == "max_tokens")
            {
                _logger.LogWarning("⚠ Claude 响应因达到 max_tokens 被截断！考虑增大 max_tokens 或精简 prompt");
            }
            if (text is not null)
            {
                _logger.LogInformation("AI 响应成功: {Len} 字符", text.Length);
                return text;
            }
        }
        catch (Exception)
        {
            // 落到下方统一报错
        }

        _logger.LogError("无法解析 Claude 响应: {Body}", Truncate(data, 500));
        throw AIProviderException.InvalidResponse("无法解析 Claude 响应");
    }

    private string ParseOpenAIResponse(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var choice = doc.RootElement.GetProperty("choices")[0];
            // 检测 finish_reason="length"，提示用户输出被截断。
            if (choice.TryGetProperty("finish_reason", out var fr) &&
                fr.GetString() == "length")
            {
                _logger.LogWarning(
                    "⚠ {Provider} 响应因达到 max_tokens 被截断！考虑增大 max_tokens 或精简 prompt",
                    _providerType.DisplayName());
            }
            var text = choice.GetProperty("message").GetProperty("content").GetString();
            if (text is not null)
            {
                _logger.LogInformation("AI 响应成功: {Len} 字符", text.Length);
                return text;
            }
        }
        catch (Exception)
        {
            // 落到下方统一报错
        }

        _logger.LogError("无法解析响应: {Body}", Truncate(data, 500));
        throw AIProviderException.InvalidResponse($"无法解析 {_providerType.DisplayName()} 响应");
    }

    /// <summary>从响应中提取 JSON 字符串（去除 markdown 代码块包裹及前后说明）。</summary>
    private static string ExtractJson(string text)
    {
        var cleaned = text.Trim();

        // 方式 1/2：从 ``` 代码块中提取。
        var blockMatch = Regex.Match(cleaned, "```(?:json)?\\s*\\n(.*)\\n```", RegexOptions.Singleline);
        if (blockMatch.Success)
        {
            return blockMatch.Groups[1].Value.Trim();
        }

        // 方式 3：去掉首尾 ``` 标记。
        if (cleaned.StartsWith("```json", StringComparison.Ordinal))
        {
            cleaned = cleaned[7..];
        }
        else if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            cleaned = cleaned[3..];
        }
        if (cleaned.EndsWith("```", StringComparison.Ordinal))
        {
            cleaned = cleaned[..^3];
        }

        // 方式 4：截取第一个 { 到最后一个 }（对象兜底）。
        cleaned = cleaned.Trim();
        if (!cleaned.StartsWith('{') && !cleaned.StartsWith('['))
        {
            var objFirst = cleaned.IndexOf('{');
            var objLast = cleaned.LastIndexOf('}');
            var arrFirst = cleaned.IndexOf('[');
            var arrLast = cleaned.LastIndexOf(']');

            // 优先选先出现的（哪个是开头哪个是真的 JSON）；若都有，对象/数组各取一段。
            var preferObj = objFirst >= 0 && (arrFirst < 0 || objFirst < arrFirst);
            if (preferObj && objLast > objFirst)
            {
                cleaned = cleaned[objFirst..(objLast + 1)];
            }
            else if (arrFirst >= 0 && arrLast > arrFirst)
            {
                cleaned = cleaned[arrFirst..(arrLast + 1)];
            }
        }

        return cleaned.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>脱敏日志 / 错误信息中可能回显的密钥（QW-14）：Bearer token、sk- 开头的 key。</summary>
    private static string Redact(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }
        s = System.Text.RegularExpressions.Regex.Replace(s, @"Bearer\s+[A-Za-z0-9\-\._]+", "Bearer ***");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"sk-[A-Za-z0-9\-]{6,}", "sk-***");
        return s;
    }
}
