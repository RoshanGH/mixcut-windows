using MixCut.Infrastructure;
using MixCut.Services.Export;
using MixCut.Services.VideoProcessing;
using Xunit;

namespace MixCut.Tests.Services.Export;

/// <summary>
/// 导出 OOM/超时鲁棒性修复的回归测试（2026-06-18 客户 RTX 3060 Ti 4K 导出 15/40 失败事故）。
/// 覆盖：失败分类、分辨率夹取、分辨率感知并发、人话报错。
/// </summary>
public class ExportRobustnessTests
{
    // ---- FFmpegException.Classify：按客户真实指纹归类 ----

    [Fact]
    public void Classify_Oom_ByExitCodeMinus12()
    {
        var ex = FFmpegException.ExecutionFailed(-12, "some stderr tail");
        Assert.Equal(FFmpegFailureClass.Oom, FFmpegException.Classify(ex));
    }

    [Fact]
    public void Classify_Oom_ByCannotAllocateMemory_InFullStderr()
    {
        // 关键行在 stderr 上方，不在最后 200 字 —— 必须扫全文（RawStderr）才认得出。
        var longTail = new string('x', 400);
        var stderr = "[vf#0:0] Error sending frames to consumers: Cannot allocate memory\n" + longTail;
        var ex = FFmpegException.ExecutionFailed(1, stderr);
        Assert.Equal(FFmpegFailureClass.Oom, FFmpegException.Classify(ex));
    }

    [Fact]
    public void Classify_Timeout_ByMessage()
    {
        var ex = FFmpegException.ExecutionFailed(-1, "进程超时");
        Assert.Equal(FFmpegFailureClass.Timeout, FFmpegException.Classify(ex));
    }

    [Fact]
    public void Classify_EncoderCrash_ByReceivedNoPackets()
    {
        var ex = FFmpegException.ExecutionFailed(-542398533,
            "at least one of its streams received no packets");
        Assert.Equal(FFmpegFailureClass.EncoderCrash, FFmpegException.Classify(ex));
    }

    [Fact]
    public void Classify_EncoderCrash_ByAccessViolation()
    {
        var ex = FFmpegException.ExecutionFailed(-1073741819, "crash");
        Assert.Equal(FFmpegFailureClass.EncoderCrash, FFmpegException.Classify(ex));
    }

    [Fact]
    public void Classify_Other_ForUnknown()
    {
        var ex = FFmpegException.ExecutionFailed(1, "unrecognized option foo");
        Assert.Equal(FFmpegFailureClass.Other, FFmpegException.Classify(ex));
    }

    // ---- ClampResolutionToMax1080：4K → 1080p，已小则原样 ----

    [Fact]
    public void Clamp_4kVertical_To1080pVertical()
    {
        Assert.Equal("1080:1920", ExportService.ClampResolutionToMax1080("2160:3840"));
    }

    [Fact]
    public void Clamp_4kLandscape_To1080pLandscape()
    {
        Assert.Equal("1920:1080", ExportService.ClampResolutionToMax1080("3840:2160"));
    }

    [Fact]
    public void Clamp_AlreadyUnder1080p_ReturnsSame()
    {
        // 长边 ≤1920 → 原样返回（== 视为无需重试，ExportService 据此判断是否还能降）。
        Assert.Equal("1080:1920", ExportService.ClampResolutionToMax1080("1080:1920"));
        Assert.Equal("720:1280", ExportService.ClampResolutionToMax1080("720:1280"));
    }

    [Fact]
    public void Clamp_ResultIsEven()
    {
        // ffmpeg 编码要求宽高为偶数。
        var clamped = ExportService.ClampResolutionToMax1080("2161:3841");
        var parts = clamped.Split(':');
        Assert.Equal(0, int.Parse(parts[0]) % 2);
        Assert.Equal(0, int.Parse(parts[1]) % 2);
    }

    // ---- 分辨率感知并发：4K 串行、未知不限 ----

    [Fact]
    public void Concurrency_4k_CappedToOne()
    {
        // 2160×3840 = 8.29M px ≥ 4K → 串行，与有无 GPU/核数无关。
        long pixels4k = 2160L * 3840L;
        Assert.Equal(1, ConcurrencyPolicy.MaxExportConcurrency(tasksCount: 40, outputPixels: pixels4k));
    }

    [Fact]
    public void Concurrency_UnknownPixels_KeepsLegacyBehavior()
    {
        // 0 = 未知 → 不额外限制（保持旧行为），且不小于 1。
        var legacy = ConcurrencyPolicy.MaxExportConcurrency(tasksCount: 40, outputPixels: 0);
        Assert.True(legacy >= 1);
    }

    [Fact]
    public void Concurrency_NeverExceedsTaskCount()
    {
        Assert.Equal(1, ConcurrencyPolicy.MaxExportConcurrency(tasksCount: 1, outputPixels: 1920L * 1080L));
    }

    // ---- 人话报错：不含原生错误码/stderr 原文 ----

    [Theory]
    [InlineData(-12, "Cannot allocate memory")]
    [InlineData(-1, "进程超时")]
    [InlineData(-1073741819, "crash")]
    public void Friendly_NeverLeaksRawCodesOrExit(int exitCode, string stderr)
    {
        var msg = ExportErrorMessage.ToFriendly(FFmpegException.ExecutionFailed(exitCode, stderr));
        Assert.DoesNotContain("exit", msg);
        Assert.DoesNotContain(exitCode.ToString(), msg);
        Assert.DoesNotContain("stderr", msg);
        Assert.False(string.IsNullOrWhiteSpace(msg));
    }

    [Fact]
    public void Friendly_Oom_SuggestsClosingAppsAnd1080p()
    {
        var msg = ExportErrorMessage.ToFriendly(FFmpegException.ExecutionFailed(-12, "Cannot allocate memory"));
        Assert.Contains("内存", msg);
        Assert.Contains("1080p", msg);
    }
}
