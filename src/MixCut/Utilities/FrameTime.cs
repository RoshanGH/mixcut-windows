namespace MixCut.Utilities;

/// <summary>
/// 帧 ↔ 秒 ↔ 时间码 互转工具。对齐 issue #7 / macOS MixCutCore.FrameTime。
///
/// 设计原则（issue #7 第二节）：**帧是离散、可精确寻址的最小单位，是唯一真值**；
/// 「秒」只是 <c>seconds = frame / fps</c> 的派生缓存。任何展示 / 播放 / 切割都从帧号换算，
/// 不再反过来以秒为准——以秒为真值就一定有「切点落在两帧之间」的歧义与浮点累积误差。
///
/// 本类为纯函数、零外部依赖，便于单元测试覆盖（见 FrameTimeTests）。
/// </summary>
public static class FrameTime
{
    /// <summary>
    /// 时间码使用的名义帧率。29.97/59.94 分别按 30/60 进位，但帧↔秒仍使用真实 fps，
    /// 因此不会把 29.97 素材错误当成 30fps 计算时长。
    /// </summary>
    public static int NominalFps(double fps) =>
        fps > 0 && double.IsFinite(fps) ? Math.Max(1, (int)Math.Round(fps)) : 0;

    /// <summary>秒 → 帧号（四舍五入到最近帧）。fps≤0 或非法秒数时返回 0。</summary>
    public static int SecondsToFrame(double seconds, double fps)
    {
        if (fps <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
        {
            return 0;
        }
        return (int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);
    }

    /// <summary>帧号 → 秒（精确派生）。fps≤0 或帧号为负时返回 0。</summary>
    public static double FrameToSeconds(int frame, double fps)
    {
        if (fps <= 0 || frame <= 0)
        {
            return 0;
        }
        return frame / fps;
    }

    /// <summary>
    /// 把任意秒数量化到「最近帧对应的精确秒」，消除「落在两帧之间」的歧义。
    /// fps≤0（旧数据未回填 fps）时原样返回（保证非负），不破坏行为。
    /// </summary>
    public static double QuantizeToFrame(double seconds, double fps)
    {
        if (fps <= 0)
        {
            return Math.Max(0, seconds);
        }
        return FrameToSeconds(SecondsToFrame(seconds, fps), fps);
    }

    /// <summary>
    /// 剪映式时间码 <c>时:分:秒:帧</c>，帧位按 fps 进位（30fps 满 30 进 1 秒，60fps 满 60 进 1 秒）。
    /// fps 取整后参与进位（29.97 近似按 30；本项目验收只要求 30/60 整数 fps 正确）。
    /// fps≤0 时退化为 <c>分:秒</c>（无帧位）。
    /// </summary>
    public static string ToTimecode(int frame, double fps)
    {
        if (frame < 0)
        {
            frame = 0;
        }
        var fpsInt = NominalFps(fps);
        if (fpsInt <= 0)
        {
            // 没有有效 fps：退化为 分:秒（帧位无意义）
            return "00:00";
        }
        var totalSeconds = frame / fpsInt;
        var ff = frame % fpsInt;
        var hh = totalSeconds / 3600;
        var mm = (totalSeconds % 3600) / 60;
        var ss = totalSeconds % 60;
        return hh > 0
            ? $"{hh:D2}:{mm:D2}:{ss:D2}:{ff:D2}"
            : $"{mm:D2}:{ss:D2}:{ff:D2}";
    }

    /// <summary>从秒数直接生成时间码。fps≤0 时退化为 <c>分:秒</c>（从秒数取整）。</summary>
    public static string ToTimecodeFromSeconds(double seconds, double fps)
    {
        if (fps <= 0)
        {
            var total = (int)Math.Floor(Math.Max(0, seconds));
            return $"{total / 60:D2}:{total % 60:D2}";
        }
        return ToTimecode(SecondsToFrame(seconds, fps), fps);
    }

    /// <summary>结束边界为 exclusive，预览应停在它前面的最后一帧。</summary>
    public static int LastIncludedFrame(int startFrame, int endFrame) =>
        Math.Max(Math.Max(0, startFrame), Math.Max(0, endFrame) - 1);

    /// <summary>
    /// 人类可读的「总时长」展示。汇总时长（多分镜之和）可能很大，秒数原文（如 "2313s"）
    /// 不符合商用软件标准。不足 1 小时显示 <c>分:秒</c>（38:33），超过显示 <c>时:分:秒</c>（1:02:33），
    /// 与 <see cref="InlineVideoPlayer"/> 播放计时格式一致。
    /// 单个分镜的短时长（通常 &lt;60s）仍沿用 "10.8s" 风格，本方法不替代它。
    /// </summary>
    public static string HumanDuration(double seconds)
    {
        var total = (int)Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero);
        var hh = total / 3600;
        var mm = (total % 3600) / 60;
        var ss = total % 60;
        return hh > 0 ? $"{hh}:{mm:D2}:{ss:D2}" : $"{mm}:{ss:D2}";
    }
}
