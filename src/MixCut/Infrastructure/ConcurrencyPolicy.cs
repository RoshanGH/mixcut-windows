namespace MixCut.Infrastructure;

/// <summary>
/// 统一的并发策略。所有跑 ffmpeg / whisper-cli 的批处理路径都从这里取并发数，
/// 避免散落在 ImportViewModel / ExportView / SettingsWindow 三处各算各的导致不一致。
///
/// 设计原则：
/// 1. CPU 核心数是基准。
/// 2. 有 GPU 编码（NVENC/QSV/AMF）时给导出 +3 路加成，但封顶 11
///    （消费级 NVIDIA NVENC session 上限通常 3-5 路，超出会失败）。
/// 3. 有 GPU 解码（cuda/qsv/d3d11va）时给分析 +1 路（whisper 仍是 CPU 瓶颈，仅 ffmpeg 部分受益）。
/// 4. 没硬件时维持原 CPU 公式。
///
/// 让 Settings UI 显示具体公式，用户看得到「8 路（CPU 5 + GPU 加成 +3）」这样的透明信息。
/// </summary>
public static class ConcurrencyPolicy
{
    /// <summary>视频分析并发数（每路跑 whisper + ffmpeg 场景检测 / 静音检测）。</summary>
    public static int MaxAnalyzeConcurrency(int videosToAnalyzeCount = int.MaxValue)
    {
        var cores = Environment.ProcessorCount;
        var ffmpegBoost = HardwareEncoderProbe.DecodeHwaccel is not null ? 1 : 0;
        // 基础：N/4（whisper 单进程吃 N-2 线程，多路并发会互相挤）
        // GPU 解码 +1：场景检测的 ffmpeg 走 GPU 解码，让 1 路出 CPU 给 whisper
        // 上限 4（有 GPU）/ 3（无 GPU）
        var ceil = ffmpegBoost > 0 ? 4 : 3;
        var raw = cores / 4 + ffmpegBoost;
        var bounded = Math.Min(ceil, Math.Max(1, raw));
        return Math.Min(bounded, Math.Max(1, videosToAnalyzeCount));
    }

    /// <summary>批量导出并发数（每路跑 ffmpeg 编码）。GPU 编码加速时显著提升。</summary>
    /// <param name="outputPixels">
    /// 本批输出分辨率的像素总数（W×H）。0=未知（保持旧行为）。
    /// 高分辨率（4K）的 filter graph 极吃内存，并发开满会撞 OOM（exit -12
    /// "Cannot allocate memory"，客户 RTX 3060 Ti 4K 导出 15/40 失败的根因），
    /// 故按分辨率额外封顶 —— 这是 NVENC 会话上限之外的「内存带宽」维度限制。
    /// </param>
    public static int MaxExportConcurrency(int tasksCount = int.MaxValue, long outputPixels = 0)
    {
        var cores = Environment.ProcessorCount;
        var h264hw = HardwareEncoderProbe.H264Hardware;
        var hwEncode = h264hw is not null;
        // v0.7.1：消费级 NVIDIA NVENC 同时只允许 3-5 路编码会话，并发开满（原 11）会让超出的
        // 会话初始化失败 → 该路导出产出 0 帧 / 崩溃（用户 N 卡导出 0/21 全失败的元凶之一）。
        // NVENC 显式封 3 路；QSV/AMF 无此硬限制，维持 11。
        var isNvenc = h264hw?.Contains("nvenc", StringComparison.OrdinalIgnoreCase) == true;
        var hwBoost = hwEncode ? 3 : 0;
        // 基础：(N-2)/2（软件 H.264 编码 ~50% CPU 一路，留 2 核给 UI / IO）
        // GPU 编码 +3：QSV/AMF 一路编码 CPU 占用 ~10%，可叠加
        // 上限：NVENC 3（会话限制）/ 其他 GPU 11 / 无 GPU 8（原 Mac 版同算法）
        var ceil = isNvenc ? 3 : (hwEncode ? 11 : 8);
        var raw = Math.Max(1, (cores - 2) / 2) + hwBoost;
        var bounded = Math.Min(ceil, raw);
        // 分辨率内存封顶：4K 串行、2K 限 2、1080p 限 4，避免多路 4K filter graph 叠加 OOM。
        bounded = Math.Min(bounded, ResolutionCap(outputPixels));
        return Math.Min(bounded, Math.Max(1, tasksCount));
    }

    /// <summary>按输出像素数返回内存安全的并发上限（0=未知则不额外限制）。</summary>
    private static int ResolutionCap(long outputPixels) => outputPixels switch
    {
        0 => int.MaxValue,                 // 未知：保持旧行为
        >= 3840L * 2160 => 1,              // ≥4K（含 2160×3840 竖屏 8.29M px）：串行
        >= 2560L * 1440 => 2,              // ≥1440p / 2K
        >= 1920L * 1080 => 4,              // ≥1080p：适度限
        _ => int.MaxValue,                 // ≤1080p：不额外限
    };

    /// <summary>给 Settings UI 用的「并发数透明拆解」文本，例如 "5 + GPU 加成 +3 = 8 路"。</summary>
    /// <param name="outputPixels">本次输出像素数；0=理论上限（Settings 通用展示用 0）。</param>
    public static string ExplainExportFormula(long outputPixels = 0)
    {
        var cores = Environment.ProcessorCount;
        var basePart = Math.Max(1, (cores - 2) / 2);
        var h264hw = HardwareEncoderProbe.H264Hardware;
        var hwEncode = h264hw is not null;
        var isNvenc = h264hw?.Contains("nvenc", StringComparison.OrdinalIgnoreCase) == true;
        var ceil = isNvenc ? 3 : (hwEncode ? 11 : 8);
        var raw = basePart + (hwEncode ? 3 : 0);
        var beforeResCap = Math.Min(ceil, raw);
        var resCap = ResolutionCap(outputPixels);
        var actual = Math.Min(beforeResCap, resCap);

        string baseText;
        if (!hwEncode)
        {
            baseText = $"{beforeResCap} 路（CPU (N-2)/2 = {basePart}，无 GPU 加速）";
        }
        else if (isNvenc)
        {
            baseText = $"{beforeResCap} 路（NVENC 会话上限封 {ceil} 路，避免并发超限导致导出失败）";
        }
        else
        {
            baseText = $"{beforeResCap} 路（CPU {basePart} + GPU 加成 +3，上限 {ceil}）";
        }

        // 分辨率内存封顶生效且更严格时，追加说明。
        if (resCap < beforeResCap)
        {
            var resLabel = outputPixels >= 3840L * 2160 ? "4K"
                : outputPixels >= 2560L * 1440 ? "2K"
                : "1080p";
            return $"{actual} 路（本次输出 {resLabel}，为避免内存不足已限为 {actual} 路{(actual == 1 ? "串行" : "")}）";
        }
        return baseText;
    }

    /// <summary>给 Settings UI 用的「分析并发透明拆解」文本。</summary>
    public static string ExplainAnalyzeFormula()
    {
        var cores = Environment.ProcessorCount;
        var basePart = Math.Max(1, cores / 4);
        var hwDecode = HardwareEncoderProbe.DecodeHwaccel is not null;
        var ceil = hwDecode ? 4 : 3;
        var raw = basePart + (hwDecode ? 1 : 0);
        var actual = Math.Min(ceil, raw);

        if (!hwDecode)
        {
            return $"{actual} 路（CPU N/4 = {basePart}，无 GPU 解码加速）";
        }
        return $"{actual} 路（CPU {basePart} + GPU 解码加成 +1，上限 {ceil}）";
    }
}
