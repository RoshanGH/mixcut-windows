using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MixCut.Infrastructure;
using MixCut.Models;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.ASR;

/// <summary>
/// ASR 语音识别服务（whisper.cpp）。对应 macOS 版 ASRService。
/// Windows 版仅支持 whisper.cpp（内置 <c>bin\whisper-cli.exe</c>）。注册为单例服务。
/// </summary>
public sealed class ASRService
{
    private static readonly string[] ModelNames =
        { "ggml-large-v3-turbo", "ggml-medium", "ggml-small", "ggml-base" };

    private static readonly Dictionary<string, string[]> ModelDownloadUrls = new()
    {
        ["ggml-large-v3-turbo"] = new[]
        {
            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin",
        },
        ["ggml-small"] = new[]
        {
            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
        },
        ["ggml-base"] = new[]
        {
            "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        },
    };

    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<ASRService> _logger;

    public ASRService(FFmpegRunner ffmpeg, ILogger<ASRService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>
    /// 全局 whisper-cli 串行信号量。同一时刻只允许一个 whisper-cli 进程运行，
    /// 避免在 4-8 核 CPU 上并行抢资源导致全部超时。
    /// </summary>
    private static readonly SemaphoreSlim WhisperSemaphore = new(1, 1);

    /// <summary>解析 whisper-cli stderr 中 "progress = N%" 的正则。</summary>
    private static readonly Regex ProgressRegex = new(
        @"progress\s*=\s*(\d+)\s*%", RegexOptions.Compiled);

    /// <summary>对视频进行语音识别。</summary>
    /// <param name="videoPath">视频文件路径。</param>
    /// <param name="language">识别语言，默认 zh。</param>
    /// <param name="videoDurationSec">视频时长（秒），用于动态计算 whisper 超时。0 表示未知。</param>
    /// <param name="progress">进度回调，0.0 - 1.0；ASR 内部细分音频提取/模型查找/whisper 运行 3 段。</param>
    public async Task<TranscriptionResult> TranscribeAsync(
        string videoPath, string language = "zh",
        double videoDurationSec = 0,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!BundledBinaries.WhisperAvailable)
        {
            _logger.LogError("Whisper 未找到，语音识别跳过");
            throw AsrException.WhisperNotFound();
        }

        // AVX2 兜底：内置 whisper-cli 用 AVX2 指令集编译，老 CPU 跑会立即 SIGILL (ExitCode=-1073741795 / 0xC000001D)。
        // 启动期 EnvironmentDiagnostics 已经弹过窗，这里再硬挡一道避免真的产生外部进程崩溃 → 让上层捕获到友好异常。
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            _logger.LogError("[CpuDiag] CPU 不支持 AVX2，无法运行内置 whisper-cli；跳过 ASR");
            throw AsrException.CpuNotSupported();
        }

        // Step 1: 提取音频为 16kHz mono WAV。
        progress?.Report(0.02);
        var tempWav = Path.Combine(FileHelper.TempDirectory, $"audio_{Guid.NewGuid():N}.wav");
        try
        {
            await _ffmpeg.ExtractAudioAsync(videoPath, tempWav, cancellationToken: cancellationToken);
            progress?.Report(0.10);

            // Step 2: 查找/下载模型。
            var modelPath = FindModel();
            if (modelPath is null)
            {
                _logger.LogInformation("Whisper 模型未找到，开始自动下载...");
                modelPath = await DownloadModelIfNeededAsync(cancellationToken: cancellationToken);
            }
            progress?.Report(0.12);

            // Step 3: 运行 whisper.cpp（串行 + 重试 1 次）。
            // 串行：避免多个 whisper-cli 抢 CPU 导致全部超时。
            // 重试：whisper.cpp 偶发卡顿/段错误，重试一次往往能成功。
            _logger.LogInformation("等待 whisper 串行槽位（视频 {File}）...", Path.GetFileName(videoPath));
            await WhisperSemaphore.WaitAsync(cancellationToken);
            try
            {
                // ASR 阶段进度区间 0.12 → 1.0，留给 whisper-cli 内部进度。
                var whisperProgress = new Progress<double>(p =>
                    progress?.Report(0.12 + p * 0.88));

                TranscriptionResult result;
                try
                {
                    result = await RunWhisperCppAsync(
                        modelPath, tempWav, language,
                        videoDurationSec, whisperProgress, cancellationToken);
                }
                catch (WhisperRetryableException ex)
                {
                    _logger.LogWarning("Whisper 首次失败（{Reason}），重试一次", ex.Message);
                    progress?.Report(0.15);
                    result = await RunWhisperCppAsync(
                        modelPath, tempWav, language,
                        videoDurationSec, whisperProgress, cancellationToken);
                }
                progress?.Report(1.0);

                // 健康指标日志（对齐 Mac transcribe 末尾埋点）：
                // 输出原生 segments / words / 最终句数 + ASR 异常告警（单 segment + >8s）。
                var rawCount = result.RawSentences.Count;
                var finalCount = result.Sentences.Count;
                _logger.LogInformation(
                    "ASR 完成: 原生 {Raw} segments / words {Words} / 最终 {Final} 句 / duration {Dur:F1}s",
                    rawCount, result.Words.Count, finalCount, videoDurationSec);
                if (rawCount == 1 && videoDurationSec > 8.0)
                {
                    _logger.LogWarning(
                        "⚠ ASR 输出粒度异常：单段长视频（{Dur:F1}s），用户可在分镜库 → 重做 ASR 重新识别",
                        videoDurationSec);
                }
                return result;
            }
            finally
            {
                WhisperSemaphore.Release();
            }
        }
        finally
        {
            TryDelete(tempWav);
        }
    }

