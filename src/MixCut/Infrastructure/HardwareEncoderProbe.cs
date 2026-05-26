using System.Diagnostics;

namespace MixCut.Infrastructure;

/// <summary>
/// 硬件加速能力探测。启动时跑一次，按优先级（NVIDIA → Intel → AMD → MS）选最优后端，
/// 用 smoke test 实测确认真正可用。没硬件时全部 fallback CPU/软件编码。
///
/// 覆盖三类任务：
/// 1. 视频编码（NVENC / QSV / AMF / MF → libx264）—— H264Hardware
/// 2. 视频解码（CUDA / QSV / D3D11VA / DXVA2 → 软件解码）—— DecodeHwaccel
/// 3. ASR Whisper 推理（CUDA / Vulkan → CPU）—— WhisperBackend
/// </summary>
public static class HardwareEncoderProbe
{
    private static readonly object _gate = new();
    private static bool _probed;
    private static string? _h264Hw;
    private static string? _h265Hw;
    private static string? _decodeHwaccel;
    private static string _whisperBackend = "cpu";

    /// <summary>选中的 H.264 硬件编码器（如 <c>h264_nvenc</c>）；null 表示无可用硬件。</summary>
    public static string? H264Hardware
    {
        get { EnsureProbed(); return _h264Hw; }
    }

    /// <summary>选中的 H.265 硬件编码器（如 <c>hevc_nvenc</c>）；null 表示无可用硬件。</summary>
    public static string? H265Hardware
    {
        get { EnsureProbed(); return _h265Hw; }
    }

    /// <summary>
    /// 选中的视频解码 hwaccel（如 <c>cuda</c> / <c>qsv</c> / <c>d3d11va</c>）；null 表示用 CPU 软件解码。
    /// 用于缩略图 / 场景检测 / I-frame 提取 / 音频抽取等纯 decode 任务。
    /// 不用于 CutSegmentAsync（trim filter 跑 software 流，hardware frame 需要 download，得不偿失）。
    /// </summary>
    public static string? DecodeHwaccel
    {
        get { EnsureProbed(); return _decodeHwaccel; }
    }

    /// <summary>
    /// Whisper 推理后端：cuda / vulkan / cpu。
    /// 当前 bundled whisper-cli 是 CPU build，永远返回 cpu；
    /// 但启动时会探测系统能力，记日志为未来 GPU build 切换做准备。
    /// </summary>
    public static string WhisperBackend
    {
        get { EnsureProbed(); return _whisperBackend; }
    }

    /// <summary>是否有任何硬件编码器可用（控制 UI 默认选项）。</summary>
    public static bool HasAnyHardware => H264Hardware is not null || H265Hardware is not null;

    /// <summary>对外的人话名称（NVIDIA / Intel / AMD / Media Foundation / 无）。</summary>
    public static string HardwareDescription
    {
        get
        {
            EnsureProbed();
            var pick = _h264Hw ?? _h265Hw;
            return pick switch
            {
                null => "无硬件加速（CPU 软件编码）",
                var s when s.Contains("nvenc", StringComparison.OrdinalIgnoreCase) => "NVIDIA NVENC",
                var s when s.Contains("qsv", StringComparison.OrdinalIgnoreCase) => "Intel Quick Sync",
                var s when s.Contains("amf", StringComparison.OrdinalIgnoreCase) => "AMD AMF",
                var s when s.Contains("_mf", StringComparison.OrdinalIgnoreCase) => "Media Foundation",
                _ => pick,
            };
        }
    }

    /// <summary>启动时主动触发一次探测，让结果尽早入日志。</summary>
    public static void EagerInit() => EnsureProbed();

