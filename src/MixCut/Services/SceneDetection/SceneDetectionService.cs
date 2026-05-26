using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MixCut.Services.VideoProcessing;

namespace MixCut.Services.SceneDetection;

/// <summary>
/// 视频本地分析服务（场景检测 + 静音检测 + I-frame 提取）。
/// 对应 macOS 版 SceneDetectionService。注册为单例服务。
/// </summary>
public sealed class SceneDetectionService
{
    private static readonly Regex PtsTimeRegex = new(@"pts_time:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex SceneScoreRegex = new(@"scene_score=([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex SilenceStartRegex = new(@"silence_start:\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex SilenceEndRegex = new(@"silence_end:\s*([\d.]+)", RegexOptions.Compiled);

    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<SceneDetectionService> _logger;

    public SceneDetectionService(FFmpegRunner ffmpeg, ILogger<SceneDetectionService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>使用 FFmpeg scene filter 检测镜头切换点。scene filter 是 software，不加 hwaccel。</summary>
    public async Task<IReadOnlyList<SceneBoundary>> DetectScenesAsync(
        string videoPath, double threshold = 0.3, CancellationToken cancellationToken = default)
    {
        var args = new[]
        {
            "-i", videoPath,
            "-vf", $"select='gt(scene,{threshold.ToString(CultureInfo.InvariantCulture)})',showinfo",
            "-f", "null", "-",
        };
        var stderr = await _ffmpeg.RunForStderrAsync(args, cancellationToken);
        return ParseSceneBoundaries(stderr);
    }

    /// <summary>使用 FFmpeg silencedetect 检测静音/停顿段。</summary>
    public async Task<IReadOnlyList<SilencePeriod>> DetectSilenceAsync(
        string videoPath, string noiseThreshold = "-30dB", double minDuration = 0.3,
        CancellationToken cancellationToken = default)
    {
        var args = new[]
        {
            "-i", videoPath,
            "-af", $"silencedetect=noise={noiseThreshold}:d={minDuration.ToString(CultureInfo.InvariantCulture)}",
            "-f", "null", "-",
        };
        var stderr = await _ffmpeg.RunForStderrAsync(args, cancellationToken);
        return ParseSilencePeriods(stderr);
    }

    /// <summary>提取视频中所有 I-frame 的精确时间戳（ffprobe 不解码，速度快）。</summary>
    public async Task<IReadOnlyList<double>> ExtractIFramesAsync(
        string videoPath, CancellationToken cancellationToken = default)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey",
            "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", videoPath,
        };
        var stdout = await _ffmpeg.RunProbeAsync(args, timeoutSeconds: 60, cancellationToken);
        return ParseIFrameTimes(stdout);
    }

    /// <summary>
    /// 并行执行所有本地视频分析（场景检测 + 静音检测 + I-frame 提取）。
    /// 4 分钟硬超时：即使内部某步挂起也会被取消。
    /// </summary>
    public async Task<VideoLocalAnalysis> AnalyzeLocallyAsync(
        string videoPath, double duration, double fps, double sceneThreshold = 0.3,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(4));
        var ct = timeoutCts.Token;

        // 粗粒度进度：三步并行，每完成一步贡献 1/3。
        var done = 0;
        var lockObj = new object();
        void Tick()
        {
            int now;
            lock (lockObj) { now = ++done; }
            progress?.Report(now / 3.0);
        }

        try
        {
            // 三步并行执行（互相独立），每步独立容错。
            var scenesTask = SafeAsync(
                () => DetectScenesAsync(videoPath, sceneThreshold, ct),
                Array.Empty<SceneBoundary>(), "场景检测", Tick);
            var silencesTask = SafeAsync(
                () => DetectSilenceAsync(videoPath, cancellationToken: ct),
                Array.Empty<SilencePeriod>(), "静音检测", Tick);
            var iframesTask = SafeAsync(
                () => ExtractIFramesAsync(videoPath, ct),
                Array.Empty<double>(), "I-frame 提取", Tick);

            await Task.WhenAll(scenesTask, silencesTask, iframesTask);

            return new VideoLocalAnalysis(
                await scenesTask, await silencesTask, await iframesTask, duration, fps);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            throw SceneDetectionException.Timeout();
        }
    }

    private async Task<IReadOnlyList<T>> SafeAsync<T>(
        Func<Task<IReadOnlyList<T>>> work, IReadOnlyList<T> fallback, string label,
        Action? onDone = null)
    {
        try
        {
            var result = await work();
            _logger.LogInformation("{Label}完成: {Count} 项", label, result.Count);
            onDone?.Invoke();
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("{Label}失败: {Message}", label, ex.Message);
            onDone?.Invoke();
            return fallback;
        }
    }

    // ---- 解析方法 ----

    private static IReadOnlyList<SceneBoundary> ParseSceneBoundaries(string output)
    {
        var boundaries = new List<SceneBoundary>();
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("showinfo", StringComparison.Ordinal))
            {
                continue;
            }

            var timeMatch = PtsTimeRegex.Match(line);
            if (!timeMatch.Success
                || !double.TryParse(timeMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
            {
                continue;
            }

            var confidence = 0.5;
            var scoreMatch = SceneScoreRegex.Match(line);
            if (scoreMatch.Success
                && double.TryParse(scoreMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
            {
                confidence = score;
            }
            boundaries.Add(new SceneBoundary(time, confidence));
        }
        return boundaries.OrderBy(b => b.Time).ToList();
    }

    private static IReadOnlyList<SilencePeriod> ParseSilencePeriods(string output)
    {
        var periods = new List<SilencePeriod>();
        double? currentStart = null;

        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("silencedetect", StringComparison.Ordinal))
            {
                continue;
            }

            var startMatch = SilenceStartRegex.Match(line);
            if (startMatch.Success
                && double.TryParse(startMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var start))
            {
                currentStart = start;
            }

            var endMatch = SilenceEndRegex.Match(line);
            if (endMatch.Success
                && double.TryParse(endMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
                && currentStart is { } s)
            {
                periods.Add(new SilencePeriod(s, end));
                currentStart = null;
            }
        }
        return periods.OrderBy(p => p.Start).ToList();
    }

    private static IReadOnlyList<double> ParseIFrameTimes(string stdout)
    {
        var times = new List<double>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line == "N/A")
            {
                continue;
            }
            if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                times.Add(t);
            }
        }
        return times;
    }
}
