using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using MixCut.Infrastructure;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;

namespace MixCut.Services.Dubbing;

/// <summary>
/// 用内置 demucs.cpp 把一条视频的整轨音频分离成「人声 / 背景音乐」。对应 mac VocalSeparationService。
/// 整轨只分离一次并按 videoHash 缓存；导出按分镜切片，克隆用人声前 6s 做参考。
/// CLI：demucs.exe &lt;model.bin&gt; &lt;input.wav&gt; &lt;outDir&gt; → target_0_drums/1_bass/2_other/3_vocals.wav。
/// </summary>
public sealed class VocalSeparationService
{
    private const string ModelFileName = "ggml-htdemucs-4s.bin";

    /// <summary>模型下载源：国内镜像优先，回退国际源（铁律：勿用 huggingface 直链当唯一源）。</summary>
    private static readonly string[] ModelUrls =
    {
        "https://hf-mirror.com/datasets/Retrobear/demucs.cpp/resolve/main/ggml-model-htdemucs-4s-f16.bin",
        "https://huggingface.co/datasets/Retrobear/demucs.cpp/resolve/main/ggml-model-htdemucs-4s-f16.bin",
    };

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim ModelDownloadGate = new(1, 1);

    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<VocalSeparationService> _logger;

    public VocalSeparationService(FFmpegRunner ffmpeg, ILogger<VocalSeparationService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>整轨分离（已缓存直接返回）。<paramref name="onProgress"/> 回调进度文案。</summary>
    public async Task<SeparatedStems> SeparateAsync(
        string videoPath, string videoHash,
        IProgress<string>? onProgress = null, CancellationToken ct = default)
    {
        var dir = AppPaths.StemsDirectory(videoHash);
        var vocals = Path.Combine(dir, "vocals.wav");
        var bgm = Path.Combine(dir, "bgm.wav");
        if (File.Exists(vocals) && File.Exists(bgm))
        {
            _logger.LogInformation("[DubDiag] 人声分离缓存命中 hash={Hash}", videoHash);
            return new SeparatedStems(vocals, bgm);
        }

        if (!BundledBinaries.DemucsAvailable)
        {
            throw new DubException("未找到人声分离组件（demucs），请重新安装应用");
        }

        var model = await EnsureModelAsync(onProgress, ct);

        var tmp = Path.GetTempPath();
        // 1) 抽整轨音频为 44.1k 立体声 wav（demucs.cpp 输入要求）
        onProgress?.Report("提取原始音频…");
        var inputWav = Path.Combine(tmp, $"mixcut-sep-in-{Guid.NewGuid():N}.wav");
        await _ffmpeg.RunAsync(
            new[] { "-y", "-i", videoPath, "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", inputWav },
            timeout: TimeSpan.FromMinutes(5), cancellationToken: ct);

        // 2) demucs 分离到临时目录（4 stems）
        onProgress?.Report("AI 分离人声与背景音乐（较慢，请稍候）…");
        var outDir = Path.Combine(tmp, $"mixcut-sep-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var probedDuration = await _ffmpeg.ProbeDurationAsync(videoPath, ct);
        _logger.LogInformation("[DubDiag] demucs 开始分离 hash={Hash} dur={Dur:F0}s", videoHash, probedDuration);
        await RunDemucsAsync(model, inputWav, outDir, probedDuration, ct);

        var drums = Path.Combine(outDir, "target_0_drums.wav");
        var bass = Path.Combine(outDir, "target_1_bass.wav");
        var other = Path.Combine(outDir, "target_2_other.wav");
        var voc = Path.Combine(outDir, "target_3_vocals.wav");
        if (!File.Exists(voc))
        {
            throw new DubException("人声分离失败（未产出人声轨），请重试");
        }

        // 3) BGM = drums + bass + other（不归一化，保持原响度）；vocals 直接取
        onProgress?.Report("合成背景音乐轨…");
        await _ffmpeg.RunAsync(
            new[] { "-y", "-i", drums, "-i", bass, "-i", other,
                    "-filter_complex", "amix=inputs=3:normalize=0", "-c:a", "pcm_s16le", bgm },
            timeout: TimeSpan.FromMinutes(5), cancellationToken: ct);

        if (File.Exists(vocals)) { try { File.Delete(vocals); } catch { /* 占用忽略 */ } }
        File.Copy(voc, vocals, overwrite: true);

        // 清理临时
        TryDelete(inputWav);
        TryDeleteDir(outDir);

        _logger.LogInformation("[DubDiag] 人声分离完成 vocals={Vocals} bgm={Bgm}", vocals, bgm);
        return new SeparatedStems(vocals, bgm);
    }

    /// <summary>
    /// 从分离出的人声里取一段做克隆参考（默认 ≤6s，转 mp3 便于 base64）。
    /// 必须短而干净：参考太长(含多句原话)会让克隆 TTS 间歇性"续读参考内容"（坑4）。
    /// </summary>
    public async Task<string> ReferenceClipAsync(string vocalsPath, double maxSeconds = 6, CancellationToken ct = default)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"mixcut-clone-ref-{Guid.NewGuid():N}.mp3");
        await _ffmpeg.RunAsync(
            new[] { "-y", "-i", vocalsPath, "-t", maxSeconds.ToString("F2"),
                    "-ac", "1", "-ar", "24000", "-c:a", "libmp3lame", "-b:a", "128k", outPath },
            timeout: TimeSpan.FromMinutes(2), cancellationToken: ct);
        return outPath;
    }

