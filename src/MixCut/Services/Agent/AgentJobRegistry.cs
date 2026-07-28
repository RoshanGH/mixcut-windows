namespace MixCut.Services.Agent;

/// <summary>
/// Agent 发起的异步任务（导入/重试/生成/导出/配音）。内存态，app 重启即失效
/// （启动自愈由 App.ResetStaleAnalyzingStatus 等兜底，Agent 改查 get_project 即可）。
/// </summary>
public sealed class AgentJob
{
    public enum JobState { Running, Completed, Failed }

    public required Guid Id { get; init; }
    /// <summary>工具名（import_videos / retry_analysis / retry_asr / generate_schemes /
    /// export_scheme / export_segments / generate_voice_variants）。</summary>
    public required string Kind { get; init; }
    public required Guid ProjectId { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; set; }
    public JobState State { get; set; } = JobState.Running;
    /// <summary>导入类 job 的结构化报告（JSON 文本，get_job 时解码回对象）。</summary>
    public string? ReportJson { get; set; }
    /// <summary>非导入类 job 的结构化结果（JSON 文本，get_job 时解码回对象）。</summary>
    public string? ResultJson { get; set; }
    public string? FailureMessage { get; set; }

    public string StateName => State switch
    {
        JobState.Running => "running",
        JobState.Completed => "completed",
        _ => "failed",
    };
}

/// <summary>
/// Agent 任务注册表：同一时间只允许一个任务运行（与 UI 串行行为一致，
/// 规避共享写路径的并发踩踏）。全部在 UI 线程访问（工具调用已编组到 Dispatcher）。
/// </summary>
public sealed class AgentJobRegistry
{
    private readonly Dictionary<Guid, AgentJob> _jobs = new();

    public AgentJob? ActiveJob => _jobs.Values.FirstOrDefault(j => j.State == AgentJob.JobState.Running);

    public AgentJob Begin(string kind, Guid projectId)
    {
        var job = new AgentJob
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            ProjectId = projectId,
            StartedAt = DateTime.Now,
        };
        _jobs[job.Id] = job;
        return job;
    }

    /// <summary>无结构化结果的收尾（retry 类 job）。</summary>
    public void Finish(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }
        job.State = AgentJob.JobState.Completed;
        job.FinishedAt = DateTime.Now;
    }

    public void FinishWithReport(Guid id, string reportJson)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }
        job.State = AgentJob.JobState.Completed;
        job.FinishedAt = DateTime.Now;
        job.ReportJson = reportJson;
    }

    public void FinishWithResult(Guid id, string resultJson)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }
        job.State = AgentJob.JobState.Completed;
        job.FinishedAt = DateTime.Now;
        job.ResultJson = resultJson;
    }

    public void Fail(Guid id, string message)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return;
        }
        job.State = AgentJob.JobState.Failed;
        job.FinishedAt = DateTime.Now;
        job.FailureMessage = message;
    }

    public AgentJob? Find(Guid id) => _jobs.GetValueOrDefault(id);
}
