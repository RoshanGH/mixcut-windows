using System.IO;
using Microsoft.Extensions.Logging;
using MixCut.Services.VideoProcessing;

namespace MixCut.Services.Export;

/// <summary>单个分镜的导出任务。对应 macOS 版 BatchExportItem。</summary>
public sealed record BatchExportItem(
    Guid Id,
    string SourcePath,
    string SourceVideoName,     // 不含扩展名
    double StartTime,
    double EndTime,
    int SequenceNumber)         // 分镜在所属视频内的编号（1-based）
{
    /// <summary>默认文件名：{编号}_{原视频名}.mp4</summary>
    public string FileName => $"{SequenceNumber}_{SourceVideoName}.mp4";
    public double Duration => Math.Max(0, EndTime - StartTime);
}

/// <summary>批量导出进度快照。对应 macOS 版 SegmentBatchExportProgress。</summary>
public sealed record SegmentBatchExportProgress(
    int TotalCount,
    int CompletedCount,
    BatchExportItem? CurrentItem,
    double CurrentItemProgress,           // 0...1
    IReadOnlyList<(BatchExportItem Item, string Error)> FailedItems)
{
    public double TotalProgress => TotalCount > 0
        ? (CompletedCount + CurrentItemProgress) / TotalCount
        : 0;
}

/// <summary>
/// 批量分镜导出服务。对应 macOS 版 BatchSegmentExportService。
///
/// 每个分镜按 start/end 用 trim+setpts 切出独立 MP4（重编码）—— 与 macOS 一致，
/// 不再用流复制，保证第一帧立即有画面。注册为单例服务。
/// </summary>
public sealed class BatchSegmentExportService
{
    private readonly FFmpegRunner _ffmpeg;
    private readonly ILogger<BatchSegmentExportService> _logger;

    public BatchSegmentExportService(FFmpegRunner ffmpeg, ILogger<BatchSegmentExportService> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>
    /// 执行批量导出。
    /// </summary>
    /// <returns>(成功数, 失败列表)。</returns>
    public async Task<(int Succeeded, IReadOnlyList<(BatchExportItem, string)> Failed)> ExportAllAsync(
        IReadOnlyList<BatchExportItem> items,
        string outputDirectory,
        Action<SegmentBatchExportProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var succeeded = 0;
        var failed = new List<(BatchExportItem, string)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = items.Count;

        for (var idx = 0; idx < total; idx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[idx];

            onProgress?.Invoke(new SegmentBatchExportProgress(
                TotalCount: total,
                CompletedCount: idx,
                CurrentItem: item,
                CurrentItemProgress: 0,
                FailedItems: failed));

            var outputPath = UniqueDestination(outputDirectory, item.FileName, usedNames);

            try
            {
                await _ffmpeg.CutSegmentAsync(
                    item.SourcePath, item.StartTime, item.EndTime, outputPath,
                    reencode: true, cancellationToken: cancellationToken);
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add((item, ex.Message));
                _logger.LogError("批量导出失败 {File}: {Msg}", item.FileName, ex.Message);
            }

            onProgress?.Invoke(new SegmentBatchExportProgress(
                TotalCount: total,
                CompletedCount: idx + 1,
                CurrentItem: idx + 1 < total ? items[idx + 1] : null,
                CurrentItemProgress: 0,
                FailedItems: failed));

            // 让出片刻让 UI 有时间刷新
            await Task.Delay(10, cancellationToken);
        }

        return (succeeded, failed);
    }

    /// <summary>
    /// 生成唯一文件名：磁盘已有 或 本批次内已用 → 自动加 (1) (2) 后缀。
    /// 对齐 macOS uniqueDestination。
    /// </summary>
    private static string UniqueDestination(string directory, string preferredName, HashSet<string> usedInBatch)
    {
        bool Exists(string name) =>
            usedInBatch.Contains(name) || File.Exists(Path.Combine(directory, name));

        if (!Exists(preferredName))
        {
            usedInBatch.Add(preferredName);
            return Path.Combine(directory, preferredName);
        }

        var stem = Path.GetFileNameWithoutExtension(preferredName);
        var ext = Path.GetExtension(preferredName);    // 含 "."，如 ".mp4"
        var counter = 1;
        var candidate = preferredName;
        while (Exists(candidate))
        {
            candidate = string.IsNullOrEmpty(ext)
                ? $"{stem} ({counter})"
                : $"{stem} ({counter}){ext}";
            counter++;
            if (counter > 9999) break;   // 防御无限循环
        }
        usedInBatch.Add(candidate);
        return Path.Combine(directory, candidate);
    }
}
