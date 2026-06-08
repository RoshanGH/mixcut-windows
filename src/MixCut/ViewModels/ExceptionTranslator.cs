using MixCut.Services.AI;
using MixCut.Services.VideoProcessing;

namespace MixCut.ViewModels;

/// <summary>
/// 把底层异常翻译成面向用户的人话提示。
///
/// CLAUDE.md 红线：用户面板上不许出现 exit code / stack trace / JsonException 等英文报错码。
/// ffmpeg 的 <c>exit -542398533</c>、whisper 的 <c>ExitCode=-1073741515</c>、原始 stderr 这类
/// 内容只能进日志，绝不直接拼进 <c>video.ErrorMessage</c> 给用户看。
///
/// 原始异常仍由各调用处的 <c>_logger.LogError</c> 记录进日志，排查信息不丢。
/// </summary>
public static class ExceptionTranslator
{
    /// <summary>把异常翻译成一句用户能看懂的中文原因（不含任何错误码 / stderr / 类型名）。</summary>
    public static string ToUserMessage(Exception ex) => ex switch
    {
        // AI 异常工厂（AIProviderException）本身已是纯中文人话，直接用。
        AIProviderException ai => ai.Message,
        // FFmpeg 异常的 Message 含 "exit {code}: {stderr}"，绝不能直接给用户 —— 翻成人话。
        FFmpegException => "视频处理失败，文件可能损坏或格式不支持，请换一个视频重试",
        OperationCanceledException => "已取消",
        // 其余（含 whisper 的 ExitCode 异常、JsonException 等）统一兜底，技术细节只在日志里。
        _ => "处理失败，请重试（详情见日志）",
    };
}
