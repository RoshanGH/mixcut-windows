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

    /// <summary>加载所有项目（含统计所需的导航数据）。已归档项目不显示在主列表。</summary>
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
            .Where(p => p.Status != ProjectStatus.Archived)
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

        NewProjectName = string.Empty;
        IsCreatingProject = false;
        FetchProjects();
        SelectedProject = Projects.FirstOrDefault(p => p.Id == project.Id);
        _logger.LogInformation("创建项目: {Name}", name);
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

        using var db = _dbFactory.CreateDbContext();
        var tracked = db.Projects
            .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
            .AsSplitQuery() // P0-8：嵌套集合拆分查询，避免笛卡尔放大
            .FirstOrDefault(p => p.Id == project.Id);
        if (tracked is null)
        {
            return;
        }

        // 收集该项目引用的视频（删除关联前）。
        var referencedVideos = tracked.ProjectVideos
            .Where(pv => pv.Video is not null)
            .Select(pv => pv.Video!)
            .ToList();

        db.Projects.Remove(tracked); // 级联删除 ProjectVideo / 策略 / 方案 / 方案分镜
        db.SaveChanges();

        // 检查每个视频是否还被其他项目引用。
        foreach (var video in referencedVideos)
        {
            var stillReferenced = db.ProjectVideos.Any(pv => pv.VideoId == video.Id);
            if (stillReferenced)
            {
                continue;
            }

            var segThumbnails = video.Segments
                .Where(s => !string.IsNullOrEmpty(s.ThumbnailPath))
                .Select(s => s.ThumbnailPath!)
                .ToList();

            var dbVideo = db.Videos.FirstOrDefault(v => v.Id == video.Id);
            if (dbVideo is not null)
            {
                db.Videos.Remove(dbVideo); // 级联删除分镜、方案分镜
                db.SaveChanges();
            }

            FileHelper.DeleteGlobalVideoFiles(video.LocalPath, video.ThumbnailPath);
            foreach (var thumb in segThumbnails)
            {
                TryDeleteFile(thumb);
            }
            _logger.LogInformation("视频无引用，已删除: {Name}", video.Name);
        }

        FetchProjects();
    }

    /// <summary>归档项目。</summary>
    [RelayCommand]
    private void ArchiveProject(Project project)
    {
        UpdateProject(project.Id, p => p.Status = ProjectStatus.Archived);
        FetchProjects();
    }

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
