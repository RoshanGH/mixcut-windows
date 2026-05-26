using System.IO;
using System.Security.Cryptography;

namespace MixCut.Utilities;

/// <summary>文件管理工具。对应 macOS 版 FileHelper。</summary>
public static class FileHelper
{
    /// <summary>全局缩略图存储目录。</summary>
    public static string GlobalThumbnailDirectory { get; } =
        CreateDir(Path.Combine(AppPaths.Root, "Thumbnails"));

    /// <summary>临时文件目录。</summary>
    public static string TempDirectory { get; } =
        CreateDir(Path.Combine(Path.GetTempPath(), "MixCut"));

    private static string CreateDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>计算文件 SHA-256 哈希（小写十六进制），用于视频全局去重。</summary>
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 拷贝视频文件到全局目录（按 hash 子目录存储，同一视频只保留一份）。
    /// 返回目标文件完整路径。
    /// </summary>
    public static string CopyVideoToGlobal(string sourcePath, string contentHash)
    {
        var hashDir = CreateDir(Path.Combine(AppPaths.VideosDirectory, contentHash));
        var destPath = Path.Combine(hashDir, Path.GetFileName(sourcePath));

        // 文件已存在则跳过（同 hash 同文件名，内容一定相同）。
        if (!File.Exists(destPath))
        {
            File.Copy(sourcePath, destPath);
        }
        return destPath;
    }

    /// <summary>删除全局视频文件（当无任何项目引用时调用）。</summary>
    public static void DeleteGlobalVideoFiles(string localPath, string? thumbnailPath)
    {
        if (File.Exists(localPath))
        {
            TryDelete(() => File.Delete(localPath));

            // 尝试删除空的 hash 子目录。
            var parentDir = Path.GetDirectoryName(localPath);
            if (parentDir is not null && Directory.Exists(parentDir)
                && !Directory.EnumerateFileSystemEntries(parentDir).Any())
            {
                TryDelete(() => Directory.Delete(parentDir));
            }
        }

        if (!string.IsNullOrEmpty(thumbnailPath) && File.Exists(thumbnailPath))
        {
            TryDelete(() => File.Delete(thumbnailPath!));
        }
    }

    /// <summary>项目导出目录。</summary>
    public static string ExportDirectory(Guid projectId) =>
        CreateDir(Path.Combine(AppPaths.Root, "Projects", projectId.ToString(), "Exports"));

    private static void TryDelete(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // 文件清理失败不应阻塞业务流程。
        }
    }
}
