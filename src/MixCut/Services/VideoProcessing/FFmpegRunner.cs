using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MixCut.Infrastructure;
using MixCut.Utilities;

namespace MixCut.Services.VideoProcessing;

/// <summary>以帧为真值的视频片段。StartFrame inclusive，EndFrame exclusive。</summary>
public sealed record FrameClip(string Path, int StartFrame, int EndFrame, double Fps)
{
    public int FrameCount => Math.Max(0, EndFrame - StartFrame);
    public double StartSeconds => FrameTime.FrameToSeconds(StartFrame, Fps);
    public double EndSeconds => FrameTime.FrameToSeconds(EndFrame, Fps);
    public double Duration => FrameTime.FrameToSeconds(FrameCount, Fps);
}

/// <summary>
/// FFmpeg / ffprobe 命令执行封装。对应 macOS 版 FFmpegRunner（actor）。
/// Windows 版用 <see cref="Process"/> 调用内置 <c>bin\ffmpeg.exe</c>。注册为单例服务。
/// </summary>
public sealed class FFmpegRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);
    private readonly ILogger<FFmpegRunner> _logger;

    public FFmpegRunner(ILogger<FFmpegRunner> logger)
    {
        _logger = logger;
    }

    // ---- 核心执行 ----

    /// <summary>执行 FFmpeg 命令并返回 stdout 原始字节。</summary>
    /// <param name="timeout">
    /// 自定义超时；null 用 <see cref="DefaultTimeout"/>（3 分钟）。
    /// 导出拼接（4K / CPU 软编码）远超 3 分钟，必须由 ConcatAsync 按数据规模动态算后传入，
    /// 严禁写死（CLAUDE.md §10：写死的超时是反模式）。
    /// </param>
    public async Task<byte[]> RunAsync(
        IReadOnlyList<string> arguments,
        double? totalDuration = null,
        Action<FFmpegProgress>? onProgress = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!BundledBinaries.FfmpegAvailable)
        {
            throw FFmpegException.BinaryNotFound();
        }

        Action<string>? stderrHandler = onProgress is null
            ? null
            : line =>
            {
                var progress = ParseProgress(line, totalDuration);
                if (progress is { } p)
                {
                    onProgress(p);
                }
            };

        // 完整 ffmpeg 命令日志（每个参数引号包裹）便于复现失败 case
        Serilog.Log.Information("[ffmpeg] {Cmd}",
            string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        var (exitCode, stdout, stderr) = await ExecuteAsync(
            BundledBinaries.Ffmpeg, arguments, captureStdout: true,
            stderrHandler, timeout ?? DefaultTimeout, cancellationToken);

        if (exitCode != 0)
        {
            // 完整命令 + 完整 stderr 写日志（异常只带 200 字符尾巴，真因常在上面几行 —— 比如
            // nvenc 的「driver does not support」「No capable devices」等关键行。诊断日志靠这个）。
            Serilog.Log.Error("[ffmpeg-fail] exit={ExitCode}\n命令: {Cmd}\nstderr:\n{Stderr}",
                exitCode,
                string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                stderr);
            throw FFmpegException.ExecutionFailed(exitCode, stderr);
        }
        return stdout;
    }

    /// <summary>执行 FFmpeg 命令并返回 stderr（用于元数据、场景检测等）。</summary>
    public async Task<string> RunForStderrAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!BundledBinaries.FfmpegAvailable)
        {
            throw FFmpegException.BinaryNotFound();
        }

        var (_, _, stderr) = await ExecuteAsync(
            BundledBinaries.Ffmpeg, arguments, captureStdout: false,
            stderrHandler: null, DefaultTimeout, cancellationToken);
        return stderr;
    }

    /// <summary>
    /// 拿视频实际 duration（秒）。失败返回 0；用于 CutSegmentAsync 防御越界。
    /// 缓存：同一个 videoPath 多次切片时只 probe 一次。
    /// </summary>
    private readonly Dictionary<string, double> _durationCache = new();
    private readonly System.Threading.SemaphoreSlim _durationCacheLock = new(1, 1);

    public async Task<double> ProbeDurationAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!BundledBinaries.FfprobeAvailable) return 0;
        await _durationCacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_durationCache.TryGetValue(videoPath, out var cached)) return cached;
        }
        finally { _durationCacheLock.Release(); }

        try
        {
            var args = new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                videoPath,
            };
            var stdout = await RunProbeAsync(args, timeoutSeconds: 15, cancellationToken);
            if (double.TryParse(stdout.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
            {
                await _durationCacheLock.WaitAsync(cancellationToken);
                try
                {
                    // 防无界增长：单例长生命周期，缓存超上限就整体清空（时长 probe 很快，重建成本低）。
                    if (_durationCache.Count >= 512) { _durationCache.Clear(); }
                    _durationCache[videoPath] = d;
                }
                finally { _durationCacheLock.Release(); }
                return d;
            }
        }
        catch
        {
            // probe 失败不阻塞 export
        }
        return 0;
    }

    /// <summary>执行 ffprobe 并返回 stdout 文本（轻量探测，对长视频也很快）。</summary>
    public async Task<string> RunProbeAsync(
        IReadOnlyList<string> arguments, int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (!BundledBinaries.FfprobeAvailable)
        {
            throw FFmpegException.BinaryNotFound();
        }

        var (_, stdout, _) = await ExecuteAsync(
            BundledBinaries.Ffprobe, arguments, captureStdout: true,
            stderrHandler: null, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        return Encoding.UTF8.GetString(stdout);
    }

    private async Task<(int ExitCode, byte[] Stdout, string Stderr)> ExecuteAsync(
        string exePath, IReadOnlyList<string> arguments, bool captureStdout,
        Action<string>? stderrHandler, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        // 降优先级，避免 ffmpeg 吃满 CPU 让 UI 卡顿 + 风扇狂转
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* ignore */ }
        // 挂到 Job Object，确保 MixCut 退出时所有 ffmpeg 子进程一起被 OS 终止，杜绝孤儿。
        Infrastructure.ChildProcessTracker.AddProcess(process);

        // stdout 按字节读取（可能是二进制）。
        var stdoutTask = captureStdout
            ? ReadAllBytesAsync(process.StandardOutput.BaseStream)
            : DiscardAsync(process.StandardOutput.BaseStream);

        // stderr 按行读取，逐行回调（FFmpeg 进度行以 \r 分隔）。
        var stderrTask = ReadStderrAsync(process.StandardError, stderrHandler);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            ForceKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("FFmpeg 进程因取消被终止");
                // 抛标准 OperationCanceledException（而非 FFmpegException.Cancelled）—— 取消必须用标准
                // 取消异常类型，否则上层 catch(OperationCanceledException)（如 ExportService 取消分支）
                // 接不住，会把「用户取消」误判为「硬件编码失败」触发不必要的 CPU 降级 / 误报导出失败。
                throw new OperationCanceledException(cancellationToken);
            }
            _logger.LogError("FFmpeg 进程超时（{Minutes} 分钟），强制终止", timeout.TotalMinutes);
            throw FFmpegException.ExecutionFailed(-1, "进程超时");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> DiscardAsync(Stream stream)
    {
        await stream.CopyToAsync(Stream.Null);
        return Array.Empty<byte>();
    }

    private static async Task<string> ReadStderrAsync(StreamReader reader, Action<string>? handler)
    {
        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            sb.Append(line).Append('\n');
            // 纵深防御：进度回调（含 ParseProgress 解析、外部 handler）抛异常不应中断整个 stderr
            // 读取任务，否则一行畸形进度会让 await stderrTask 重抛、把整个导出/分析误报为失败。
            try
            {
                handler?.Invoke(line);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[FFmpeg] stderr handler 异常已忽略，不中断读取");
            }
        }
        return sb.ToString();
    }

    private void ForceKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("终止 FFmpeg 进程失败: {Message}", ex.Message);
        }
    }

    // ---- 便捷方法 ----

    /// <summary>提取音频为 16kHz mono WAV（用于 ASR）。</summary>
    public Task ExtractAudioAsync(
        string videoPath, string outputPath,
        Action<FFmpegProgress>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var args = new[]
        {
            "-i", videoPath,
            "-vn", "-acodec", "pcm_s16le", "-ar", "16000", "-ac", "1",
            "-y", outputPath,
        };
        return RunAsync(args, onProgress: onProgress, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 裁切视频片段。
    /// reencode=true（默认）：trim filter + setpts 精确切片，**保证第一帧立即有画面**。
    /// 对齐 macOS v0.2.4 commit 704363c 的「第一帧黑屏」修复。
    /// reencode=false：流复制（-c copy），仅在关键帧对齐场景使用。
    /// </summary>
    public async Task CutSegmentAsync(
        string videoPath, double start, double end, string outputPath,
        bool reencode = true,
        CancellationToken cancellationToken = default)
    {
        // 防御：start/end 越界会让 trim filter 输出 0 包。
        var actualDuration = await ProbeDurationAsync(videoPath, cancellationToken);
        var origStart = start;
        var origEnd = end;
        if (actualDuration > 0)
        {
            var maxEnd = Math.Max(0.1, actualDuration - 0.05);
            if (end > maxEnd) end = maxEnd;
            if (start >= end) start = Math.Max(0, end - 0.5);
        }
        Serilog.Log.Information(
            "[CutSegment] video={Video} reqStart={ReqStart:F2}s reqEnd={ReqEnd:F2}s actualDur={Dur:F2}s -> useStart={Start:F2}s useEnd={End:F2}s",
            System.IO.Path.GetFileName(videoPath), origStart, origEnd, actualDuration, start, end);
        if (end - start < 0.1)
        {
            throw FFmpegException.ExecutionFailed(-1,
                $"分镜时间区间无效：start={start:F2}s end={end:F2}s (视频实际长度 {actualDuration:F2}s)");
        }

        if (!reencode)
        {
            var copyArgs = new[]
            {
                "-ss", Fmt(start), "-i", videoPath,
                "-t", Fmt(end - start),
                "-c", "copy", "-avoid_negative_ts", "make_zero",
                "-y", outputPath,
            };
            await RunAsync(copyArgs, cancellationToken: cancellationToken);
            return;
        }

        var videoFilter = $"trim=start={Fmt(start)}:end={Fmt(end)},setpts=PTS-STARTPTS";
        var audioFilter = $"atrim=start={Fmt(start)}:end={Fmt(end)},asetpts=PTS-STARTPTS";

        // 编码用硬件加速（如果可用），否则 libx264 兜底。
        var h264Codec = Infrastructure.HardwareEncoderProbe.H264Hardware ?? "libx264";
        var isHardware = Infrastructure.HardwareEncoderProbe.H264Hardware is not null;
        var args = new List<string>
        {
            "-i", videoPath,
            "-vf", videoFilter,
            "-af", audioFilter,
            "-c:v", h264Codec,
        };
        if (isHardware)
        {
            args.AddRange(new[] { "-b:v", "8000k", "-maxrate", "16000k", "-tag:v", "avc1" });
        }
        else
        {
            args.AddRange(new[] { "-crf", "20", "-preset", "fast" });
        }
        args.AddRange(new[] { "-pix_fmt", "yuv420p" });
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        args.AddRange(new[] { "-movflags", "+faststart" });
        args.AddRange(new[] { "-y", outputPath });

        await RunAsync(args, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 按整数帧精确切割。视频使用 trim=start_frame/end_frame，音频使用同一帧边界派生的秒数。
    /// </summary>
    public async Task CutSegmentFramesAsync(
        FrameClip clip, string outputPath, CancellationToken cancellationToken = default)
    {
        if (clip.Fps <= 0 || clip.EndFrame <= clip.StartFrame)
        {
            throw FFmpegException.ExecutionFailed(-1, "分镜帧区间无效");
        }

        var actualDuration = await ProbeDurationAsync(clip.Path, cancellationToken);
        var maxFrame = actualDuration > 0
            ? FrameTime.SecondsToFrame(actualDuration, clip.Fps)
            : clip.EndFrame;
        var startFrame = Math.Clamp(clip.StartFrame, 0, Math.Max(0, maxFrame - 1));
        var endFrame = Math.Clamp(clip.EndFrame, startFrame + 1, Math.Max(startFrame + 1, maxFrame));
        var startSeconds = FrameTime.FrameToSeconds(startFrame, clip.Fps);
        var endSeconds = FrameTime.FrameToSeconds(endFrame, clip.Fps);

        Serilog.Log.Information(
            "[FrameCutDiag] file={File} startFrame={StartFrame} endFrame={EndFrame} lastFrame={LastFrame} fps={Fps:F3}",
            Path.GetFileName(clip.Path), startFrame, endFrame, endFrame - 1, clip.Fps);

        var hasAudio = await ProbeHasAudioAsync(clip.Path, cancellationToken);

        List<string> BuildArgs(string codec, bool hardware)
        {
            var args = new List<string>
            {
                "-i", clip.Path,
                "-vf", $"trim=start_frame={startFrame}:end_frame={endFrame},setpts=PTS-STARTPTS",
                "-c:v", codec,
            };
            if (hasAudio)
            {
                args.AddRange(new[]
                {
                    "-af", $"atrim=start={Fmt(startSeconds)}:end={Fmt(endSeconds)},asetpts=PTS-STARTPTS",
                });
            }
            if (hardware)
            {
                args.AddRange(new[] { "-b:v", "8000k", "-maxrate", "16000k", "-tag:v", "avc1" });
            }
            else
            {
                args.AddRange(new[] { "-crf", "20", "-preset", "fast" });
            }
            args.AddRange(new[] { "-pix_fmt", "yuv420p" });
            if (hasAudio)
            {
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
            }
            args.AddRange(new[] { "-movflags", "+faststart", "-y", outputPath });
            return args;
        }

        var hardwareCodec = HardwareEncoderProbe.H264Hardware;
        try
        {
            await RunAsync(
                BuildArgs(hardwareCodec ?? "libx264", hardwareCodec is not null),
                clip.Duration, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (hardwareCodec is not null)
        {
            _logger.LogWarning(ex,
                "[FrameCutFallback] 硬件编码失败，降级 CPU 重试 file={File}", clip.Path);
            await RunAsync(
                BuildArgs("libx264", hardware: false),
                clip.Duration, cancellationToken: cancellationToken);
        }
    }

    /// <summary>生成视频缩略图。</summary>
    public Task GenerateThumbnailAsync(
        string videoPath, string outputPath, double time = 1.0,
        CancellationToken cancellationToken = default)
    {
        // 注意：不加 -hwaccel —— QSV / CUDA hardware frame 无法直接 encode 成 jpg，
        // 之前加上会让所有缩略图生成 0 帧失败（这是用户报「卡片黑屏」的真凶）。
        var args = new[]
        {
            "-ss", Fmt(time), "-i", videoPath,
            "-frames:v", "1", "-update", "1", "-q:v", "2",
            "-y", outputPath,
        };
        return RunAsync(args, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 把 HardwareEncoderProbe 选中的解码 hwaccel 注入到 args 开头（必须在 -i 之前）。
    /// 无可用硬件时不加任何参数（CPU 软件解码）。
    /// 注意：仅用于 decode-only 任务（缩略图/场景检测/I-frame）。
    /// CutSegment 用 trim filter（software 流），加 hwaccel 反而需要 hwdownload 拖累。
    /// </summary>
    private static void AddDecodeHwaccel(List<string> args)
    {
        foreach (var a in DecodeHwaccelArgs) args.Add(a);
    }

    /// <summary>外部模块（如 SceneDetectionService）也能复用此 hwaccel args。</summary>
    public static IReadOnlyList<string> DecodeHwaccelArgs
    {
        get
        {
            var hw = Infrastructure.HardwareEncoderProbe.DecodeHwaccel;
            return hw is null ? Array.Empty<string>() : new[] { "-hwaccel", hw };
        }
    }

    /// <summary>探测视频文件是否包含音频轨道。</summary>
    public async Task<bool> ProbeHasAudioAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!BundledBinaries.FfprobeAvailable)
        {
            return true; // 无 ffprobe 时默认有音频
        }

        try
        {
            var output = await RunProbeAsync(new[]
            {
                "-v", "quiet", "-select_streams", "a",
                "-show_entries", "stream=codec_type", "-of", "csv=p=0", path,
            }, timeoutSeconds: 30, cancellationToken);
            return output.Contains("audio", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return true; // 出错时默认有音频
        }
    }

    /// <summary>
    /// 拼接多个视频片段为一个 MP4（统一编码，自动处理不同分辨率/编码/帧率）。
    /// 对应 macOS 版 concat()。
    /// </summary>
    public async Task ConcatAsync(
        IReadOnlyList<FrameClip> segments,
        string outputPath,
        string? resolution = null,
        int crf = 18,
        string codec = "libx264",
        bool isHardware = false,
        int videoBitrateKbps = 8_000,
        Action<FFmpegProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            return;
        }

        // 并行探测每个输入是否有音频轨道。
        var hasAudio = new bool[segments.Count];
        await Task.WhenAll(Enumerable.Range(0, segments.Count).Select(async i =>
        {
            hasAudio[i] = await ProbeHasAudioAsync(segments[i].Path, cancellationToken);
        }));

        var args = new List<string>();

        // 每个片段作为独立输入，真正裁切在 filter_complex 中按帧执行。
        foreach (var seg in segments)
        {
            args.AddRange(new[] { "-i", seg.Path });
        }

        // 沉默音频生成器（索引 = segments.Count），替代无音轨片段。
        var silenceIndex = segments.Count;
        args.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo" });

        var targetScale = resolution ?? "1080:1920";
        var filterParts = new List<string>();

        for (var i = 0; i < segments.Count; i++)
        {
            // format=yuv420p 必须有（v0.7.1 修 N 卡导出崩溃）：
            //  ① concat 滤镜要求所有输入像素格式一致，混入 10-bit HEVC（手机素材常见）会
            //     「at least one of its streams received no packets」(exit -542398533) 输出 0 帧；
            //  ② h264_nvenc 不支持 10-bit，喂 yuv420p10le 直接 0xC0000005 崩溃 (exit -1073741819)。
            //  统一降到 8-bit 4:2:0，nvenc/qsv/amf/libx264 全部支持。构建机(QSV+8bit 素材)测不出此坑。
            filterParts.Add(
                $"[{i}:v]trim=start_frame={segments[i].StartFrame}:end_frame={segments[i].EndFrame}," +
                "setpts=PTS-STARTPTS," +
                $"scale={targetScale}:force_original_aspect_ratio=decrease," +
                $"pad={targetScale}:(ow-iw)/2:(oh-ih)/2:black," +
                $"setsar=1,fps=30,format=yuv420p[v{i}]");

            var startStr = segments[i].StartSeconds.ToString("F9", CultureInfo.InvariantCulture);
            var endStr = segments[i].EndSeconds.ToString("F9", CultureInfo.InvariantCulture);
            var durStr = segments[i].Duration.ToString("F9", CultureInfo.InvariantCulture);
            if (hasAudio[i])
            {
                filterParts.Add(
                    $"[{i}:a]atrim=start={startStr}:end={endStr},asetpts=PTS-STARTPTS," +
                    $"aresample=44100,loudnorm=I=-16:TP=-1.5:LRA=11,apad=whole_dur={durStr}[a{i}]");
            }
            else
            {
                filterParts.Add(
                    $"[{silenceIndex}:a]atrim=0:{durStr},asetpts=PTS-STARTPTS[a{i}]");
            }
        }

        var concatInputs = string.Concat(Enumerable.Range(0, segments.Count).Select(i => $"[v{i}][a{i}]"));
        filterParts.Add($"{concatInputs}concat=n={segments.Count}:v=1:a=1[outv][outa]");

        args.AddRange(new[] { "-filter_complex", string.Join(";", filterParts) });
        args.AddRange(new[] { "-map", "[outv]", "-map", "[outa]" });

        if (isHardware)
        {
            // 硬件编码（nvenc / qsv / amf / mf）不支持 CRF，必须用比特率 + maxrate 控质量。
            // -tag:v hvc1/avc1 让产物在 QuickTime/Safari/Windows 媒体播放器都能播。
            //
            // 注意：v0.4.1 起移除了 NVENC 的 -allow_sw 0 选项 —— gyan.dev ffmpeg 8.1.1 起
            // 该选项被全局解析器拒识为「Unrecognized option」，导致整条导出路径失败
            // (exit -1414549496) Error splitting the argument list: Option not found"。
            // HardwareEncoderProbe 启动期已经做了真实 smoke test 确保 codec 可用，无需额外强制。
            args.AddRange(new[] { "-c:v", codec });
            args.AddRange(new[] { "-b:v", $"{videoBitrateKbps}k" });
            args.AddRange(new[] { "-maxrate", $"{videoBitrateKbps * 2}k" });
            args.AddRange(new[] { "-bufsize", $"{videoBitrateKbps * 2}k" });
            // H.265 加 hvc1 tag（部分 Windows 播放器与跨平台兼容）。
            var isHevc = codec.Contains("hevc", StringComparison.OrdinalIgnoreCase)
                         || codec.Contains("h265", StringComparison.OrdinalIgnoreCase);
            args.AddRange(new[] { "-tag:v", isHevc ? "hvc1" : "avc1" });
        }
        else
        {
            var preset = crf switch
            {
                <= 10 => "slower",
                <= 20 => "slow",
                <= 25 => "medium",
                _ => "fast",
            };
            args.AddRange(new[] { "-c:v", codec, "-crf", crf.ToString(CultureInfo.InvariantCulture), "-preset", preset });
        }
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        args.AddRange(new[] { "-movflags", "+faststart" });
        args.AddRange(new[] { "-y", outputPath });

        var totalDuration = segments.Sum(s => s.Duration);

        // 动态超时：导出拼接耗时随「输出像素 × 成片时长 × 编码方式」线性增长，绝不写死。
        //   pixelFactor: 1080p=1.0，4K(2160×3840)≈4.0；
        //   speedFactor: libx264 软编比 nvenc 慢 5-8×，取 6；硬件取 1；
        //   下限 2 分钟防极短片把超时算成几秒；无上限（4K/CPU 降级可能要几十分钟，必须给够）。
        var outputPixels = ParsePixels(targetScale);
        var pixelFactor = outputPixels / (1920.0 * 1080.0);
        var speedFactor = isHardware ? 1.0 : 6.0;
        var perOutputSec = 2.5 * pixelFactor * speedFactor;
        var exportTimeout = TimeSpan.FromSeconds(Math.Max(totalDuration * perOutputSec, 120));
        Serilog.Log.Information(
            "[ExportTimeout] totalDur={Dur:F1}s pixels={Px} hw={Hw} timeout={Timeout:F0}s",
            totalDuration, outputPixels, isHardware, exportTimeout.TotalSeconds);

        // ⚠️ 命名参数：RunAsync 新增的 timeout 在 cancellationToken 之前，
        //   必须命名传参，否则 token 会被当成 timeout、取消失效。
        await RunAsync(args, totalDuration, onProgress, timeout: exportTimeout, cancellationToken: cancellationToken);
    }

    /// <summary>把 "W:H" 形式的目标分辨率解析成像素总数；失败回退 1080p。</summary>
    private static long ParsePixels(string scale)
    {
        var parts = scale.Split(':');
        if (parts.Length == 2
            && long.TryParse(parts[0], out var w) && w > 0
            && long.TryParse(parts[1], out var h) && h > 0)
        {
            return w * h;
        }
        return 1920L * 1080L;
    }

    // ---- 进度解析 ----

    private static readonly Regex TimeRegex = new(@"time=(\d{2}):(\d{2}):(\d{2}\.\d+)", RegexOptions.Compiled);
    private static readonly Regex FrameRegex = new(@"frame=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex FpsRegex = new(@"fps=\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex SpeedRegex = new(@"speed=\s*([\d.]+)x", RegexOptions.Compiled);

    /// <summary>从 FFmpeg stderr 行中解析进度信息。</summary>
    private static FFmpegProgress? ParseProgress(string text, double? totalDuration)
    {
        var timeMatch = TimeRegex.Match(text);
        if (!timeMatch.Success)
        {
            return null;
        }

        // 防御：用 TryParse 而非 Parse —— ffmpeg stderr 偶发畸形进度行（科学计数 / 超大值 / N/A）
        // 不该抛 FormatException 拖垮整个 stderr 读取任务（见 ReadStderrAsync 的纵深防护）。时间解析
        // 失败则跳过这一行（返回 null），frame/fps/speed 失败给 0。
        if (!double.TryParse(timeMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) ||
            !double.TryParse(timeMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(timeMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }
        var currentTime = hours * 3600 + minutes * 60 + seconds;

        var frame = FrameRegex.Match(text) is { Success: true } fm
            && int.TryParse(fm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fv) ? fv : 0;
        var fps = FpsRegex.Match(text) is { Success: true } fpm
            && double.TryParse(fpm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fpv) ? fpv : 0;
        var speed = SpeedRegex.Match(text) is { Success: true } sm
            && double.TryParse(sm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var spv) ? spv : 0;

        var percentage = totalDuration is > 0
            ? Math.Min(currentTime / totalDuration.Value, 1.0)
            : 0;

        return new FFmpegProgress(frame, fps, currentTime, speed, percentage);
    }

    private static string Fmt(double seconds) => seconds.ToString("F3", CultureInfo.InvariantCulture);
}
