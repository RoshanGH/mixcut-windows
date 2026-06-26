using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MixCut.Services.AI;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>
/// 阿里百炼克隆音色 TTS 合成（model=<see cref="VoiceCloneService.TargetModel"/>）。对应 mac QwenTTSClient(克隆路)。
/// HTTP POST → output.audio.url（OSS http→https）→ 下载 wav → ffprobe 测时长。复用千问 DashScope key。
/// </summary>
public sealed class CloneTtsClient
{
    private const string Endpoint =
        "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly AppSettings _settings;
    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<CloneTtsClient> _logger;

    public CloneTtsClient(AppSettings settings, FFmpegRunner ffmpeg, ILogger<CloneTtsClient> logger)
    {
        _settings = settings;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>用克隆音色合成一段台词，返回 wav 路径 + 时长。失败抛 <see cref="DubException"/>。</summary>
    public async Task<TtsResult> SynthesizeAsync(string text, string voiceId, CancellationToken ct = default)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) throw new DubException("配音台词为空");

        var key = _settings.GetApiKey(AIProviderType.Qwen);
        if (string.IsNullOrEmpty(key)) throw new DubException("尚未配置千问 API Key，请到「设置」里填写后重试");

        var body = new
        {
            model = VoiceCloneService.TargetModel,
            input = new { text = trimmed, voice = voiceId, language_type = "Chinese" },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        // 显式 UTF-8（P0 实测：默认编码会打乱中文台词 → InvalidParameter: invalid text）。
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("[DubDiag] 克隆合成失败 HTTP {Code}: {Body}", (int)resp.StatusCode, Trunc(respText));
            throw new DubException($"配音合成失败：HTTP {(int)resp.StatusCode} {Trunc(respText)}");
        }

        string? urlStr;
        using (var doc = JsonDocument.Parse(respText))
        {
            urlStr = doc.RootElement.TryGetProperty("output", out var output)
                     && output.TryGetProperty("audio", out var audio)
                     && audio.TryGetProperty("url", out var url)
                ? url.GetString()
                : null;
        }
        if (string.IsNullOrEmpty(urlStr)) throw new DubException("配音合成未返回音频 URL（响应格式异常）");

        // DashScope 返回的音频 URL 多为 http(OSS)，升级 https（OSS V1 预签名不含 scheme，不影响签名）。
        if (urlStr.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            urlStr = "https://" + urlStr["http://".Length..];
        }

        var dest = Path.Combine(Path.GetTempPath(), $"mixcut-tts-{Guid.NewGuid():N}.wav");
        await using (var src = await Http.GetStreamAsync(urlStr, ct))
        await using (var fs = File.Create(dest))
        {
            await src.CopyToAsync(fs, ct);
        }

        var dur = await _ffmpeg.ProbeDurationAsync(dest, ct);
        return new TtsResult(dest, dur);
    }

    /// <summary>
    /// 克隆 TTS 偶发「续读参考内容」跑飞（音频比台词长、混进下一段）。干净版必然最短：
    /// 重试最多 3 次取最短；某次时长 ≤ 字数×0.28（视为干净）即提前返回。对应 mac synthesizeCloneRobust。
    /// </summary>
    public async Task<TtsResult> SynthesizeRobustAsync(string text, string voiceId, CancellationToken ct = default)
    {
        const int maxTries = 3;
        var cleanLimit = Math.Max(1, text.Length) * 0.28;
        TtsResult? best = null;
        for (var i = 0; i < maxTries; i++)
        {
            var r = await SynthesizeAsync(text, voiceId, ct);
            if (best is null || r.RawDuration < best.RawDuration) best = r;
            if (r.RawDuration <= cleanLimit) break; // 看着干净，直接用
        }
        return best!;
    }

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300];
}