    /// <summary>whisper-cli 临时性失败（超时 / 进程崩溃）—— 触发上层重试。</summary>
    private sealed class WhisperRetryableException : Exception
    {
        public WhisperRetryableException(string message) : base(message) { }
    }

    private async Task<TranscriptionResult> RunWhisperCppAsync(
        string modelPath, string audioPath, string language,
        double videoDurationSec, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var outputPrefix = Path.Combine(FileHelper.TempDirectory, $"whisper_{Guid.NewGuid():N}");
        var jsonPath = outputPrefix + ".json";

        // 动态超时：large-v3-turbo 在 8 核 CPU 大约 0.5x 实时，给 4x 余量。
        // 起跳 5 分钟，最长 30 分钟（防 1h+ 长视频锁死）。
        // P0-27：duration ≤ 0 表示元数据提取失败（损坏/异常容器），按 duration*4 会得到 5min 下限，
        // 大文件必然误超时。此时无从估算真实时长，直接给 30min 上限保守值，宁可多等也别冤杀。
        var dynTimeout = videoDurationSec > 0
            ? TimeSpan.FromSeconds(Math.Clamp(videoDurationSec * 4, 300, 1800))
            : TimeSpan.FromSeconds(1800);

        // 留 2 个逻辑核给系统 + UI（之前吃满所有核导致风扇狂转）。
        // 8 核 → 6 线程；4 核 → 2 线程；2 核 → 2 线程。
        var threads = Math.Max(2, Environment.ProcessorCount - 2);

        var psi = new ProcessStartInfo
        {
            FileName = BundledBinaries.WhisperCli,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[]
                 {
                     "-m", modelPath, "-f", audioPath, "-l", language,
                     "-t", threads.ToString(CultureInfo.InvariantCulture),
                     "--print-progress",
                     "--output-json-full", "-of", outputPrefix,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        ChildProcessTracker.AddProcess(process);
        // 把 whisper 进程优先级降到 BelowNormal，让 UI / 系统抢占（用户不会被风扇响吓到）
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* ignore */ }

        // stdout 直接丢弃（whisper 把识别文本也写 stdout，我们用 JSON 文件结果即可）。
        var drainOut = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);

        // stderr 行式读取，解析 "progress = N%" 报告给上层。
        var drainErr = Task.Run(async () =>
        {
            try
            {
                using var reader = process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    var m = ProgressRegex.Match(line);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
                    {
                        progress?.Report(Math.Clamp(pct / 100.0, 0.0, 1.0));
                    }
                }
            }
            catch (Exception)
            {
                // stderr 读取失败不影响主流程
            }
        }, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(dynTimeout);
        _logger.LogInformation("启动 whisper-cli: 视频 {Dur}s, 超时 {Timeout}s, threads={T}",
            videoDurationSec, dynTimeout.TotalSeconds, threads);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            _logger.LogError("Whisper 进程超时（{Min:F1} 分钟），强制终止", dynTimeout.TotalMinutes);
            throw new WhisperRetryableException($"超时 {dynTimeout.TotalMinutes:F1} 分钟");
        }
        await Task.WhenAll(drainOut, drainErr);

        if (process.ExitCode != 0)
        {
            _logger.LogError("Whisper 进程异常退出: ExitCode={Code}", process.ExitCode);
            throw new WhisperRetryableException($"进程异常退出 ExitCode={process.ExitCode}");
        }

