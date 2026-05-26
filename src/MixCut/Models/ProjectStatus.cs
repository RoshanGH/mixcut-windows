namespace MixCut.Models;

/// <summary>项目状态。对应 macOS 版 ProjectStatus（7 种）。</summary>
public enum ProjectStatus
{
    /// <summary>刚创建。</summary>
    Created,
    /// <summary>导入素材中。</summary>
    Importing,
    /// <summary>AI 分析中。</summary>
    Analyzing,
    /// <summary>分析完成，可生成方案。</summary>
    Ready,
    /// <summary>生成方案中。</summary>
    Generating,
    /// <summary>已完成。</summary>
    Completed,
    /// <summary>已归档。</summary>
    Archived,
}

public static class ProjectStatusExtensions
{
    private static readonly Dictionary<ProjectStatus, string> Labels = new()
    {
        [ProjectStatus.Created] = "新建",
        [ProjectStatus.Importing] = "导入中",
        [ProjectStatus.Analyzing] = "分析中",
        [ProjectStatus.Ready] = "就绪",
        [ProjectStatus.Generating] = "生成中",
        [ProjectStatus.Completed] = "已完成",
        [ProjectStatus.Archived] = "已归档",
    };

    /// <summary>中文显示名。</summary>
    public static string ToLabel(this ProjectStatus status) => Labels[status];
}
