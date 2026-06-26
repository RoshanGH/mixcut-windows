using System.IO;
using Microsoft.Extensions.Logging;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>落库后的配音产物：m4a 路径 + 对齐方案（导出时复现）。对应 mac FinalizedDub。</summary>
public sealed record FinalizedDub(string M4aPath, AlignmentPlan Plan);

/// <summary>
/// 把 TTS 原始 wav 按对齐方案做 atempo 变速 + 转 AAC/m4a，写到全局 Dubs 目录。对应 mac DubAudioFinalizer。
/// 定格补帧 / 末尾静音不在此处烧入（freezePadFrames 恒 0；trailingSilence 随 plan 落库，导出阶段处理）。
/// </summary>
public sealed class DubAudioFinalizer
{
    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<DubAudioFinalizer> _logger;

    public DubAudioFinalizer(FFmpegRunner ffmpeg, ILogger<DubAudioFinalizer> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public async Task<FinalizedDub> FinalizeAsync(
        TtsResult tts, double targetDuration, double fps,
        string videoHash, Guid segmentId, string voiceId, int textVariantIndex,
        CancellationToken ct = default)
    {
        var plan = AudioAligner.Plan(targetDuration, tts.RawDuration, fps);

        var dir = AppPaths.DubAudioDirectory;
        var dest = Path.Combine(dir,
            $"{videoHash}_{segmentId:N}_{Sanitize(voiceId)}_{textVariantIndex}.m4a");
        if (File.Exists(dest))
        {
            try { File.Delete(dest); } catch { /* 占用时忽略，下方 -y 覆盖 */ }
        }

        // atempo 单段只接受 0.5~2.0；>2.0 链式拆成多段相乘，保证多少字都能压进画面、绝不超过。
        var filters = AudioAligner.AtempoChain(plan.AtempoFactor);

        var args = new List<string> { "-y", "-i", tts.WavPath };
        if (filters.Count > 0)
        {
            args.Add("-filter:a");
            args.Add(string.Join(",", filters));
        }
        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add("128k");
        args.Add(dest);

        await _ffmpeg.RunAsync(args, timeout: TimeSpan.FromMinutes(2), cancellationToken: ct);
        _logger.LogInformation("[DubDiag] 配音对齐完成 seg={Seg} atempo={Atempo:F3} tail={Tail:F2}s → {Path}",
            segmentId, plan.AtempoFactor, plan.TrailingSilence, dest);

        return new FinalizedDub(dest, plan);
    }

    private static string Sanitize(string s) =>
        new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
}
