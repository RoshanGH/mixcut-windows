namespace MixCut.Services.Dubbing;

/// <summary>
/// 配音时长对齐的纯决策 + atempo 链拆分。对应 mac AudioAligner + DubAudioFinalizer.atempoChain。
///
/// 铁律（PRD §决策5）：<b>配音绝不超过画面时长</b>——偏长一律用 atempo 压到正好等于画面
/// （永不靠定格补帧延长画面，<see cref="AlignmentPlan.FreezePadFrames"/> 恒为 0）；偏短才适度放慢，
/// 残差用末尾静音补满。
/// </summary>
public static class AudioAligner
{
    /// <summary>偏短直接命中阈值（秒）：配音比画面短 ≤ 此值时不变速，末尾补少量静音即可。</summary>
    private const double DirectThreshold = 0.15;

    /// <summary>放慢封底（最多放慢 ~25%）：配音偏短时拉满画面、消除末尾空挡。</summary>
    private const double AtempoMinStretch = 0.8;

    /// <param name="targetDuration">画面（分镜）时长 D。</param>
    /// <param name="audioDuration">TTS 原始音频时长 D'。</param>
    /// <param name="fps">视频帧率（保留参数，当前不再用于定格补帧）。</param>
    public static AlignmentPlan Plan(double targetDuration, double audioDuration, double fps)
    {
        if (targetDuration <= 0 || audioDuration <= 0 || fps <= 0)
        {
            return new AlignmentPlan(1.0, 0, 0);
        }

        double atempo;
        if (audioDuration > targetDuration)
        {
            // 偏长 → 无封顶压缩到正好等于画面（>2.0 由 AtempoChain 链式拆分实现）。
            atempo = audioDuration / targetDuration;
        }
        else
        {
            var shortBy = targetDuration - audioDuration;
            atempo = shortBy <= DirectThreshold
                ? 1.0
                : Math.Max(audioDuration / targetDuration, AtempoMinStretch);
        }

        var outDuration = audioDuration / atempo;
        var residual = outDuration - targetDuration; // >0 仅极端超长（封顶仍超）；<0 偏短

        // 永不通过定格补帧延长画面。
        return residual < -0.001
            ? new AlignmentPlan(atempo, 0, -residual)
            : new AlignmentPlan(atempo, 0, 0);
    }

    /// <summary>
    /// 把任意 atempo 系数拆成每段都落在 [0.5, 2.0] 的链（相乘等于原系数）。
    /// 例：2.6 → ["atempo=2.0000","atempo=1.3000"]；接近 1.0 时返回空（不变速）。
    /// </summary>
    public static IReadOnlyList<string> AtempoChain(double factor)
    {
        if (factor <= 0 || Math.Abs(factor - 1.0) <= 0.001)
        {
            return Array.Empty<string>();
        }

        var remaining = factor;
        var parts = new List<string>();
        while (remaining > 2.0) { parts.Add("atempo=2.0000"); remaining /= 2.0; }
        while (remaining < 0.5) { parts.Add("atempo=0.5000"); remaining /= 0.5; }
        parts.Add($"atempo={remaining:F4}");
        return parts;
    }
}