    private static void EnsureProbed()
    {
        lock (_gate)
        {
            if (_probed) return;
            _probed = true;

            if (!BundledBinaries.FfmpegAvailable) return;

            try
            {
                // 1. 拿 ffmpeg encoders + hwaccels 列表
                var encodersOutput = RunFfmpegOutput("-hide_banner", "-encoders");
                var hwaccelsOutput = RunFfmpegOutput("-hide_banner", "-hwaccels");

                // 2. 编码器：候选 → smoke test
                var h264Candidates = ListFirst(encodersOutput,
                    "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf");
                var h265Candidates = ListFirst(encodersOutput,
                    "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf");
                _h264Hw = h264Candidates.FirstOrDefault(SmokeTestEncoder);
                _h265Hw = h265Candidates.FirstOrDefault(SmokeTestEncoder);

                // 3. 解码 hwaccel：按优先级列表挑第一个 ffmpeg 支持的。
                //    NVIDIA CUDA > Intel QSV > Microsoft D3D11VA > Microsoft DXVA2
                //    复用 encode smoke 结果作启发：encode 通过 → 同厂 decode 大概率通过
                _decodeHwaccel = PickDecodeHwaccel(hwaccelsOutput);

                // 4. Whisper backend（当前 binary 仅 CPU；探测 GPU 写日志为未来准备）
                _whisperBackend = DetectWhisperBackend();

                Serilog.Log.Information(
                    "[HwProbe] encode={Encode} decode={Decode} whisper={Whisper}",
                    _h264Hw ?? "libx264",
                    _decodeHwaccel ?? "(software)",
                    _whisperBackend);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[HwProbe] 探测异常，全部 fallback 软件");
                _h264Hw = null;
                _h265Hw = null;
                _decodeHwaccel = null;
                _whisperBackend = "cpu";
            }
        }
    }

    /// <summary>
    /// 按优先级选 decode hwaccel。复用 encode smoke test 结果：同厂商 encoder 通过 → decoder 大概率可用。
    /// 这避免了 decode smoke test 需要现成 encoded 视频文件的复杂度。
    /// </summary>
    private static string? PickDecodeHwaccel(string hwaccelsOutput)
    {
        // 检查 ffmpeg 是否列出了该 hwaccel
        bool ListContains(string name) => hwaccelsOutput.Contains(
            Environment.NewLine + name, StringComparison.OrdinalIgnoreCase)
            || hwaccelsOutput.Contains("\n" + name, StringComparison.OrdinalIgnoreCase);

        // 优先 NVIDIA（如果 NVENC 通过 smoke test，cuda decode 也可用）
        if (_h264Hw is not null && _h264Hw.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
            && ListContains("cuda"))
        {
            return "cuda";
        }
        // 其次 Intel（QSV encode 通过 → QSV decode 可用）
        if (_h264Hw is not null && _h264Hw.Contains("qsv", StringComparison.OrdinalIgnoreCase)
            && ListContains("qsv"))
        {
            return "qsv";
        }
        // 通用 Windows D3D11VA（大多数 GPU 都支持，无需驱动）
        if (ListContains("d3d11va")) return "d3d11va";
        // 老版本 Windows DXVA2 兜底
        if (ListContains("dxva2")) return "dxva2";
        return null;
    }

    /// <summary>检测 Whisper GPU 推理能力。当前 binary 是 CPU build，永远返回 "cpu"。</summary>
    private static string DetectWhisperBackend()
    {
        // 未来支持 GPU whisper-cli build 时这里实现：
        // 1. 检查是否有 CUDA build 的 whisper-cli + nvcuda.dll 存在
        // 2. 检查是否有 Vulkan build 的 whisper-cli + vulkan-1.dll 存在
        // 当前 bundled binary 用 OpenBLAS CPU，永远返回 cpu。
        return "cpu";
    }

    private static string RunFfmpegOutput(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BundledBinaries.Ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        ChildProcessTracker.AddProcess(p);
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    private static List<string> ListFirst(string encoderList, params string[] candidates)
    {
        var result = new List<string>();
        foreach (var c in candidates)
        {
            if (encoderList.Contains(" " + c + " ", StringComparison.Ordinal) ||
                encoderList.Contains("\t" + c + "\t", StringComparison.Ordinal) ||
                encoderList.Contains(" " + c + "\t", StringComparison.Ordinal))
            {
                result.Add(c);
            }
        }
        return result;
    }

    /// <summary>实际试跑一帧编码，验证 encoder 真正可用（识破"binary 编进了但驱动缺失"假阳性）。</summary>
    private static bool SmokeTestEncoder(string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = BundledBinaries.Ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[]
            {
                "-v", "error",
                "-f", "lavfi", "-i", "color=black:s=320x240:r=30:d=0.1",
                "-c:v", encoder,
                "-f", "null", "-",
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var p = new Process { StartInfo = psi };
            p.Start();
            ChildProcessTracker.AddProcess(p);
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            if (p.ExitCode != 0)
            {
                Serilog.Log.Information("[HwProbe] {Enc} smoke 失败", encoder);
                return false;
            }
            Serilog.Log.Information("[HwProbe] {Enc} smoke 通过", encoder);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