    // ---- demucs 进程 ----

    private async Task RunDemucsAsync(string model, string input, string outDir, double durationSec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BundledBinaries.Demucs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add(outDir);

        using var process = new Process { StartInfo = psi };
        process.Start();
        ChildProcessTracker.AddProcess(process); // 防孤儿：MixCut 退出时一并杀
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* 忽略 */ }

        // 排掉 stdout/stderr，避免管道缓冲塞满阻塞进程（demucs 进度打到 stdout）。
        _ = process.StandardOutput.ReadToEndAsync(ct);
        _ = process.StandardError.ReadToEndAsync(ct);

        // 超时按时长动态算（实测 CPU ~9x 实时，留足余量），下限 15 分钟。
        var timeout = TimeSpan.FromSeconds(Math.Max(900, durationSec * 30));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            throw new DubException($"人声分离超时（超过 {timeout.TotalMinutes:F0} 分钟）");
        }

        if (process.ExitCode != 0)
        {
            throw new DubException($"人声分离失败（demucs 退出码 {process.ExitCode}）");
        }
    }

    // ---- 模型下载（国内镜像 + Range 续传 + 原子落地；对齐 ASRService 思路）----

    private async Task<string> EnsureModelAsync(IProgress<string>? onProgress, CancellationToken ct)
    {
        var dest = Path.Combine(AppPaths.DemucsModelsDirectory, ModelFileName);
        if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;

        await ModelDownloadGate.WaitAsync(ct);
        try
        {
            if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;

            onProgress?.Report("下载人声分离模型（约 80MB，仅首次）…");
            Exception? last = null;
            foreach (var url in ModelUrls)
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        await DownloadWithResumeAsync(url, dest, onProgress, ct);
                        _logger.LogInformation("[DubDiag] demucs 模型就绪 {Path}", dest);
                        return dest;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        last = ex;
                        _logger.LogWarning("[DubDiag] demucs 模型下载失败({Url} 第{N}次): {Msg}", url, attempt + 1, ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                    }
                }
            }
            throw new DubException("人声分离模型下载失败，请检查网络后重试", last ?? new Exception("unknown"));
        }
        finally
        {
            ModelDownloadGate.Release();
        }
    }

    private static async Task DownloadWithResumeAsync(
        string url, string dest, IProgress<string>? onProgress, CancellationToken ct)
    {
        var partPath = dest + ".download";
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        // 416 = 已下完整个文件（服务端认为 range 不可满足）→ 视为完成
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            FinalizePart(partPath, dest);
            return;
        }
        resp.EnsureSuccessStatusCode();

        var append = resp.StatusCode == System.Net.HttpStatusCode.PartialContent && existing > 0;
        if (!append) existing = 0;

        var total = (resp.Content.Headers.ContentLength ?? 0) + (append ? existing : 0);
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = new FileStream(partPath, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1 << 16];
            long received = existing;
            long lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                received += n;
                if (total > 0 && received - lastReport >= 4 << 20) // 每 4MB 报一次
                {
                    lastReport = received;
                    onProgress?.Report($"下载人声分离模型 {received * 100.0 / total:F0}%（仅首次）…");
                }
            }
        }

        FinalizePart(partPath, dest);
    }

    private static void FinalizePart(string partPath, string dest)
    {
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(partPath, dest);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
