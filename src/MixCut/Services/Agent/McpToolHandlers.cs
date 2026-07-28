using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Services.Export;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;
using MixCut.ViewModels;

namespace MixCut.Services.Agent;

/// <summary>
/// MCP 工具实现层（issue #23，对齐 macOS MCPToolHandlers）：
/// 所有调用已由 AgentGateway 编组到 UI 线程执行（单写者，与 UI 共用同一写路径）；
/// 数据访问走 IDbContextFactory 短上下文（本仓库统一契约）；写库后广播 UI 全量重载。
/// 本文件：入口分发 + 5 个只读工具 + 项目级写工具。分镜级工具见 McpToolHandlers.Segments.cs。
/// </summary>
public sealed partial class McpToolHandlers
{
    public readonly record struct Outcome(string Json, bool IsError);

    /// <summary>工具层结构化错误（8 个错误码全集，message 一律中文人话）。</summary>
    private sealed class ToolFailure : Exception
    {
        public AgentToolErrorCode Code { get; }
        public ToolFailure(AgentToolErrorCode code, string message) : base(message) => Code = code;
    }

    private static readonly JsonSerializerOptions ArgsOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IDbContextFactory<MixCutDbContext> _dbFactory;
    private readonly ImportViewModel _importVM;
    private readonly DubbingViewModel _dubbingVM;
    private readonly SchemeViewModel _schemeVM;
    private readonly ProjectViewModel _projectVM;
    private readonly ExportService _exportService;
    private readonly BatchSegmentExportService _batchExportService;
    private readonly FFmpegRunner _ffmpeg;
    private readonly AppSettings _settings;
    private readonly AgentJobRegistry _jobs;
    private readonly ILogger _logger;

    public McpToolHandlers(
        IDbContextFactory<MixCutDbContext> dbFactory,
        ImportViewModel importVM,
        DubbingViewModel dubbingVM,
        SchemeViewModel schemeVM,
        ProjectViewModel projectVM,
        ExportService exportService,
        BatchSegmentExportService batchExportService,
        FFmpegRunner ffmpeg,
        AppSettings settings,
        AgentJobRegistry jobs,
        ILogger logger)
    {
        _dbFactory = dbFactory;
        _importVM = importVM;
        _dubbingVM = dubbingVM;
        _schemeVM = schemeVM;
        _projectVM = projectVM;
        _exportService = exportService;
        _batchExportService = batchExportService;
        _ffmpeg = ffmpeg;
        _settings = settings;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<Outcome> CallAsync(string name, string argumentsJson)
    {
        try
        {
            return name switch
            {
                "list_projects" => await ListProjectsAsync(),
                "get_project" => await GetProjectAsync(argumentsJson),
                "list_segments" => await ListSegmentsAsync(argumentsJson),
                "list_schemes" => await ListSchemesAsync(argumentsJson),
                "get_job" => await GetJobAsync(argumentsJson),
                "create_project" => await CreateProjectAsync(argumentsJson),
                "import_videos" => await ImportVideosAsync(argumentsJson),
                "retry_analysis" => await RetryPipelineAsync(argumentsJson, "retry_analysis"),
                "retry_asr" => await RetryPipelineAsync(argumentsJson, "retry_asr"),
                "remove_video" => await RemoveVideoAsync(argumentsJson),
                "generate_schemes" => await GenerateSchemesAsync(argumentsJson),
                "export_scheme" => await ExportSchemeAsync(argumentsJson),
                "delete_project" => await DeleteProjectAsync(argumentsJson),
                "update_segment_tags" => await UpdateSegmentTagsAsync(argumentsJson),
                "adjust_segment_boundary" => await AdjustSegmentBoundaryAsync(argumentsJson),
                "set_subtitle_mode" => await SetSubtitleModeAsync(argumentsJson),
                "set_voice_keep_original" => await SetVoiceKeepOriginalAsync(argumentsJson),
                "set_dub_participation" => await SetDubParticipationAsync(argumentsJson),
                "generate_voice_variants" => await GenerateVoiceVariantsAsync(argumentsJson),
                "delete_segments" => await DeleteSegmentsAsync(argumentsJson),
                "create_custom_scheme" => await CreateCustomSchemeAsync(argumentsJson),
                "export_segments" => await ExportSegmentsAsync(argumentsJson),
                _ => Failure(AgentToolErrorCode.InvalidArgument, $"未知工具：{name}"),
            };
        }
        catch (ToolFailure failure)
        {
            return Failure(failure.Code, failure.Message);
        }
        catch (JsonException)
        {
            return Failure(AgentToolErrorCode.InvalidArgument, "参数解析失败：缺少必填字段或类型不符");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MCP] 工具 {Tool} 执行异常", name);
            return Failure(AgentToolErrorCode.InvalidArgument, ExceptionTranslator.ToUserMessage(ex));
        }
    }

