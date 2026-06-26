using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MixCut.Services.AI;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>
/// 阿里百炼声音克隆：拿一段参考音频(mp3) 注册出一个克隆音色 voice_id。对应 mac VoiceCloneService。
/// 合成时用 <see cref="CloneTtsClient"/>(model=<see cref="TargetModel"/>) + 该 voice_id。复用千问 DashScope key。
/// </summary>
public sealed class VoiceCloneService
{
    /// <summary>克隆音色绑定的合成模型；注册与合成必须用同一个。</summary>
    public const string TargetModel = "qwen3-tts-vc-2026-01-22";

    private const string Endpoint = "https://dashscope.aliyuncs.com/api/v1/services/audio/tts/customization";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly AppSettings _settings;
    private readonly ILogger<VoiceCloneService> _logger;

    public VoiceCloneService(AppSettings settings, ILogger<VoiceCloneService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>用参考音频注册克隆音色，返回 voice_id。失败抛 <see cref="DubException"/>。</summary>
    public async Task<string> EnrollAsync(string referenceAudioPath, string preferredName, CancellationToken ct = default)
    {
        var key = _settings.GetApiKey(AIProviderType.Qwen);
        if (string.IsNullOrEmpty(key))
        {
            throw new DubException("尚未配置千问 API Key，请到「设置」里填写后重试");
        }

        var bytes = await File.ReadAllBytesAsync(referenceAudioPath, ct);
        var b64 = Convert.ToBase64String(bytes);

        var body = new
        {
            model = "qwen-voice-enrollment",
            input = new
            {
                action = "create",
                target_model = TargetModel,
                preferred_name = SanitizedName(preferredName),
                audio = new { data = $"data:audio/mpeg;base64,{b64}" },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        // 显式 UTF-8（P0 实测：默认编码会打乱中文，但此处无中文；统一规范防未来踩坑）。
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("[DubDiag] 声音克隆注册失败 HTTP {Code}: {Body}", (int)resp.StatusCode, Trunc(text));
            throw new DubException($"声音克隆失败：HTTP {(int)resp.StatusCode} {Trunc(text)}");
        }

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("output", out var output)
            && output.TryGetProperty("voice", out var voice)
            && voice.GetString() is { Length: > 0 } voiceId)
        {
            _logger.LogInformation("[DubDiag] 声音克隆成功 voiceId={VoiceId}", voiceId);
            return voiceId;
        }

        _logger.LogError("[DubDiag] 声音克隆未返回 output.voice: {Body}", Trunc(text));
        throw new DubException("声音克隆未返回音色 ID（响应格式异常）");
    }

    /// <summary>preferred_name 仅允许小写字母/数字，长度受限；做个稳妥清洗。</summary>
    private static string SanitizedName(string raw)
    {
        var lowered = new string(raw.ToLowerInvariant().Where(char.IsLetterOrDigit).Take(20).ToArray());
        return string.IsNullOrEmpty(lowered) ? "mixcut" : lowered;
    }

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300];
}