        try
        {
            if (!File.Exists(jsonPath))
            {
                _logger.LogWarning("Whisper 未产出 JSON 文件（{Path}），视为空结果", Path.GetFileName(jsonPath));
                return TranscriptionResult.Empty(language);
            }
            var raw = await File.ReadAllTextAsync(jsonPath, Encoding.UTF8, cancellationToken);
            return ParseWhisperCppOutput(raw, language);
        }
        finally
        {
            TryDelete(jsonPath);
        }
    }

    // ---- whisper.cpp JSON 解析 ----

    private TranscriptionResult ParseWhisperCppOutput(string raw, string language)
    {
        // whisper.cpp 输出可能含 UTF-8 解码失败残留的 U+FFFD，先清洗。
        var cleaned = raw.Replace("�", string.Empty);

        WhisperCppOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<WhisperCppOutput>(cleaned,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogError("解析 whisper.cpp 输出失败: {Message}", ex.Message);
            return TranscriptionResult.Empty(language);
        }

        if (output?.Transcription is null)
        {
            return TranscriptionResult.Empty(language);
        }

        var words = new List<AsrWord>();
        foreach (var segment in output.Transcription)
        {
            if (segment.Tokens is null)
            {
                continue;
            }
            foreach (var token in segment.Tokens)
            {
                var text = CleanWhisperToken(token.Text);
                if (text.Length == 0 || token.Timestamps is null)
                {
                    continue;
                }
                var start = ParseTimestamp(token.Timestamps.From);
                var end = ParseTimestamp(token.Timestamps.To);
                if (end > start)
                {
                    words.Add(new AsrWord(text, start, end));
                }
            }
        }

        var rawSentences = new List<AsrSentence>();
        foreach (var segment in output.Transcription)
        {
            var text = CleanWhisperText(segment.Text);
            if (text.Length == 0)
            {
                continue;
            }
            rawSentences.Add(new AsrSentence(
                text,
                ParseTimestamp(segment.Timestamps?.From ?? "00:00:00.000"),
                ParseTimestamp(segment.Timestamps?.To ?? "00:00:00.000")));
        }

        var fullText = string.Concat(rawSentences.Select(s => s.Text));
        var duration = words.Count > 0 ? words[^1].End : 0;
        return new TranscriptionResult
        {
            Text = fullText,
            Words = words,
            RawSentences = rawSentences,
            Language = language,
            Duration = duration,
        };
    }

    /// <summary>清洗 whisper.cpp 单个 token：过滤特殊标记、空白、U+FFFD。</summary>
    private static string CleanWhisperToken(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('[') || trimmed.StartsWith("<|", StringComparison.Ordinal))
        {
            return string.Empty;
        }
        var cleaned = trimmed.Replace("�", string.Empty);
        return cleaned.Trim().Length == 0 ? string.Empty : cleaned;
    }

    /// <summary>清洗 whisper.cpp segment 级文本：trim、移除 U+FFFD、合并连续空格。</summary>
    private static string CleanWhisperText(string raw)
    {
        var text = raw.Trim().Replace("�", string.Empty);
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ");
        }
        return text.Trim();
    }

    /// <summary>解析时间戳 "00:00:01.234" 或 "00:00:01,234" → 秒。</summary>
    private static double ParseTimestamp(string str)
    {
        var parts = str.Replace(',', '.').Split(':');
        if (parts.Length != 3)
        {
            return 0;
        }
        double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h);
        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m);
        double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s);
        return h * 3600 + m * 60 + s;
    }

    // ---- 模型查找与下载 ----

    /// <summary>查找 whisper.cpp 模型文件（优先大模型，准确率更高）。</summary>
    public string? FindModel()
    {
        foreach (var name in ModelNames)
        {
            var bundled = Path.Combine(BundledBinaries.BinDirectory, name + ".bin");
            if (File.Exists(bundled))
            {
                return bundled;
            }
            var cached = Path.Combine(AppPaths.WhisperModelsDirectory, name + ".bin");
            if (File.Exists(cached))
            {
                return cached;
            }
        }
        return null;
    }

    /// <summary>模型是否可用。</summary>
    public bool IsModelAvailable() => FindModel() is not null;

    /// <summary>下载 whisper 模型到本地缓存目录（自动尝试多个镜像源）。</summary>
    public async Task<string> DownloadModelIfNeededAsync(
        string modelName = "ggml-large-v3-turbo",
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (FindModel() is { } existing)
        {
            return existing;
        }

        if (!ModelDownloadUrls.TryGetValue(modelName, out var urls) || urls.Length == 0)
        {
            throw AsrException.ModelNotFound();
        }

        var destPath = Path.Combine(AppPaths.WhisperModelsDirectory, modelName + ".bin");
        var tempPath = destPath + ".download";

        // 用 SocketsHttpHandler 关闭超时，让大文件长连接不被 .NET 自动断。
        using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(20) };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        const int maxAttemptsPerUrl = 3;
        Exception? lastError = null;

        foreach (var url in urls)
        {
            var source = url.Contains("hf-mirror", StringComparison.Ordinal) ? "国内镜像" : "HuggingFace";

            for (var attempt = 1; attempt <= maxAttemptsPerUrl; attempt++)
            {
                _logger.LogInformation("开始下载 Whisper 模型: {Model}（{Source}，第 {Attempt}/{Max} 次）",
                    modelName, source, attempt, maxAttemptsPerUrl);
                try
                {
                    var resumeFrom = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (resumeFrom > 0)
                    {
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
                    }

                    onProgress?.Invoke(new DownloadProgress(
                        resumeFrom > 0 ? 0.05 : 0.02, resumeFrom, 0));

                    using var response = await http.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    // 416 表示 Range 不可满足（文件已完整）→ 当作完成。
                    if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable
                        && resumeFrom > 0)
                    {
                        _logger.LogInformation("服务端返回 416，认为下载已完成");
                        goto Finalize;
                    }
                    response.EnsureSuccessStatusCode();

                    var isResume = response.StatusCode == System.Net.HttpStatusCode.PartialContent
                                   && resumeFrom > 0;
                    long total;
                    if (isResume && response.Content.Headers.ContentRange?.Length is long rangeTotal)
                    {
                        total = rangeTotal;
                    }
                    else
                    {
                        var len = response.Content.Headers.ContentLength ?? 0;
                        total = isResume ? len + resumeFrom : len;
                        if (!isResume)
                        {
                            // 服务端不支持续传，从 0 开始重下。
                            resumeFrom = 0;
                        }
                    }

                    await using (var src = await response.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var dst = new FileStream(tempPath,
                        isResume ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                    {
                        var buffer = new byte[1 << 20];
                        long received = isResume ? resumeFrom : 0;
                        int read;
                        var lastReport = 0L;
                        while ((read = await src.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            received += read;
                            if (received - lastReport >= 512 * 1024 || received == total)
                            {
                                lastReport = received;
                                var pct = total > 0 ? received / (double)total : 0;
                                onProgress?.Invoke(new DownloadProgress(pct, received, total));
                            }
                        }
                    }

                Finalize:
                    if (File.Exists(destPath))
                    {
                        File.Delete(destPath);
                    }
                    File.Move(tempPath, destPath);
                    onProgress?.Invoke(new DownloadProgress(1.0,
                        new FileInfo(destPath).Length, new FileInfo(destPath).Length));
                    _logger.LogInformation("Whisper 模型下载完成: {Model}", modelName);
                    return destPath;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.LogError("从 {Source} 下载失败（第 {Attempt} 次）: {Message}",
                        source, attempt, ex.Message);
                    if (attempt < maxAttemptsPerUrl)
                    {
                        var delayMs = 2000 * attempt;
                        _logger.LogInformation("{Ms} 毫秒后重试同一源（已下载部分会续传）...", delayMs);
                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }
            _logger.LogInformation("源 {Source} 3 次都失败，尝试下一个源...", source);
        }

        throw new AsrException(AsrErrorKind.ModelNotFound,
            lastError is null
                ? "Whisper 模型文件未找到，所有下载源均失败"
                : "Whisper 模型下载失败：" + lastError.Message
                  + "\n临时文件保留在 " + Path.GetFileName(tempPath) + "，下次重试会自动续传");
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("删除临时文件失败 {Path}: {Message}", path, ex.Message);
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    // ---- whisper.cpp --output-json-full DTO ----

    private sealed class WhisperCppOutput
    {
        [JsonPropertyName("transcription")]
        public List<Segment>? Transcription { get; set; }

        public sealed class Segment
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("timestamps")]
            public Stamps? Timestamps { get; set; }

            [JsonPropertyName("tokens")]
            public List<Token>? Tokens { get; set; }
        }

        public sealed class Token
        {
            [JsonPropertyName("text")]
            public string Text { get; set; } = string.Empty;

            [JsonPropertyName("timestamps")]
            public Stamps? Timestamps { get; set; }
        }

        public sealed class Stamps
        {
            [JsonPropertyName("from")]
            public string From { get; set; } = "00:00:00.000";

            [JsonPropertyName("to")]
            public string To { get; set; } = "00:00:00.000";
        }
    }
}