    // ==== 只读工具 ====

    private async Task<Outcome> ListProjectsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var projects = await db.Projects
            .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
            .Include(p => p.Schemes)
            .AsSplitQuery()
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync();
        var items = projects.Select(p => new Dictionary<string, object?>
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["status"] = StatusName(p.Status),
            // 用非空视频数（与 videos 列表口径一致），不用 ProjectVideos.Count（历史数据存在悬空关联）
            ["video_count"] = p.VideoCount,
            ["segment_count"] = p.SegmentCount,
            ["scheme_count"] = p.SchemeCount,
            ["updated_at"] = Iso(p.UpdatedAt),
        }).ToList();
        return Success(new Dictionary<string, object?> { ["projects"] = items });
    }

    private sealed record GetProjectArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId);

    private async Task<Outcome> GetProjectAsync(string argsJson)
    {
        var args = Parse<GetProjectArgs>(argsJson);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await FetchProjectAsync(db, Require(args.ProjectId, "project_id"));
        var videos = OrderedVideos(project);
        return Success(new Dictionary<string, object?>
        {
            ["id"] = project.Id,
            ["name"] = project.Name,
            ["status"] = StatusName(project.Status),
            ["created_at"] = Iso(project.CreatedAt),
            ["updated_at"] = Iso(project.UpdatedAt),
            ["videos"] = videos.Select((v, idx) =>
            {
                var summary = VideoSummary(v);
                summary["video_no"] = idx + 1;
                return summary;
            }).ToList(),
        });
    }

    private sealed record ListSegmentsArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("video_id")] string? VideoId,
        [property: JsonPropertyName("semantic_type")] string? SemanticType,
        [property: JsonPropertyName("position_type")] string? PositionType);

    private async Task<Outcome> ListSegmentsAsync(string argsJson)
    {
        var args = Parse<ListSegmentsArgs>(argsJson);
        await using var db = await _dbFactory.CreateDbContextAsync();
        List<Video> videos;
        switch (args.ProjectId, args.VideoId)
        {
            case (not null, not null):
                throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "project_id 与 video_id 只能提供一个");
            case (not null, null):
                var project = await FetchProjectAsync(db, args.ProjectId, includeDubs: true);
                videos = OrderedVideos(project);
                break;
            case (null, not null):
                videos = new List<Video> { await FetchVideoAsync(db, args.VideoId, includeDubs: true) };
                break;
            default:
                throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "必须提供 project_id 或 video_id 之一");
        }

        var items = new List<Dictionary<string, object?>>();
        foreach (var video in videos)
        {
            var sorted = OrderedSegments(video);
            for (var idx = 0; idx < sorted.Count; idx++)
            {
                var seg = sorted[idx];
                var types = seg.SemanticTypes.Select(t => t.ToLabel()).ToList();
                if (args.SemanticType is { } st && !types.Contains(st))
                {
                    continue;
                }
                if (args.PositionType is { } pt && seg.PositionType.ToLabel() != pt)
                {
                    continue;
                }
                items.Add(new Dictionary<string, object?>
                {
                    ["id"] = seg.Id,
                    ["segment_no"] = idx + 1,
                    ["segment_index"] = seg.SegmentIndex,
                    ["video_id"] = video.Id,
                    ["video_name"] = video.Name,
                    ["start_frame"] = seg.StartFrame,
                    ["end_frame"] = seg.EndFrame,
                    ["start_sec"] = seg.StartTime,
                    ["end_sec"] = seg.EndTime,
                    ["duration_sec"] = seg.Duration,
                    ["text"] = seg.Text,
                    ["semantic_types"] = types,
                    ["position_type"] = seg.PositionType.ToLabel(),
                    ["confidence"] = seg.Confidence,
                    ["quality_score"] = seg.QualityScore,
                    ["is_voice_locked"] = seg.IsVoiceLocked,
                    ["subtitle_mode"] = SubtitleModeName(seg),
                    ["font_ratio"] = EffectiveFontRatio(seg),
                    ["voice_variant_count"] = seg.EffectiveDubVariants.Count,
                    ["has_stale_dubs"] = HasStaleDubs(seg),
                });
            }
        }
        return Success(new Dictionary<string, object?> { ["segments"] = items, ["count"] = items.Count });
    }

    private sealed record ListSchemesArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId);

    private async Task<Outcome> ListSchemesAsync(string argsJson)
    {
        var args = Parse<ListSchemesArgs>(argsJson);
        var projectId = ParseUuid(Require(args.ProjectId, "project_id"), "project_id");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var exists = await db.Projects.AnyAsync(p => p.Id == projectId);
        if (!exists)
        {
            throw new ToolFailure(AgentToolErrorCode.ProjectNotFound, $"找不到项目 {args.ProjectId}");
        }
        var schemes = await db.Schemes
            .Include(s => s.Strategy)
            .Include(s => s.SchemeSegments).ThenInclude(ss => ss.Segment!).ThenInclude(seg => seg.Video)
            .AsSplitQuery()
            .AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .ToListAsync();
        var items = schemes
            .OrderBy(s => s.Strategy?.Name ?? "")
            .ThenBy(s => s.VariationIndex)
            .Select(s => new Dictionary<string, object?>
            {
                ["id"] = s.Id,
                ["name"] = s.Name,
                ["style"] = s.Style,
                ["strategy"] = s.Strategy?.Name ?? "未分组",
                ["variation_index"] = s.VariationIndex,
                ["description"] = s.SchemeDescription,
                ["target_audience"] = s.TargetAudience,
                // 实时计算的总时长（存储的 EstimatedDuration 在边界改动后会陈旧）
                ["total_duration_sec"] = s.TotalDuration,
                ["segment_count"] = s.SegmentCount,
                ["segment_indexes"] = s.OrderedSegments
                    .Where(ss => ss.Segment is not null)
                    .Select(ss => ss.Segment!.SegmentIndex)
                    .ToList(),
            }).ToList();
        return Success(new Dictionary<string, object?> { ["schemes"] = items, ["count"] = items.Count });
    }

    private sealed record GetJobArgs(
        [property: JsonPropertyName("job_id")] string? JobId);

    private async Task<Outcome> GetJobAsync(string argsJson)
    {
        var args = Parse<GetJobArgs>(argsJson);
        var raw = Require(args.JobId, "job_id");
        if (!Guid.TryParse(raw, out var jobId))
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "job_id 不是合法 UUID");
        }
        var job = _jobs.Find(jobId)
            ?? throw new ToolFailure(
                AgentToolErrorCode.JobNotFound,
                $"找不到任务 {raw}（app 重启后任务会丢失，请改用 get_project 查看视频实时状态）");

        var payload = new Dictionary<string, object?>
        {
            ["id"] = job.Id,
            ["kind"] = job.Kind,
            ["state"] = job.StateName,
            ["project_id"] = job.ProjectId,
            ["started_at"] = Iso(job.StartedAt),
        };
        if (job.FinishedAt is { } finished)
        {
            payload["finished_at"] = Iso(finished);
        }
        if (job.FailureMessage is { } message)
        {
            payload["failure_message"] = message;
        }
        if (job.ResultJson is { } resultJson)
        {
            payload["result"] = JsonSerializer.Deserialize<JsonElement>(resultJson);
        }
        if (job.ReportJson is { } reportJson)
        {
            payload["report"] = JsonSerializer.Deserialize<JsonElement>(reportJson);
        }
        // 项目内所有视频的实时流水线状态（直接读库，非缓存快照）
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects
            .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == job.ProjectId);
        if (project is not null)
        {
            payload["videos"] = OrderedVideos(project).Select(VideoSummary).ToList();
        }
        return Success(payload);
    }

    // ==== 项目级写工具 ====

    private sealed record CreateProjectArgs(
        [property: JsonPropertyName("name")] string? Name);

    private Task<Outcome> CreateProjectAsync(string argsJson)
    {
        var args = Parse<CreateProjectArgs>(argsJson);
        var trimmed = (args.Name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "项目名称不能为空");
        }
        // 与 UI 的 ProjectViewModel.CreateProject 同一路径：同步创建「自定义组合」策略容器
        var project = _projectVM.CreateProjectCore(trimmed);
        AgentGateway.NotifyUiReload();
        return Task.FromResult(Success(new Dictionary<string, object?>
        {
            ["id"] = project.Id,
            ["name"] = project.Name,
        }));
    }

    private sealed record ImportVideosArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("paths")] List<string>? Paths);

    private async Task<Outcome> ImportVideosAsync(string argsJson)
    {
        var args = Parse<ImportVideosArgs>(argsJson);
        Guid projectId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            projectId = (await FetchProjectAsync(db, Require(args.ProjectId, "project_id"))).Id;
        }
        if (args.Paths is null || args.Paths.Count == 0)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "paths 不能为空");
        }
        foreach (var path in args.Paths)
        {
            if (!Path.IsPathRooted(path))
            {
                throw new ToolFailure(AgentToolErrorCode.InvalidArgument, $"必须是绝对路径：{path}");
            }
            if (!File.Exists(path) || !CanReadFile(path))
            {
                throw new ToolFailure(AgentToolErrorCode.FileNotFound, $"文件不存在或不可读：{path}");
            }
        }
        EnsureNoActiveJob();
        var job = _jobs.Begin("import_videos", projectId);
        var paths = args.Paths.ToList();
        _ = RunJobAsync(job.Id, async () =>
        {
            var report = await _importVM.ImportVideosAsync(paths, projectId);
            _jobs.FinishWithReport(job.Id, AgentJson.Encode(new Dictionary<string, object?>
            {
                ["imported"] = report.ImportedNames,
                ["linked_existing"] = report.LinkedExistingNames,
                ["skipped_duplicates"] = report.SkippedDuplicateNames,
                ["failed"] = report.Failed
                    .Select(f => new Dictionary<string, object?> { ["name"] = f.Name, ["reason"] = f.Reason })
                    .ToList(),
                ["abort_message"] = report.AbortMessage,
            }));
            AgentGateway.NotifyUiReload();
        });
        return Success(new Dictionary<string, object?> { ["job_id"] = job.Id });
    }

    private sealed record RetryArgs(
        [property: JsonPropertyName("video_id")] string? VideoId);

    private async Task<Outcome> RetryPipelineAsync(string argsJson, string kind)
    {
        var args = Parse<RetryArgs>(argsJson);
        Guid videoId;
        Guid projectId;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var video = await FetchVideoAsync(db, Require(args.VideoId, "video_id"));
            videoId = video.Id;
            var pv = await db.ProjectVideos.AsNoTracking()
                .Where(x => x.VideoId == videoId && x.ProjectId != null)
                .OrderBy(x => x.AddedAt)
                .FirstOrDefaultAsync();
            projectId = pv?.ProjectId
                ?? throw new ToolFailure(AgentToolErrorCode.ProjectNotFound, "视频未关联任何项目，无法重试");
        }
        EnsureNoActiveJob();
        var job = _jobs.Begin(kind, projectId);
        _ = RunJobAsync(job.Id, async () =>
        {
            if (kind == "retry_asr")
            {
                await _importVM.RetryASRAsync(videoId);
            }
            else
            {
                await _importVM.RetryAnalysisAsync(videoId);
            }
            _jobs.Finish(job.Id);
            AgentGateway.NotifyUiReload();
        });
        return Success(new Dictionary<string, object?> { ["job_id"] = job.Id });
    }

    private sealed record RemoveVideoArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("video_id")] string? VideoId);

    private async Task<Outcome> RemoveVideoAsync(string argsJson)
    {
        var args = Parse<RemoveVideoArgs>(argsJson);
        Guid projectId;
        Guid videoId;
        bool deletedGlobally;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var project = await FetchProjectAsync(db, Require(args.ProjectId, "project_id"));
            var video = await FetchVideoAsync(db, Require(args.VideoId, "video_id"));
            projectId = project.Id;
            videoId = video.Id;
            var inProject = await db.ProjectVideos.AnyAsync(pv => pv.ProjectId == projectId && pv.VideoId == videoId);
            if (!inProject)
            {
                throw new ToolFailure(AgentToolErrorCode.VideoNotFound, "视频不在该项目中");
            }
            // 仅当无其它项目引用时才会连分镜记录全局删除（与 ImportViewModel.DeleteVideo 判定一致）
            deletedGlobally = !await db.ProjectVideos
                .AnyAsync(pv => pv.VideoId == videoId && pv.ProjectId != projectId);
        }
        _importVM.DeleteVideo(videoId, projectId);
        AgentGateway.NotifyUiReload();
        return Success(new Dictionary<string, object?>
        {
            ["removed"] = true,
            ["video_deleted_globally"] = deletedGlobally,
        });
    }

    private sealed record GenerateSchemesArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("target_count")] int? TargetCount,
        [property: JsonPropertyName("custom_prompt")] string? CustomPrompt);

    private async Task<Outcome> GenerateSchemesAsync(string argsJson)
    {
        var args = Parse<GenerateSchemesArgs>(argsJson);
        Project projectSnapshot;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var project = await FetchProjectAsync(db, Require(args.ProjectId, "project_id"));
            if (project.SegmentCount == 0)
            {
                throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "项目没有可用分镜，请先导入并完成分析");
            }
            projectSnapshot = project;
        }
        EnsureNoActiveJob();
        var job = _jobs.Begin("generate_schemes", projectSnapshot.Id);
        var targetCount = args.TargetCount ?? 50;
        var customPrompt = args.CustomPrompt;
        _ = RunJobAsync(job.Id, async () =>
        {
            await _schemeVM.GenerateSchemesAsync(projectSnapshot, targetCount, customPrompt);
            if (_schemeVM.ErrorMessage is { } error)
            {
                _jobs.Fail(job.Id, error);
            }
            else
            {
                int schemeCount;
                await using (var db = await _dbFactory.CreateDbContextAsync())
                {
                    schemeCount = await db.Schemes.CountAsync(s => s.ProjectId == projectSnapshot.Id);
                }
                _jobs.FinishWithResult(job.Id, AgentJson.Encode(new Dictionary<string, object?>
                {
                    ["scheme_count"] = schemeCount,
                    ["failed_strategies"] = _schemeVM.LastFailedStrategyCount,
                }));
            }
            AgentGateway.NotifyUiReload();
        });
        return Success(new Dictionary<string, object?> { ["job_id"] = job.Id });
    }

    private sealed record ExportSchemeArgs(
        [property: JsonPropertyName("scheme_ids")] List<string>? SchemeIds,
        [property: JsonPropertyName("output_dir")] string? OutputDir);

    private async Task<Outcome> ExportSchemeAsync(string argsJson)
    {
        var args = Parse<ExportSchemeArgs>(argsJson);
        if (args.SchemeIds is null || args.SchemeIds.Count == 0)
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "scheme_ids 不能为空");
        }
        var outputDir = Require(args.OutputDir, "output_dir");
        ValidateOutputDirectory(outputDir);

        // 先校验并提取全部导出任务，任何一个方案有问题整个调用不执行
        var tasks = new List<(ExportInput Input, string OutputPath, string Name)>();
        Guid projectId = Guid.Empty;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            foreach (var idString in args.SchemeIds)
            {
                var scheme = await FetchSchemeAsync(db, idString);
                if (scheme.ProjectId is { } pid)
                {
                    projectId = pid;
                }
                var input = ExportInput.FromScheme(scheme)
                    ?? throw new ToolFailure(AgentToolErrorCode.InvalidArgument, $"方案「{scheme.Name}」没有有效分镜，无法导出");
                // 命名规则与 UI 批量导出一致：策略名_变体号_方案名.mp4
                var strategyName = scheme.Strategy?.Name ?? "未分组";
                var sanitized = SanitizeFilename($"{strategyName}_{scheme.VariationIndex}_{scheme.Name}");
                tasks.Add((input, Path.Combine(outputDir, $"{sanitized}.mp4"), scheme.Name));
            }
        }

        EnsureNoActiveJob();
        var job = _jobs.Begin("export_scheme", projectId);
        _ = RunJobAsync(job.Id, async () =>
        {
            var exported = new List<string>();
            var failures = new List<Dictionary<string, object?>>();
            foreach (var task in tasks)
            {
                try
                {
                    await _exportService.ExportAsync(task.Input, task.OutputPath);
                    exported.Add(task.OutputPath);
                }
                catch (Exception ex)
                {
                    failures.Add(new Dictionary<string, object?>
                    {
                        ["scheme"] = task.Name,
                        ["reason"] = ExportErrorMessage.ToFriendly(ex),
                    });
                }
            }
            if (exported.Count == 0 && failures.Count > 0)
            {
                _jobs.Fail(job.Id, $"全部导出失败：{failures[0]["reason"]}");
            }
            else
            {
                _jobs.FinishWithResult(job.Id, AgentJson.Encode(new Dictionary<string, object?>
                {
                    ["exported"] = exported,
                    ["failed"] = failures,
                }));
            }
        });
        return Success(new Dictionary<string, object?> { ["job_id"] = job.Id });
    }

    private sealed record DeleteProjectArgs(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("confirm")] bool? Confirm);

    private async Task<Outcome> DeleteProjectAsync(string argsJson)
    {
        var args = Parse<DeleteProjectArgs>(argsJson);
        Guid projectId;
        string projectName;
        int videoCount;
        int schemeCount;
        List<string> wouldDeleteGlobally;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var project = await FetchProjectAsync(db, Require(args.ProjectId, "project_id"));
            projectId = project.Id;
            projectName = project.Name;
            var videos = project.Videos.ToList();
            videoCount = videos.Count;
            schemeCount = project.Schemes.Count;
            var videoIds = videos.Select(v => v.Id).ToList();
            var multiReferenced = (await db.ProjectVideos
                .Where(pv => pv.VideoId != null && videoIds.Contains(pv.VideoId.Value) && pv.ProjectId != projectId)
                .Select(pv => pv.VideoId!.Value)
                .Distinct()
                .ToListAsync()).ToHashSet();
            wouldDeleteGlobally = videos
                .Where(v => !multiReferenced.Contains(v.Id))
                .Select(v => v.Name)
                .ToList();
        }

        // 二次确认：未显式 confirm=true 时只返回影响预览，不执行（§1.7-3 产品级硬规则）
        if (args.Confirm != true)
        {
            return Success(new Dictionary<string, object?>
            {
                ["requires_confirmation"] = true,
                ["project"] = projectName,
                ["video_count"] = videoCount,
                ["scheme_count"] = schemeCount,
                ["videos_that_would_be_deleted_globally"] = wouldDeleteGlobally,
                ["message"] = "未执行删除。确认无误后，带 confirm=true 重新调用才会真正删除（无撤销）。",
            });
        }

        // 与 UI 的 ProjectViewModel.DeleteProject 同一路径，立即执行、无撤销
        var deletedVideoNames = _projectVM.DeleteProjectCore(projectId);
        AgentGateway.NotifyUiReload();
        return Success(new Dictionary<string, object?>
        {
            ["deleted"] = true,
            ["name"] = projectName,
            ["videos_deleted_globally"] = deletedVideoNames,
        });
    }

    // ==== 公共辅助 ====

    /// <summary>异步 job 的统一外壳：任何未捕获异常都翻译成人话记入 job，绝不静默丢失。</summary>
    private async Task RunJobAsync(Guid jobId, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MCP] job {JobId} 执行异常", jobId);
            _jobs.Fail(jobId, ExceptionTranslator.ToUserMessage(ex));
            AgentGateway.NotifyUiReload();
        }
    }

    private void EnsureNoActiveJob()
    {
        if (_jobs.ActiveJob is { } active)
        {
            throw new ToolFailure(
                AgentToolErrorCode.JobAlreadyRunning,
                $"已有任务在运行（job_id: {active.Id}），请等它完成后再发起");
        }
    }

    private static T Parse<T>(string argsJson) =>
        JsonSerializer.Deserialize<T>(argsJson, ArgsOptions)
            ?? throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "参数解析失败：缺少必填字段或类型不符");

    private static string Require(string? value, string fieldName) =>
        value ?? throw new ToolFailure(AgentToolErrorCode.InvalidArgument, $"缺少必填参数 {fieldName}");

    private static Guid ParseUuid(string raw, string fieldName)
    {
        if (!Guid.TryParse(raw, out var id))
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, $"{fieldName} 不是合法 UUID：{raw}");
        }
        return id;
    }

    /// <summary>按 id 取项目（含视频/分镜/方案导航）。找不到报 PROJECT_NOT_FOUND。</summary>
    private static async Task<Project> FetchProjectAsync(MixCutDbContext db, string idString, bool includeDubs = false)
    {
        var id = ParseUuid(idString, "project_id");
        var query = db.Projects
            .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
            .Include(p => p.Schemes)
            .AsQueryable();
        if (includeDubs)
        {
            query = query.Include(p => p.ProjectVideos)
                .ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments).ThenInclude(s => s.SegmentDubs);
        }
        var project = await query.AsSplitQuery().FirstOrDefaultAsync(p => p.Id == id);
        return project ?? throw new ToolFailure(AgentToolErrorCode.ProjectNotFound, $"找不到项目 {idString}");
    }

    private static async Task<Video> FetchVideoAsync(MixCutDbContext db, string idString, bool includeDubs = false)
    {
        var id = ParseUuid(idString, "video_id");
        var query = db.Videos.Include(v => v.Segments).AsQueryable();
        if (includeDubs)
        {
            query = query.Include(v => v.Segments).ThenInclude(s => s.SegmentDubs);
        }
        var video = await query.AsSplitQuery().FirstOrDefaultAsync(v => v.Id == id);
        return video ?? throw new ToolFailure(AgentToolErrorCode.VideoNotFound, $"找不到视频 {idString}");
    }

    private static async Task<MixScheme> FetchSchemeAsync(MixCutDbContext db, string idString)
    {
        var id = ParseUuid(idString, "scheme_id");
        var scheme = await db.Schemes
            .Include(s => s.Strategy)
            .Include(s => s.SchemeSegments).ThenInclude(ss => ss.Segment!).ThenInclude(seg => seg.Video)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id);
        return scheme ?? throw new ToolFailure(AgentToolErrorCode.SchemeNotFound, $"找不到方案 {idString}");
    }

    /// <summary>视频稳定顺序：项目内按导入时间（ProjectVideo.AddedAt）升序，1 起编号的依据。
    /// 覆盖项目内全部视频（含自建分镜载体），即使某些页面不显示它们。</summary>
    internal static List<Video> OrderedVideos(Project project) =>
        project.ProjectVideos
            .Where(pv => pv.Video is not null)
            .OrderBy(pv => pv.AddedAt)
            .Select(pv => pv.Video!)
            .ToList();

    /// <summary>分镜稳定顺序：视频内按开始帧升序（与 UI 卡片 # 编号口径一致）。</summary>
    internal static List<Segment> OrderedSegments(Video video) =>
        video.Segments.OrderBy(s => s.StartFrame).ToList();

    /// <summary>三档中文名：直接烧录 / 模糊虚化 / 纯色遮挡。</summary>
    private static string SubtitleModeName(Segment seg)
    {
        if (!seg.HasHardSubtitle)
        {
            return "直接烧录";
        }
        return seg.MaskStyle == MaskStyle.Solid ? "纯色遮挡" : "模糊虚化";
    }

    /// <summary>该分镜的有效字号比例：逐分镜值优先，未设置（≤0）跟随全局默认。</summary>
    private double EffectiveFontRatio(Segment seg) => seg.EffectiveSubtitleFontRatio(_settings.SubtitleFontRatio);

    /// <summary>某配音变体是否「按旧时长/旧文本合成」（快照三项任一不一致即 stale）。</summary>
    private static bool IsStaleDub(SegmentDub dub, Segment seg) =>
        dub.GeneratedForStartFrame != seg.StartFrame
        || dub.GeneratedForEndFrame != seg.EndFrame
        || dub.GeneratedForTextHash != DubbingViewModel.TextHash(dub.RewrittenText);

    /// <summary>该分镜是否存在过期配音（只看已生成音频的变体）。</summary>
    private static bool HasStaleDubs(Segment seg) =>
        seg.EffectiveDubVariants.Any(dub => IsStaleDub(dub, seg));

    private static Dictionary<string, object?> VideoSummary(Video video) => new()
    {
        ["id"] = video.Id,
        ["name"] = video.Name,
        ["status"] = StatusName(video.Status),
        ["duration_sec"] = video.Duration,
        ["error_message"] = video.ErrorMessage,
        ["segment_count"] = video.Segments.Count,
    };

    /// <summary>状态字符串与 macOS 版 rawValue 一致（Agent 侧看到同一套词汇）。</summary>
    private static string StatusName(ProjectStatus status) => status switch
    {
        ProjectStatus.Created => "created",
        ProjectStatus.Importing => "importing",
        ProjectStatus.Analyzing => "analyzing",
        ProjectStatus.Ready => "ready",
        ProjectStatus.Generating => "generating",
        ProjectStatus.Completed => "completed",
        _ => "created",
    };

    private static string StatusName(VideoStatus status) => status switch
    {
        VideoStatus.Imported => "imported",
        VideoStatus.DetectingScenes => "detecting_scenes",
        VideoStatus.Transcribing => "transcribing",
        VideoStatus.Analyzing => "analyzing",
        VideoStatus.Completed => "completed",
        VideoStatus.Failed => "failed",
        _ => "imported",
    };

    /// <summary>非法文件名字符换 _（与 UI 批量导出一致）。</summary>
    private static string SanitizeFilename(string name)
    {
        var illegal = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var parts = name.Split(illegal, StringSplitOptions.None);
        return string.Join("_", parts);
    }

    /// <summary>导出目录校验：不存在/不是目录/不可写都给人话说明。</summary>
    private static void ValidateOutputDirectory(string dir)
    {
        if (!Path.IsPathRooted(dir) || !Directory.Exists(dir))
        {
            throw new ToolFailure(AgentToolErrorCode.InvalidArgument, "选择的位置不存在或不是文件夹，请重新选择。");
        }
        try
        {
            var probe = Path.Combine(dir, $".mixcut_write_probe_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            throw new ToolFailure(
                AgentToolErrorCode.InvalidArgument,
                $"没有写入「{Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}」的权限，请换一个目录（例如「桌面」或「下载」）。");
        }
    }

    private static bool CanReadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Iso(DateTime date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static Outcome Success(object payload) => new(AgentJson.Encode(payload), false);

    private static Outcome Failure(AgentToolErrorCode code, string message) =>
        new(AgentJson.Error(code, message), true);
}
