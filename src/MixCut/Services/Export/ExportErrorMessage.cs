using MixCut.Services.VideoProcessing;

namespace MixCut.Services.Export;

/// <summary>
/// 把导出失败的底层异常翻译成「人话 + 可操作建议」。
///
/// §最高原则红线：绝不把 stack trace / exit -1 / -12 / stderr 原文丢用户看。
/// 原始报错码与完整 stderr 已写进 [ffmpeg-fail] 日志供开发诊断；这里只产出用户能看懂、
/// 看完知道下一步怎么办的中文。
/// </summary>
public static class ExportErrorMessage
{
    public static string ToFriendly(Exception ex)
    {
        // 已经是人话的（组件缺失提示），直接用。
        if (ex is FFmpegException { Kind: FFmpegErrorKind.BinaryNotFound } binEx)
        {
            return binEx.Message;
        }

        if (ex is FFmpegException ffEx)
        {
            return FFmpegException.Classify(ffEx) switch
            {
                FFmpegFailureClass.Oom =>
                    "内存不足或机器繁忙：建议关闭剪映等占用内存的大型程序，或在导出设置里选 1080p；" +
                    "已自动降低规格重试仍未成功",
                FFmpegFailureClass.Timeout =>
                    "导出耗时过长已中止：4K 渲染很慢，建议在导出设置里选 1080p，" +
                    "或关闭其他占用 CPU/显卡的程序后重试",
                FFmpegFailureClass.EncoderCrash =>
                    "显卡编码器异常：已尝试用 CPU 重新编码仍失败，" +
                    "建议更新显卡驱动或在导出设置里改用「H.264（CPU 软件编码）」",
                _ =>
                    "导出失败，请重试；若反复失败请在设置里改用 1080p 与 CPU 编码",
            };
        }

        // 非 FFmpeg 异常（IO / 磁盘满等）——给通用人话，不暴露 .NET 异常文本。
        return "导出失败，请重试；若反复失败请检查磁盘空间，或在设置里改用 1080p 与 CPU 编码";
    }
}
