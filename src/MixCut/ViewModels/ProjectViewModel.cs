using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Utilities;

namespace MixCut.ViewModels;

/// <summary>项目列表 ViewModel。对应 macOS 版 ProjectViewModel。</summary>
public partial class ProjectViewModel : ObservableObject
{
    private readonly IDbContextFactory<MixCutDbContext> _dbFactory;
    private readonly ILogger<ProjectViewModel> _logger;

    /// <summary>项目列表（按更新时间倒序）。</summary>
    public ObservableCollection<Project> Projects { get; } = new();

    [ObservableProperty]
    private Project? _selectedProject;

    [ObservableProperty]
    private bool _isCreatingProject;

    [ObservableProperty]
    private string _newProjectName = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public ProjectViewModel(IDbContextFactory<MixCutDbContext> dbFactory, ILogger<ProjectViewModel> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        FetchProjects();
    }

    /// <summary>加载所有项目（含统计所需的导航数据）。</summary>
    public void FetchProjects()
    {
        using var db = _dbFactory.CreateDbContext();
        var list = db.Projects
            .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
            .Include(p => p.Schemes)
            // P0-8：多集合 Include 默认 SingleQuery 会笛卡尔积爆炸（videos×segments×schemes），
            // 项目/分镜多时启动卡顿。AsSplitQuery 拆成多条查询，结果完全一致、零消费点改动。
            .AsSplitQuery()
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();

        Projects.Clear();
        foreach (var project in list)
        {
            Projects.Add(project);
        }
    }

    /// <summary>创建新项目。</summary>
    [RelayCommand]
    private void CreateProject()
    {
        var name = NewProjectName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var project = CreateProjectCore(name);

        NewProjectName = string.Empty;
        IsCreatingProject = false;
        FetchProjects();
        SelectedProject = Projects.FirstOrDefault(p => p.Id == project.Id);
    }

    /// <summary>
    /// 建项目的核心 DB 逻辑（UI 与 Agent 共用同一路径，issue #23）。
    /// 不动列表/选中状态——UI 调用方自己 FetchProjects，Agent 走全量重载广播。
    /// </summary>
    public Project CreateProjectCore(string name)
    {
        var project = new Project { Name = name };
        using (var db = _dbFactory.CreateDbContext())
        {
            db.Projects.Add(project);

            // v0.3.0 对齐：新项目同步创建「自定义组合」容器策略，让用户在「混剪方案」板块
            // 看到的左栏永远有这一项（即使还没生成任何 AI 策略），点击空状态可以引导去分镜库挑选。
            var customGroup = new MixStrategy
            {
                Name = "自定义组合",
                Style = string.Empty,
                StrategyDescription = "手动挑选分镜组合的方案",
                TargetAudience = string.Empty,
                NarrativeStructure = string.Empty,
                TargetDuration = 0,
                IsCustomGroup = true,
                Project = project,
            };
            db.Strategies.Add(customGroup);

            db.SaveChanges();
        }
        _logger.LogInformation("创建项目: {Name}", name);
        return project;
    }

    /// <summary>
    /// 删除项目。ProjectVideo/策略/方案级联删除；视频仅在无其他项目引用时才真正删除。
    /// </summary>
    [RelayCommand]
    private void DeleteProject(Project project)
    {
        if (SelectedProject?.Id == project.Id)
        {
            SelectedProject = null;
        }
        DeleteProjectCore(project.Id);
        FetchProjects();
    }

    /// <summary>
    /// 删项目的核心 DB 逻辑（UI 与 Agent 共用同一路径，issue #23）。
    /// 返回因无引用而被全局删除的视频名清单（Agent 的 delete_project 结果需要）。
    /// </summary>
    public IReadOnlyList<string> DeleteProjectCore(Guid projectId)
    {
        using var db = _dbFactory.CreateDbContext();
        var tracked = db.Projects
            .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
            .AsSplitQuery() // P0-8：嵌套集合拆分查询，避免笛卡尔放大
            .FirstOrDefault(p => p.Id == projectId);
        if (tracked is null)
        {
            return Array.Empty<string>();
        }

        // 收集该项目引用的视频（删除关联前）。
        var referencedVideos = tracked.ProjectVideos
            .Where(pv => pv.Video is not null)
            .Select(pv => pv.Video!)
            .ToList();

        db.Projects.Remove(tracked); // 级联删除 ProjectVideo / 策略 / 方案 / 方案分镜
        db.SaveChanges();

        // 检查哪些视频还被其他项目引用 —— 一次查完。
        // （原来是每个视频 1 次 Any + 1 次 SELECT + 1 次 SaveChanges，而每次 SaveChanges 都是一个
        //  独立的 SQLite 写事务 + fsync，是删项目卡住的主要来源。）
        var videoIds = referencedVideos.Select(v => v.Id).ToList();
        var stillReferencedIds = db.ProjectVideos
            .Where(pv => pv.VideoId != null && videoIds.Contains(pv.VideoId.Value))
            .Select(pv => pv.VideoId!.Value)
            .Distinct()
            .ToHashSet();

        var orphans = referencedVideos.Where(v => !stillReferencedIds.Contains(v.Id)).ToList();
        var deletedNames = orphans.Select(v => v.Name).ToList();
        if (orphans.Count > 0)
        {
            var orphanIds = orphans.Select(v => v.Id).ToList();
            var dbVideos = db.Videos.Where(v => orphanIds.Contains(v.Id)).ToList();
            db.Videos.RemoveRange(dbVideos); // 级联删除分镜、方案分镜
            db.SaveChanges();               // 一次写事务，不是每个视频一次

            // 文件删除放在 DB 提交之后统一做：DB 没删成功就不该动用户的文件。
            foreach (var video in orphans)
            {
                FileHelper.DeleteGlobalVideoFiles(video.LocalPath, video.ThumbnailPath);
                foreach (var thumb in video.Segments
                             .Where(s => !string.IsNullOrEmpty(s.ThumbnailPath))
                             .Select(s => s.ThumbnailPath!))
                {
                    TryDeleteFile(thumb);
                }
                _logger.LogInformation("视频无引用，已删除: {Name}", video.Name);
            }
        }

        return deletedNames;
    }

    // issue #16（对齐 macOS v0.8.1）：已移除「归档」——归档后项目从列表消失且无从恢复，
    // 是个只会误伤用户的无用功能。右键菜单现只保留「重命名」+ 带二次确认的「删除」。
    // 后续已彻底清干净：ProjectStatus.Archived 枚举、状态色/文案分支、这里的列表过滤全部删除，
    // 历史库里残留的归档项目由启动期 App.RestoreArchivedProjects 恢复成「已完成」重新可见。

    /// <summary>重命名项目。</summary>
    public void RenameProject(Project project, string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }
        UpdateProject(project.Id, p => p.Name = trimmed);
        FetchProjects();
    }

    private void UpdateProject(Guid projectId, Action<Project> mutate)
    {
        using var db = _dbFactory.CreateDbContext();
        var tracked = db.Projects.FirstOrDefault(p => p.Id == projectId);
        if (tracked is null)
        {
            return;
        }
        mutate(tracked);
        tracked.UpdatedAt = DateTime.Now;
        db.SaveChanges();
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("删除缩略图失败 {Path}: {Message}", path, ex.Message);
        }
    }
}
