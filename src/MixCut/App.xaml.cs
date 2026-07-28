using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Services.AI;
using MixCut.Services.ASR;
using MixCut.Services.BoundaryOptimizer;
using MixCut.Services.Export;
using MixCut.Services.SceneDetection;
using MixCut.Services.SchemeGeneration;
using MixCut.Services.VideoProcessing;
using MixCut.Utilities;
using MixCut.ViewModels;
using MixCut.Views;
using Serilog;

namespace MixCut;

/// <summary>
/// 应用入口。对应 macOS 版的 MixCutApp。
/// 使用 .NET 泛型主机（Generic Host）统一管理依赖注入、日志和生命周期。
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .UseSerilog((_, config) => config
                .MinimumLevel.Information()
                // 屏蔽 EF Core 的 SQL 调试噪音，否则一次查询能刷几十行 SQL 把业务日志淹没。
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .WriteTo.Console()
                .WriteTo.File(
                    Path.Combine(AppPaths.LogDirectory, "mixcut-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    // 显式 UTF-8（也是 Serilog 当前默认值），把编码钉死不随库版本漂移。
                    // 排查提示：日志文件本身是 UTF-8，但 Windows PowerShell 5.1 的 Get-Content
                    // 默认按系统 ANSI(GBK) 读，中文会显示成「鏈鐞嗗紓甯?」这种乱码 —— 那是读法不对，
                    // 不是日志坏了。读日志请加 -Encoding UTF8。
                    encoding: System.Text.Encoding.UTF8,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))
            .ConfigureServices(ConfigureServices)
            .Build();
    }

    /// <summary>注册依赖注入服务。</summary>
    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // 数据库：按操作创建短生命周期 DbContext，避免并发竞态。
        services.AddDbContextFactory<MixCutDbContext>(options =>
            options.UseSqlite($"Data Source={AppPaths.DatabaseFile}"));

        // 基础设施与服务层（单例）。
        services.AddSingleton<AppSettings>();
        services.AddSingleton<FFmpegRunner>();
        services.AddSingleton<SceneDetectionService>();
        services.AddSingleton<ASRService>();
        services.AddSingleton<PromptLoader>();
        services.AddSingleton<AIProviderManager>();
        services.AddSingleton<AIAnalysisService>();
        services.AddSingleton<Services.UserSegments.UserSegmentImportService>();   // #17 自建分镜上传
        services.AddSingleton<SchemeGenerationService>();
        services.AddSingleton<BoundaryOptimizerService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<BatchSegmentExportService>();
        services.AddSingleton<VariantBatchExportService>();  // 变体感知批量导出（原版 + 各配音变体）
        // v0.3.1 对齐：启动期版本检测（GitHub 优先 / Gitee 容灾）
        services.AddSingleton<Services.UpdateChecker.UpdateChecker>();

        // v0.5.0 分镜级 AI 配音：分离 / 克隆 / 合成 / 对齐 / 改写（单例）。
        services.AddSingleton<Services.Dubbing.VocalSeparationService>();
        services.AddSingleton<Services.Dubbing.VoiceCloneService>();
        services.AddSingleton<Services.Dubbing.CloneTtsClient>();
        services.AddSingleton<Services.Dubbing.DubAudioFinalizer>();
        services.AddSingleton<Services.Dubbing.ScriptRewriteService>();
        services.AddSingleton<Services.Dubbing.DubExportService>();
        services.AddSingleton<Services.Dubbing.ParaformerAsrClient>();
        services.AddSingleton<Services.Dubbing.SegmentReRecognizer>();

        // #22 全局 BGM 库 + 导出替换背景音乐（单例）。
        services.AddSingleton<Services.Bgm.BgmLibraryService>();

        // #12 分镜头 AI 画面替换：切分 / AI 变体 / 合成（单例）。
        services.AddSingleton<Services.ShotEdit.ShotSlicerService>();
        services.AddSingleton<Services.ShotEdit.WanVideoEditClient>();
        services.AddSingleton<Services.ShotEdit.ShotVariantService>();
        services.AddSingleton<Services.ShotEdit.ShotCompositionService>();
        services.AddTransient<ShotEditViewModel>();   // 每个分镜头工作区一个

        // ViewModel（单例，主窗口持有）。
        services.AddSingleton<ProjectViewModel>();
        services.AddSingleton<ImportViewModel>();
        services.AddSingleton<SegmentLibraryViewModel>();
        services.AddSingleton<SchemeViewModel>();
        services.AddSingleton<DubbingViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<UpdateBannerViewModel>();

        // issue #23 Agent 接入：内嵌本地 MCP server（仅 127.0.0.1，设置页可开关/改端口）。
        services.AddSingleton<Services.Agent.AgentGateway>();

        // 视图。
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<OnboardingWindow>();
    }

    /// <summary>
    /// 预览渲染自测：离屏起一个真 <see cref="Views.Components.FfmpegFramePlayer"/>，Open 一个视频，
    /// 数实际渲染帧数后退出。验证「进程外 ffmpeg 解码 → WPF WriteableBitmap 渲染」整链路在真机能跑
    /// （尤其 HEVC）。结果 `[PreviewSelfTest] frames=N` 同时进日志与 stdout，便于 SSH grep。
    /// </summary>
    private static async Task RunPreviewSelfTestAsync(string path)
    {
        try
        {
            var player = new Views.Components.FfmpegFramePlayer { Width = 320, Height = 180 };
            var failed = false;
            var reason = "";
            player.Failed += (_, r) => { failed = true; reason = r; };
            var host = new Window
            {
                Width = 360, Height = 240, Left = -3000, Top = -3000,
                ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = player,
            };
            host.Show();
            await Task.Delay(200); // 等布局生效（ActualWidth）

            player.Open(path, 0, 3);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(6) && !failed)
            {
                await Task.Delay(200);
                if (player.FramesRendered > 0 && !player.IsPlaying)
                {
                    break; // 已播完
                }
            }

            var line = $"[PreviewSelfTest] path={path} frames={player.FramesRendered} " +
                       $"lastPos={player.Position.TotalSeconds:F2} failed={failed} reason={reason}";
            Log.Information(line);
            Console.WriteLine(line);
            player.Stop();
            host.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PreviewSelfTest] 异常");
            Console.WriteLine("[PreviewSelfTest] EXCEPTION " + ex.Message);
        }
    }

    /// <summary>帧精确预览自测：验证输出帧数与最后一帧位置都严格匹配 [startFrame,endFrame)。</summary>
    private static async Task RunFramePreviewSelfTestAsync(
        string path, int startFrame, int endFrame, double fps)
    {
        try
        {
            var player = new Views.Components.FfmpegFramePlayer { Width = 320, Height = 180 };
            var failed = false;
            var ended = false;
            var reason = "";
            player.Failed += (_, r) => { failed = true; reason = r; };
            player.Ended += (_, _) => ended = true;
            var host = new Window
            {
                Width = 360, Height = 240, Left = -3000, Top = -3000,
                ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = player,
            };
            host.Show();
            await Task.Delay(200);

            var start = Utilities.FrameTime.FrameToSeconds(startFrame, fps);
            var duration = Utilities.FrameTime.FrameToSeconds(endFrame - startFrame, fps);
            player.Open(path, start, duration, startFrame: startFrame, endFrame: endFrame, sourceFps: fps);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(Math.Max(8, duration + 5))
                   && !failed && !ended)
            {
                await Task.Delay(100);
            }

            var expected = Math.Max(0, endFrame - startFrame);
            var expectedLast = Utilities.FrameTime.FrameToSeconds(
                Utilities.FrameTime.LastIncludedFrame(startFrame, endFrame), fps);
            var ok = ended && !failed && player.FramesRendered == expected
                             && Math.Abs(player.Position.TotalSeconds - expectedLast) < 0.5 / fps;
            var line = $"[FramePreviewSelfTest] ok={ok} expected={expected} actual={player.FramesRendered} " +
                       $"expectedLast={expectedLast:F6} actualLast={player.Position.TotalSeconds:F6} " +
                       $"range=[{startFrame},{endFrame}) fps={fps:F3} reason={reason}";
            Log.Information(line);
            Console.WriteLine(line);
            player.Stop();
            host.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FramePreviewSelfTest] 异常");
            Console.WriteLine("[FramePreviewSelfTest] EXCEPTION " + ex.Message);
        }
    }

    private static async Task RunFrameCutSelfTestAsync(
        string path, int startFrame, int endFrame, double fps, string outputPath)
    {
        try
        {
            var runner = new Services.VideoProcessing.FFmpegRunner(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    Services.VideoProcessing.FFmpegRunner>.Instance);
            await runner.CutSegmentFramesAsync(
                new Services.VideoProcessing.FrameClip(path, startFrame, endFrame, fps),
                outputPath);
            var frameText = await runner.RunProbeAsync(new[]
            {
                "-v", "error", "-select_streams", "v:0",
                "-count_frames", "-show_entries", "stream=nb_read_frames",
                "-of", "default=nokey=1:noprint_wrappers=1", outputPath,
            });
            var actual = int.TryParse(frameText.Trim(), out var count) ? count : -1;
            var expected = Math.Max(0, endFrame - startFrame);
            var ok = actual == expected;
            var line = $"[FrameCutSelfTest] ok={ok} expected={expected} actual={actual} " +
                       $"range=[{startFrame},{endFrame}) fps={fps:F3} output={outputPath}";
            Log.Information(line);
            Console.WriteLine(line);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FrameCutSelfTest] 异常");
            Console.WriteLine("[FrameCutSelfTest] EXCEPTION " + ex.Message);
        }
    }

    /// <summary>
    /// 叙事结构 AI 自测（issue #6 §六，构建机用真 DeepSeek 跑）：找带标签分镜最多的项目 →
    /// 取最高频 3 个标签建 3 段 → 调 CreateNarrativeStructureAsync 真生成 → 打变体结果。
    /// 结果 `[NarrativeSelfTest]` 进日志 + stdout 便于 SSH grep。需 host 已 StartAsync。
    /// </summary>
    private async Task RunNarrativeSelfTestAsync()
    {
        try
        {
            var factory = _host.Services.GetRequiredService<IDbContextFactory<MixCutDbContext>>();
            var vm = _host.Services.GetRequiredService<SchemeViewModel>();

            // 找带标签分镜最多的项目
            Models.Project? target = null;
            var bestTagged = 0;
            using (var db = factory.CreateDbContext())
            {
                var projects = db.Projects
                    .Include(p => p.ProjectVideos).ThenInclude(pv => pv.Video!).ThenInclude(v => v.Segments)
                    .AsSplitQuery().ToList();
                foreach (var p in projects)
                {
                    var tagged = p.ProjectVideos.Where(pv => pv.Video != null)
                        .SelectMany(pv => pv.Video!.Segments).Count(s => s.SemanticTypes.Count > 0);
                    if (tagged > bestTagged)
                    {
                        bestTagged = tagged;
                        target = p;
                    }
                }
            }
            if (target is null || bestTagged == 0)
            {
                EmitSelfTest("[NarrativeSelfTest] NO_DATA 库里没有带标签分镜的项目");
                return;
            }

            vm.LoadSchemes(target);
            var segments = vm.LoadProjectSegments(target);

            // 取出现频率最高的 3 个标签，各建一段
            var tagCounts = new Dictionary<Models.SemanticType, int>();
            foreach (var seg in segments)
            {
                foreach (var t in seg.SemanticTypes)
                {
                    tagCounts[t] = tagCounts.GetValueOrDefault(t) + 1;
                }
            }
            var topTags = tagCounts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
            if (topTags.Count == 0)
            {
                EmitSelfTest("[NarrativeSelfTest] NO_TAGS");
                return;
            }
            var slots = topTags.Select((t, i) => new Models.NarrativeSlot(i + 1, new List<Models.SemanticType> { t })).ToList();
            EmitSelfTest($"[NarrativeSelfTest] project={target.Name} segs={segments.Count} " +
                         $"slots={string.Join(" · ", topTags.Select(t => t.ToLabel()))}");

            var result = await vm.CreateNarrativeStructureAsync(target, slots, segments, 3);
            if (result is not null)
            {
                var variants = result.OrderedSchemes;
                EmitSelfTest($"[NarrativeSelfTest] OK 结构='{result.NarrativeDisplayName}' 变体数={variants.Count} " +
                             $"名字=[{string.Join(",", variants.Select(v => v.Name))}]");
                foreach (var v in variants)
                {
                    EmitSelfTest($"[NarrativeSelfTest]   {v.Name}: {v.SegmentCount} 段 | {v.SchemeDescription}");
                }
            }
            else
            {
                EmitSelfTest("[NarrativeSelfTest] NULL 无连贯变体通过（AI 或二次校验全刷掉）");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NarrativeSelfTest] 异常");
            Console.WriteLine("[NarrativeSelfTest] EXCEPTION " + ex.Message);
        }
    }

    private static void EmitSelfTest(string line)
    {
        Log.Information(line);
        Console.WriteLine(line);
    }

    /// <summary>
    /// v0.5.0 配音自测：`--selftest-dub` 对库里第一个「有带台词分镜」的视频跑一遍
    /// RewriteAll 全链路（分离→克隆→改写→合成对齐），把 [DubDiag] 计数打到日志+stdout 后退出。
    /// 构建机用真实 qwen key 验证服务引擎（§自我验证铁律）。正常启动不受影响。
    /// </summary>
    private async Task RunDubSelfTestAsync()
    {
        try
        {
            var factory = _host.Services.GetRequiredService<IDbContextFactory<MixCutDbContext>>();
            var dub = _host.Services.GetRequiredService<DubbingViewModel>();

            // 测试兜底：未配置千问 key 时，从环境变量 DASHSCOPE_API_KEY 种入（仅自测路径，生产不受影响）。
            var settings = _host.Services.GetRequiredService<AppSettings>();
            var envKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
            if (!settings.HasApiKey(Services.AI.AIProviderType.Qwen) && !string.IsNullOrEmpty(envKey))
            {
                settings.SaveApiKey(envKey, Services.AI.AIProviderType.Qwen);
                settings.ActiveProvider = Services.AI.AIProviderType.Qwen;
                EmitSelfTest("[DubSelfTest] 已从环境变量种入千问 key（仅测试）");
            }

            Guid videoId = Guid.Empty;
            string videoName = "";
            int textSegs = 0;
            using (var db = factory.CreateDbContext())
            {
                var v = db.Videos
                    .Where(v => v.Segments.Any(s => s.Text != "" && !s.IsVoiceLocked))
                    .OrderByDescending(v => v.Segments.Count(s => s.Text != ""))
                    .FirstOrDefault();
                if (v is not null)
                {
                    videoId = v.Id;
                    videoName = v.Name;
                    textSegs = db.Segments.Count(s => s.VideoId == v.Id && s.Text != "" && !s.IsVoiceLocked);
                }
            }
            if (videoId == Guid.Empty)
            {
                EmitSelfTest("[DubSelfTest] NO_DATA 库里没有带台词分镜的视频");
                return;
            }

            EmitSelfTest($"[DubSelfTest] START video='{videoName}' 可重配分镜={textSegs} 变体数={dub.VariantCount}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await dub.RewriteAllAsync(videoId);
            sw.Stop();

            var produced = await dub.EffectiveVariantCountAsync(videoId);
            EmitSelfTest($"[DubSelfTest] DONE 有效变体={produced} 耗时={sw.Elapsed.TotalSeconds:F0}s err={dub.ErrorMessage ?? "无"}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DubSelfTest] 异常");
            Console.WriteLine("[DubSelfTest] EXCEPTION " + ex.Message);
        }
    }

    /// <summary>
    /// ① ASR 重识别自测：`--selftest-reasr` 对库里第一个带台词分镜跑一遍 <see cref="DubbingViewModel.ReRecognizeSegmentAsync"/>
    /// （抽区间音频 → 真调 Paraformer 流式 ASR → 拿回文本），打印「旧文本 vs 新文本」实证 Point 1 网络重识别整链路，
    /// 跑完<b>把文本还原</b>不改用户数据。需千问 key（缺时从 DASHSCOPE_API_KEY 种入，同 --selftest-dub）。
    /// </summary>
    private async Task RunReAsrSelfTestAsync()
    {
        try
        {
            var factory = _host.Services.GetRequiredService<IDbContextFactory<MixCutDbContext>>();
            var dub = _host.Services.GetRequiredService<DubbingViewModel>();

            var settings = _host.Services.GetRequiredService<AppSettings>();
            var envKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
            if (!settings.HasApiKey(Services.AI.AIProviderType.Qwen) && !string.IsNullOrEmpty(envKey))
            {
                settings.SaveApiKey(envKey, Services.AI.AIProviderType.Qwen);
                settings.ActiveProvider = Services.AI.AIProviderType.Qwen;
                EmitSelfTest("[ReAsrSelfTest] 已从环境变量种入千问 key（仅测试）");
            }

            Guid segId = Guid.Empty; string oldText = "";
            using (var db = factory.CreateDbContext())
            {
                var s = db.Segments
                    .Where(x => x.Video != null && x.Text != "")
                    .OrderBy(x => x.StartFrame)
                    .FirstOrDefault();
                if (s is not null) { segId = s.Id; oldText = s.Text; }
            }
            if (segId == Guid.Empty)
            {
                EmitSelfTest("[ReAsrSelfTest] NO_DATA 库里没有带台词分镜");
                return;
            }

            EmitSelfTest($"[ReAsrSelfTest] START seg={segId} 旧文本='{(oldText.Length > 40 ? oldText[..40] + "…" : oldText)}'");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string newText;
            try
            {
                newText = await dub.ReRecognizeSegmentAsync(segId);
            }
            finally
            {
                // 还原原文本，不改用户数据（ReRecognizeSegmentAsync 已把 Text 落库）。
                using var db = factory.CreateDbContext();
                var t = db.Segments.FirstOrDefault(x => x.Id == segId);
                if (t is not null) { t.Text = oldText; db.SaveChanges(); }
                EmitSelfTest("[ReAsrSelfTest] CLEANUP 已还原原文本");
            }
            sw.Stop();
            EmitSelfTest($"[ReAsrSelfTest] DONE 耗时={sw.Elapsed.TotalSeconds:F1}s 新文本='{(newText.Length > 60 ? newText[..60] + "…" : newText)}' 长度={newText.Length}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ReAsrSelfTest] 异常");
            Console.WriteLine("[ReAsrSelfTest] EXCEPTION " + ex.Message);
        }
    }

    /// <summary>
    /// ① 手动编辑台词自测：`--selftest-edittext` 对库里第一个带台词分镜调
    /// <see cref="DubbingViewModel.UpdateSegmentTextAsync"/> 写一个带标记的新文本，
    /// 再<b>重新开一个 DbContext 回查</b>确认真落库（验证 §3 编辑台词数据链路），最后还原原文本，不改用户数据。
    /// </summary>
    private async Task RunEditTextSelfTestAsync()
    {
        try
        {
            var factory = _host.Services.GetRequiredService<IDbContextFactory<MixCutDbContext>>();
            var dub = _host.Services.GetRequiredService<DubbingViewModel>();

            Guid segId = Guid.Empty; string oldText = "";
            using (var db = factory.CreateDbContext())
            {
                var s = db.Segments
                    .Where(x => x.Video != null && x.Text != "")
                    .OrderBy(x => x.StartFrame)
                    .FirstOrDefault();
                if (s is not null) { segId = s.Id; oldText = s.Text; }
            }
            if (segId == Guid.Empty)
            {
                EmitSelfTest("[EditTextSelfTest] NO_DATA 库里没有带台词分镜");
                return;
            }

            var marker = " 【自测编辑】";
            var wantText = oldText + marker;
            EmitSelfTest($"[EditTextSelfTest] START seg={segId} 旧文本长度={oldText.Length}");
            try
            {
                var returned = await dub.UpdateSegmentTextAsync(segId, wantText);
                // 关键：另开一个 DbContext 回查，确认是真落库而非只改内存。
                string persisted;
                using (var db = factory.CreateDbContext())
                {
                    persisted = db.Segments.FirstOrDefault(x => x.Id == segId)?.Text ?? "<缺失>";
                }
                var ok = persisted == wantText.Trim() && returned == wantText.Trim();
                EmitSelfTest($"[EditTextSelfTest] VERIFY 落库={(persisted.EndsWith(marker.Trim()) ? "含标记✓" : "无标记✗")} 回查长度={persisted.Length} 期望长度={wantText.Trim().Length} 结果={(ok ? "PASS" : "FAIL")}");
            }
            finally
            {
                using var db = factory.CreateDbContext();
                var t = db.Segments.FirstOrDefault(x => x.Id == segId);
                if (t is not null) { t.Text = oldText; db.SaveChanges(); }
                EmitSelfTest("[EditTextSelfTest] CLEANUP 已还原原文本");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[EditTextSelfTest] 异常");
            Console.WriteLine("[EditTextSelfTest] EXCEPTION " + ex.Message);
        }
    }

    /// <summary>
    /// ① 逐分镜阿里精识别批量自测：`--selftest-reidbatch` 对库里第一个「有带台词分镜」的视频调
    /// <see cref="ImportViewModel.ReidentifyVideoAsync"/> 重跑一遍阿里 Paraformer 精识别，
    /// 回查有多少段文本被覆盖（打印一个前后对比样本），最后<b>还原全部原文本</b>，不改用户数据。
    /// 验证 §「导入时自动逐分镜精识别」对齐 mac 的核心链路。需千问 key（缺时从 DASHSCOPE_API_KEY 种入）。
    /// </summary>
    private async Task RunReidBatchSelfTestAsync()
    {
        try
        {
            var factory = _host.Services.GetRequiredService<IDbContextFactory<MixCutDbContext>>();
            var import = _host.Services.GetRequiredService<ImportViewModel>();

            var settings = _host.Services.GetRequiredService<AppSettings>();
            var envKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
            if (!settings.HasApiKey(Services.AI.AIProviderType.Qwen) && !string.IsNullOrEmpty(envKey))
            {
                settings.SaveApiKey(envKey, Services.AI.AIProviderType.Qwen);
                settings.ActiveProvider = Services.AI.AIProviderType.Qwen;
                EmitSelfTest("[ReidBatchSelfTest] 已从环境变量种入千问 key（仅测试）");
            }

            Guid videoId = Guid.Empty; string videoName = "";
            var snapshot = new System.Collections.Generic.Dictionary<Guid, string>();
            using (var db = factory.CreateDbContext())
            {
                var v = db.Videos.Where(x => x.Segments.Any(s => s.Text != ""))
                    .OrderBy(x => x.CreatedAt).FirstOrDefault();
                if (v is not null)
                {
                    videoId = v.Id; videoName = v.Name;
                    foreach (var s in db.Segments.Where(s => s.VideoId == v.Id))
                        snapshot[s.Id] = s.Text;
                }
            }
            if (videoId == Guid.Empty)
            {
                EmitSelfTest("[ReidBatchSelfTest] NO_DATA 库里没有带台词分镜的视频");
                return;
            }

            EmitSelfTest($"[ReidBatchSelfTest] START video='{videoName}' 分镜={snapshot.Count}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ok, fail) = await import.ReidentifyVideoAsync(videoId);
            sw.Stop();

            int changed = 0; string sample = "";
            using (var db = factory.CreateDbContext())
            {
                foreach (var s in db.Segments.Where(s => s.VideoId == videoId))
                {
                    if (snapshot.TryGetValue(s.Id, out var old) && old != s.Text)
                    {
                        changed++;
                        if (sample.Length == 0)
                            sample = $"样本 旧='{(old.Length > 30 ? old[..30] + "…" : old)}' → 新='{(s.Text.Length > 30 ? s.Text[..30] + "…" : s.Text)}'";
                    }
                }
            }
            EmitSelfTest($"[ReidBatchSelfTest] DONE 耗时={sw.Elapsed.TotalSeconds:F1}s ok={ok} fail={fail} 文本变化={changed}/{snapshot.Count} 结果={(ok > 0 && changed > 0 ? "PASS" : "CHECK")}  {sample}");

            using (var db = factory.CreateDbContext())
            {
                foreach (var s in db.Segments.Where(s => s.VideoId == videoId))
                    if (snapshot.TryGetValue(s.Id, out var old)) s.Text = old;
                db.SaveChanges();
            }
            EmitSelfTest("[ReidBatchSelfTest] CLEANUP 已还原全部原文本");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ReidBatchSelfTest] 异常");
            Console.WriteLine("[ReidBatchSelfTest] EXCEPTION " + ex.Message);
        }
    }

    /// <summary>
    /// v0.7.x 手动添加变体自测：`--selftest-manualadd` 给库里第一个有台词分镜的视频<b>临时</b>种一个假
    /// ClonedVoiceId（跳过 20min demucs 克隆），对其分镜调 AddManualVariantAsync 验证「点了能加出空白版」，
    /// 完事把假变体 + 假 voiceId 还原（不污染用户数据）。验证「手动添加点了没反应」是否真在加。
    /// </summary>
    private async Task RunManualAddSelfTestAsync()
    {
        Guid videoId = Guid.Empty, segId = Guid.Empty;
        string? originalVoiceId = null;
        var factory = _host.Services.GetRequiredService<IDbContextFactory<MixCutDbContext>>();
        try
        {
            var dub = _host.Services.GetRequiredService<DubbingViewModel>();
            using (var db = factory.CreateDbContext())
            {
                var seg = db.Segments.Include(s => s.Video)
                    .Where(s => s.Text != "" && !s.IsVoiceLocked && s.Video != null)
                    .OrderBy(s => s.StartFrame).FirstOrDefault();
                if (seg?.Video is null) { EmitSelfTest("[ManualAddSelfTest] NO_DATA 无带台词分镜"); return; }
                segId = seg.Id; videoId = seg.Video.Id;
                originalVoiceId = seg.Video.ClonedVoiceId;
                seg.Video.ClonedVoiceId = "selftest-dummy-voice"; // 跳过真克隆
                db.SaveChanges();
            }

            int before, after;
            using (var db = factory.CreateDbContext())
                before = db.SegmentDubs.Count(d => d.SegmentId == segId);

            EmitSelfTest($"[ManualAddSelfTest] START seg={segId} 变体数(前)={before}");
            var idx = await dub.AddManualVariantAsync(segId);

            using (var db = factory.CreateDbContext())
                after = db.SegmentDubs.Count(d => d.SegmentId == segId);
            EmitSelfTest($"[ManualAddSelfTest] DONE 返回下标={(idx?.ToString() ?? "null")} 变体数(后)={after} delta={after - before} err={dub.ErrorMessage ?? "无"}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ManualAddSelfTest] 异常");
            Console.WriteLine("[ManualAddSelfTest] EXCEPTION " + ex.Message);
        }
        finally
        {
            // 还原：删掉假 voiceId 加出来的空白变体 + 复原 ClonedVoiceId，不污染用户库。
            try
            {
                using var db = factory.CreateDbContext();
                var fakes = db.SegmentDubs.Where(d => d.VoiceId == "selftest-dummy-voice").ToList();
                db.SegmentDubs.RemoveRange(fakes);
                if (videoId != Guid.Empty)
                {
                    var v = db.Videos.FirstOrDefault(v => v.Id == videoId);
                    if (v != null) v.ClonedVoiceId = originalVoiceId;
                }
                db.SaveChanges();
                EmitSelfTest($"[ManualAddSelfTest] CLEANUP 删假变体={fakes.Count} 复原voiceId={(originalVoiceId ?? "null")}");
            }
            catch (Exception ex) { Console.WriteLine("[ManualAddSelfTest] CLEANUP FAIL " + ex.Message); }
        }
    }

    /// <summary>
    /// v0.7.x 人声分离进度自测：对给定短 wav 跑一遍 demucs 分离，把每次百分比回调打到日志。
    /// 验证 demucs 进度行能被事件读流捕获、解析、经 onPercent 回调上来（构建机自验证，§自我验证铁律）。
    /// </summary>
    private async Task RunSepSelfTestAsync(string wavPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(wavPath) || !System.IO.File.Exists(wavPath))
            {
                EmitSelfTest($"[SepSelfTest] NO_INPUT 找不到输入 wav: '{wavPath}'");
                return;
            }
            var sep = _host.Services.GetRequiredService<Services.Dubbing.VocalSeparationService>();
            var reports = 0;
            var lastPct = -1.0;
            var pct = new Progress<double>(f =>
            {
                reports++;
                lastPct = f;
            });
            var text = new Progress<string>(msg => EmitSelfTest($"[SepSelfTest] 阶段: {msg}"));
            // 用 wav 路径派生一个唯一 hash，强制不命中缓存（每次真跑 demucs）。
            var hash = "selftest" + Guid.NewGuid().ToString("N")[..8];
            EmitSelfTest($"[SepSelfTest] START wav='{wavPath}'");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var stems = await sep.SeparateAsync(wavPath, hash, text, pct);
            sw.Stop();
            EmitSelfTest($"[SepSelfTest] DONE 百分比回调次数={reports} 末值={lastPct:P0} " +
                         $"耗时={sw.Elapsed.TotalSeconds:F0}s vocals存在={System.IO.File.Exists(stems.VocalsPath)} " +
                         $"bgm存在={System.IO.File.Exists(stems.BgmPath)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SepSelfTest] 异常");
            Console.WriteLine("[SepSelfTest] EXCEPTION " + ex.Message);
        }
    }

    /// <summary>
    /// v0.5.0 配音组合导出自测：`--selftest-dubexport` 对库里配音变体最多的视频，按其分镜(优先选改写A、
    /// 否则原声)构造一条配音成片导出，验证滤镜图/字幕渲染/-ac2 声道统一/concat/首帧归零/BGM 混音整链路。
    /// </summary>
    private async Task RunDubExportSelfTestAsync()
    {
        try
        {
            var factory = _host.Services.GetRequiredService<IDbContextFactory<MixCutDbContext>>();
            var exporter = _host.Services.GetRequiredService<Services.Dubbing.DubExportService>();

            List<Models.Segment> segs;
            int vw, vh;
            string videoName;
            using (var db = factory.CreateDbContext())
            {
                var v = db.Videos
                    .Where(v => v.Segments.Any(s => s.SegmentDubs.Any(d => d.AudioFilePath != null)))
                    .OrderByDescending(v => v.Segments.SelectMany(s => s.SegmentDubs).Count(d => d.AudioFilePath != null))
                    .FirstOrDefault();
                if (v is null) { EmitSelfTest("[DubExportSelfTest] NO_DATA 无含已生成配音的视频"); return; }
                videoName = v.Name; vw = v.Width; vh = v.Height;
                segs = db.Segments
                    .Include(s => s.Video)
                    .Include(s => s.SegmentDubs)
                    .Where(s => s.VideoId == v.Id)
                    .OrderBy(s => s.StartFrame)
                    .Take(4) // 取前 4 个分镜，控制自测时长
                    .ToList();
            }

            // 构造 specs：有已生成变体的取首个(改写A)，否则原声。
            var specs = new List<Services.Dubbing.DubSegmentSpec>();
            foreach (var s in segs)
            {
                var fps = s.EffectiveFps > 0 ? s.EffectiveFps : 30;
                var dub = s.EffectiveDubVariants.FirstOrDefault();
                if (dub is not null && !string.IsNullOrEmpty(dub.AudioFilePath))
                {
                    var bgm = string.IsNullOrEmpty(s.Video?.ContentHash) ? null
                        : System.IO.Path.Combine(AppPaths.StemsDirectory(s.Video!.ContentHash), "bgm.wav");
                    var stWhole = string.IsNullOrEmpty(dub.RewrittenText) ? s.Text : dub.RewrittenText;
                    var stCaps = string.IsNullOrEmpty(stWhole)
                        ? new System.Collections.Generic.List<Models.CaptionLine>()
                        : new System.Collections.Generic.List<Models.CaptionLine> { new(stWhole, 0, s.Duration) };
                    specs.Add(new Services.Dubbing.DubSegmentSpec(
                        s.Video!.LocalPath, s.StartFrame, s.EndFrame, fps,
                        stCaps,
                        HasHardSubtitle: false, s.MaskStyleRaw, s.MaskRect,
                        IsVoiceLocked: false, dub.AudioFilePath, dub.FreezePadFrames, dub.TrailingSilence,
                        System.IO.File.Exists(bgm) ? bgm : null));
                }
                else
                {
                    specs.Add(new Services.Dubbing.DubSegmentSpec(
                        s.Video!.LocalPath, s.StartFrame, s.EndFrame, fps,
                        System.Array.Empty<Models.CaptionLine>(),
                        false, s.MaskStyleRaw, s.MaskRect, IsVoiceLocked: true, null, 0, 0, null));
                }
            }

            var dubbed = specs.Count(x => x.DubAudioPath != null);
            EmitSelfTest($"[DubExportSelfTest] START video='{videoName}' 分镜={specs.Count}(配音={dubbed}/原声={specs.Count - dubbed})");

            var input = new Services.Dubbing.DubExportInput(specs, vw, vh);
            var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mixcut-dubexport-selftest.mp4");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await exporter.ExportAsync(input, outPath, onProgress: p =>
                EmitSelfTest($"[DubExportSelfTest]   {p.Phase} {p.Progress:P0} {p.Description}"));
            sw.Stop();

            // 验证：文件存在 + 起始时间≈0 + 有音频
            var exists = System.IO.File.Exists(outPath);
            var size = exists ? new System.IO.FileInfo(outPath).Length : 0;
            var ffprobe = Infrastructure.BundledBinaries.Ffprobe;
            string Probe(string a) { try { var psi = new System.Diagnostics.ProcessStartInfo(ffprobe){RedirectStandardOutput=true,UseShellExecute=false,CreateNoWindow=true}; foreach(var x in a.Split(' ')) psi.ArgumentList.Add(x); var p=System.Diagnostics.Process.Start(psi)!; var o=p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return o; } catch { return "?"; } }
            var start = Probe($"-v error -select_streams v:0 -show_entries stream=start_time -of csv=p=0 {outPath}");
            var hasAudio = Probe($"-v error -select_streams a:0 -show_entries stream=codec_type -of csv=p=0 {outPath}");
            EmitSelfTest($"[DubExportSelfTest] DONE exists={exists} size={size / 1024}KB start_time={start} audio={hasAudio} 耗时={sw.Elapsed.TotalSeconds:F0}s → {outPath}");
            EmitSelfTest("[DubExportSelfTest] 请用播放器/资源管理器缩略图确认 t=0 首帧非黑、各段都有声。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DubExportSelfTest] 异常");
            Console.WriteLine("[DubExportSelfTest] EXCEPTION " + ex.Message);
        }
    }

    /// <summary>
    /// v0.5.0 配音换声预览自测：`--selftest-dubpreview` 对库里第一个有「已生成配音 + 分离 BGM」的分镜，
    /// 用真 <see cref="Views.Components.FfmpegFramePlayer"/> 设置换声覆盖（克隆配音 + BGM）后离屏播放，
    /// 验证 Point 4 方案预览换声整链路：视频帧正常渲染 + StartAudio 走 dub 分支（日志 [DubPreviewDiag]）+
    /// 混音 ffmpeg 进程起得来。与 UIA 点击等价，但无需开窗点击（§自我验证铁律：要实证数字）。
    /// </summary>
    private async Task RunDubPreviewSelfTestAsync()
    {
        try
        {
            var factory = _host.Services.GetRequiredService<IDbContextFactory<MixCutDbContext>>();
            string videoPath; int startFrame, endFrame; double fps; string dubPath; string? bgm;
            using (var db = factory.CreateDbContext())
            {
                var s = db.Segments
                    .Include(x => x.Video)
                    .Include(x => x.SegmentDubs)
                    .Where(x => x.Video != null && x.SegmentDubs.Any(d => d.AudioFilePath != null))
                    .OrderBy(x => x.StartFrame)
                    .FirstOrDefault();
                if (s is null) { EmitSelfTest("[DubPreviewSelfTest] NO_DATA 无含已生成配音的分镜"); return; }
                var dub = s.EffectiveDubVariants.FirstOrDefault(d => !string.IsNullOrEmpty(d.AudioFilePath));
                if (dub is null) { EmitSelfTest("[DubPreviewSelfTest] NO_DATA 分镜无音频变体"); return; }
                videoPath = s.Video!.LocalPath;
                startFrame = s.StartFrame; endFrame = s.EndFrame;
                fps = s.EffectiveFps > 0 ? s.EffectiveFps : 30;
                dubPath = dub.AudioFilePath!;
                var cand = string.IsNullOrEmpty(s.Video.ContentHash) ? null
                    : System.IO.Path.Combine(AppPaths.StemsDirectory(s.Video.ContentHash), "bgm.wav");
                bgm = !string.IsNullOrEmpty(cand) && System.IO.File.Exists(cand) ? cand : null;
            }

            var player = new Views.Components.FfmpegFramePlayer { Width = 320, Height = 568 };
            var failed = false; var ended = false; var reason = "";
            player.Failed += (_, r) => { failed = true; reason = r; };
            player.Ended += (_, _) => ended = true;
            var host = new Window
            {
                Width = 360, Height = 620, Left = -3000, Top = -3000,
                ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = player,
            };
            host.Show();
            await Task.Delay(200);

            player.SetAudioOverride(dubPath, bgm); // 换声：mute 原声、播「克隆配音 + BGM」
            var start = Utilities.FrameTime.FrameToSeconds(startFrame, fps);
            var dur = Utilities.FrameTime.FrameToSeconds(endFrame - startFrame, fps);
            EmitSelfTest($"[DubPreviewSelfTest] START dub={System.IO.Path.GetFileName(dubPath)} bgm={bgm != null} range=[{startFrame},{endFrame}) dur={dur:F2}s");
            player.Open(videoPath, start, dur, startFrame: startFrame, endFrame: endFrame, sourceFps: fps);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(Math.Max(8, dur + 5)) && !failed && !ended)
            {
                await Task.Delay(100);
            }
            EmitSelfTest($"[DubPreviewSelfTest] DONE frames={player.FramesRendered} ended={ended} failed={failed} reason={reason}（上方应有 [DubPreviewDiag] 换声预览 dub=... bgm=True，证明走了换声分支）");
            player.Stop();
            host.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DubPreviewSelfTest] 异常");
            Console.WriteLine("[DubPreviewSelfTest] EXCEPTION " + ex.Message);
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 隐藏自测模式：`--selftest-preview <视频>` 离屏跑一遍 FfmpegFramePlayer 渲染，
        // 数实际渲染帧数后退出。用于构建机自验证「进程外 ffmpeg 解码→WPF 渲染」整链路
        // （§自我验证铁律：要实证数字，不靠编译过臆断）。正常启动不受影响。
        var selfTestIdx = Array.IndexOf(e.Args, "--selftest-preview");
        if (selfTestIdx >= 0)
        {
            var path = selfTestIdx + 1 < e.Args.Length ? e.Args[selfTestIdx + 1] : "";
            await RunPreviewSelfTestAsync(path);
            Shutdown();
            return;
        }

        var frameSelfTestIdx = Array.IndexOf(e.Args, "--selftest-frame-preview");
        if (frameSelfTestIdx >= 0)
        {
            var args = e.Args.Skip(frameSelfTestIdx + 1).Take(4).ToArray();
            if (args.Length == 4
                && int.TryParse(args[1], out var startFrame)
                && int.TryParse(args[2], out var endFrame)
                && double.TryParse(args[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var fps))
            {
                await RunFramePreviewSelfTestAsync(args[0], startFrame, endFrame, fps);
            }
            else
            {
                Console.WriteLine("[FramePreviewSelfTest] 参数错误: <video> <startFrame> <endFrame> <fps>");
            }
            Shutdown();
            return;
        }

        var cutSelfTestIdx = Array.IndexOf(e.Args, "--selftest-frame-cut");
        if (cutSelfTestIdx >= 0)
        {
            var args = e.Args.Skip(cutSelfTestIdx + 1).Take(5).ToArray();
            if (args.Length == 5
                && int.TryParse(args[1], out var startFrame)
                && int.TryParse(args[2], out var endFrame)
                && double.TryParse(args[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var fps))
            {
                await RunFrameCutSelfTestAsync(args[0], startFrame, endFrame, fps, args[4]);
            }
            else
            {
                Console.WriteLine("[FrameCutSelfTest] 参数错误: <video> <startFrame> <endFrame> <fps> <output>");
            }
            Shutdown();
            return;
        }

        // 数据目录迁移自测（构建机自验证，不碰真实 %APPDATA% 指针）：沙盒里跑一遍拷贝+改库，打印 PASS/FAIL。
        if (Array.IndexOf(e.Args, "--selftest-datamigrate") >= 0)
        {
            var sandbox = Path.Combine(Path.GetTempPath(), "mixcut-datamigrate-selftest");
            string outcome;
            try { outcome = Infrastructure.DataDirectoryMigrator.SelfTest(sandbox); }
            catch (Exception ex) { outcome = "[DataMigrateSelfTest] THREW\n" + ex; }
            Console.WriteLine(outcome);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixcut-datamigrate-selftest-result.txt"), outcome); }
            catch { /* ignore */ }
            Shutdown();
            return;
        }

        // 数据目录迁移（用户在「设置 → 存储」改了存储位置，上次退出前登记了待迁移）：
        // 必须在**任何 AppPaths / 日志 / DB 打开之前**执行 —— 否则旧目录的 mixcut.db / 日志被占用，搬不动。
        // 无待迁移标记时零副作用、立即返回。放在自测早退之后、闪屏 + host 启动之前，是全流程最早的安全点。
        try
        {
            if (Infrastructure.DataDirectoryMigrator.HasPending())
                RunDataMigrationWithProgress();
            else
                // 自愈 v0.10.0 遗留：指针被误删但数据已搬到别处的用户，从迁移日志把指针找回来（否则会被当新用户）。
                Infrastructure.DataDirectoryMigrator.RecoverLostPointerIfAny();
        }
        catch (Exception ex)
        {
            // 迁移器自身已把失败兜住并保留旧数据；这里再兜一层，绝不因迁移崩掉启动。
            MessageBox.Show($"数据迁移出错，已保留原数据。\n{ex.Message}", "MixCut",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // QW-10：冷启动期 host 启动 + DB 迁移 + 硬件探测有数秒空白，先弹启动闪屏给即时反馈，
        // 避免「双击了没反应」的错觉。try/catch 兜底 —— 闪屏永远不能阻断或拖垮真正的启动。
        SplashWindow? splash = null;
        try
        {
            splash = new SplashWindow();
            splash.Show();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Splash] 启动闪屏创建失败，忽略不影响启动");
        }

        await _host.StartAsync();
        Log.Information("MixCut 启动，数据目录: {Root}", AppPaths.Root);

        // 启动期环境一站式体检（v0.4.0）：OS/CPU/AVX2/VC Runtime/Binaries/AppData/InstallDir。
        // 一行汇总 [EnvDiag] 写日志，关键失败立即弹窗。让用户在撞运行时崩溃前先看到清晰指引。
        var envReport = Infrastructure.EnvironmentDiagnostics.RunAndLog();
        ShowEnvDialogsIfNeeded(envReport);

        // 预览解码栈诊断（§兼容性总纲）：hover 预览用自带 ffmpeg.exe 进程外解码，
        // 与导出同源、不碰系统编解码器（消灭 0xC00D109B）。确认 ffmpeg.exe 在位。
        Log.Information("[FfmeDiag] ffmpeg.exe={Path} exists={Ok}",
            Infrastructure.BundledBinaries.Ffmpeg, Infrastructure.BundledBinaries.FfmpegAvailable);

        // 启动时主动跑一次硬件能力探测（包含 encode smoke test + decode hwaccel 选择），
        // 结果写日志。后续所有 ffmpeg / ASR 任务用探测结果按优先级走 GPU/CPU 兜底。
        _ = Task.Run(() =>
        {
            Infrastructure.HardwareEncoderProbe.EagerInit();
            // issue #4 Phase 3：探测出硬件编码器后，再用「导出实际参数」跑一次真实 smoke。
            // 某个 codec 私有选项被新版 ffmpeg 拒识时自动降级 CPU，杜绝导出 0/N 全崩
            // （v0.4.0 -allow_sw 事故：HardwareEncoderProbe 的通用 smoke 绿、但真实导出全失败）。
            Infrastructure.ExportCommandSmokeTest.Run();
        });

        // 统一捕获未处理异常 —— 之前出过 async void 事件处理器异常直接终止进程的事故，
        // 看 EventLog 也拿不到堆栈。三个 hook 兜底：
        //   - DispatcherUnhandledException：UI 线程同步异常 / async void 抛出
        //   - TaskScheduler.UnobservedTaskException：Task 抛了但没人 await
        //   - AppDomain.UnhandledException：以上漏网（包括子线程崩溃）
        // 前两者会真正威胁进程存活 → Log.Fatal 写盘 + 弹 MessageBox + flush，让用户看得到错误而不是窗口神秘消失。
        // UnobservedTaskException 例外：它按定义不会崩进程，绝大多数是后台连接拆解的 socket 噪音，只静默记日志（见下）。
        DispatcherUnhandledException += (_, args) =>
        {
            LogFatalAndShow(args.Exception, "UI 线程");
            args.Handled = true; // 不让进程退出
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogFatalAndShow(ex, "未处理异常");
            }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // 未观察的 Task 异常 = 没人 await 的后台任务抛的错。按定义进程不会因此崩溃（窗口不会神秘消失），
            // 绝大多数是后台 HTTP/WebSocket 连接被拆时的 socket abort（SocketException 995 /
            // IOException「已中止 I/O 操作」）—— 例如逐分镜精识别 / AI 请求的连接池回收。
            // 这类噪音绝不能弹模态框吓用户（违反 §最高原则红线：不许把 stack trace / 原生报错码丢用户脸上）。
            // 只静默写日志 + SetObserved，让它安静消失。真正会崩进程的走上面 Dispatcher / AppDomain 两个 hook。
            args.SetObserved();
            Log.Warning(args.Exception, "[后台任务异常] 已忽略（不影响运行）：{Message}", args.Exception.Message);
        };

        // 初始化数据库（不存在则按当前模型创建）。
        using (var db = _host.Services
                   .GetRequiredService<IDbContextFactory<MixCutDbContext>>()
                   .CreateDbContext())
        {
            db.Database.EnsureCreated();
            Log.Information("数据库就绪: {DbFile}", AppPaths.DatabaseFile);

            // v0.6.0 对齐迁移：EF Core EnsureCreated 不会自动 ALTER 老库，
            // 老用户升级后 Strategies / Schemes 缺新加的列会撞 SqliteException。
            // 显式补列（默认 false，对老数据无副作用）。必须在写入新字段前完成。
            AddColumnIfMissing(db, "Strategies", "IsCustomGroup", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(db, "Schemes", "IsManuallyEdited", "INTEGER NOT NULL DEFAULT 0");
            // issue #6 自定义叙事结构：MixStrategy 新增两列
            AddColumnIfMissing(db, "Strategies", "IsNarrativeTemplate", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(db, "Strategies", "NarrativeSlotsJson", "TEXT");
            // issue #7 帧精确重构：Segment 新增帧号 / fps 列（帧为真值，秒派生）
            AddColumnIfMissing(db, "Segments", "StartFrame", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(db, "Segments", "EndFrame", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(db, "Segments", "Fps", "REAL NOT NULL DEFAULT 0");

            // v0.5.0 分镜级 AI 配音：Segment 字幕处理/保留原声列 + Video 克隆音色 + SchemeSegment 选定变体 + SegmentDubs 新表
            AddColumnIfMissing(db, "Segments", "IsVoiceLocked", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(db, "Segments", "HasHardSubtitle", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(db, "Segments", "MaskStyleRaw", "TEXT NOT NULL DEFAULT 'Blur'");
            AddColumnIfMissing(db, "Segments", "MaskRectJson", "TEXT");
            AddColumnIfMissing(db, "Videos", "ClonedVoiceId", "TEXT");
            // #17 自建分镜：载体视频标记（默认 0=普通视频，旧数据无副作用）。
            AddColumnIfMissing(db, "Videos", "IsUserUploaded", "INTEGER NOT NULL DEFAULT 0");
            // 每个分镜单独克隆音色（配音跟本段原声一致）：Segment 新增自己的克隆音色列
            AddColumnIfMissing(db, "Segments", "ClonedVoiceId", "TEXT");
            AddColumnIfMissing(db, "SchemeSegments", "SelectedSegmentDubId", "TEXT");
            // #13 配音变体参与排列组合：原版默认参与(1)、改写版默认不参与(0，opt-in)
            AddColumnIfMissing(db, "Segments", "OriginalParticipatesInCombination", "INTEGER NOT NULL DEFAULT 1");
            // 注意（issue #21）：SegmentDubs 的两处补列（ParticipatesInCombination / CaptionLinesJson）
            // 必须放到 CreateTableIfMissing(SegmentDubs) 之后（见下方）。老库首次升级时 SegmentDubs 表
            // 还不存在，若在建表前 AddColumnIfMissing 会撞 "no such table" 静默失败 → 该表建出来后仍缺列 →
            // 之后任何 eager-load SegmentDubs 的查询（LoadSchemes）报 "no such column"，表现为「生成方案失败」。
            // 通用铁律：任何 AddColumnIfMissing(表X,*) 都必须排在 CreateTableIfMissing(表X) 之后。
            // issue #23 Agent 接入：逐分镜字幕字号比例（-1 = 未单独设置，跟随全局值，存量行为不变）
            AddColumnIfMissing(db, "Segments", "SubtitleFontRatio", "REAL NOT NULL DEFAULT -1");
            // #12 分镜头 AI 画面替换：Segment 四个「替换画面」列 + PhysicalShots / ShotVariants 两张新表
            AddColumnIfMissing(db, "Segments", "ReplacedPictureVideoPath", "TEXT");
            AddColumnIfMissing(db, "Segments", "ReplacedPictureThumbnailPath", "TEXT");
            AddColumnIfMissing(db, "Segments", "ReplacedPictureFrameCount", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(db, "Segments", "PictureShowsReplaced", "INTEGER NOT NULL DEFAULT 0");
            CreateTableIfMissing(db, "PhysicalShots", @"
                CREATE TABLE IF NOT EXISTS ""PhysicalShots"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_PhysicalShots"" PRIMARY KEY,
                    ""SegmentId"" TEXT NULL,
                    ""OrderIndex"" INTEGER NOT NULL DEFAULT 1,
                    ""StartFrame"" INTEGER NOT NULL DEFAULT 0,
                    ""EndFrame"" INTEGER NOT NULL DEFAULT 0,
                    ""SelectedVariantId"" TEXT NULL,
                    ""ThumbnailPath"" TEXT NULL,
                    CONSTRAINT ""FK_PhysicalShots_Segments_SegmentId"" FOREIGN KEY (""SegmentId"")
                        REFERENCES ""Segments"" (""Id"") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ""IX_PhysicalShots_SegmentId"" ON ""PhysicalShots"" (""SegmentId"");");
            CreateTableIfMissing(db, "ShotVariants", @"
                CREATE TABLE IF NOT EXISTS ""ShotVariants"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_ShotVariants"" PRIMARY KEY,
                    ""ShotId"" TEXT NULL,
                    ""Prompt"" TEXT NOT NULL DEFAULT '',
                    ""StatusRaw"" TEXT NOT NULL DEFAULT 'Pending',
                    ""TaskId"" TEXT NULL,
                    ""ResultVideoPath"" TEXT NULL,
                    ""ThumbnailPath"" TEXT NULL,
                    ""FriendlyError"" TEXT NULL,
                    ""CreatedAt"" TEXT NOT NULL DEFAULT '',
                    CONSTRAINT ""FK_ShotVariants_PhysicalShots_ShotId"" FOREIGN KEY (""ShotId"")
                        REFERENCES ""PhysicalShots"" (""Id"") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ""IX_ShotVariants_ShotId"" ON ""ShotVariants"" (""ShotId"");");
            CreateTableIfMissing(db, "SegmentDubs", @"
                CREATE TABLE IF NOT EXISTS ""SegmentDubs"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_SegmentDubs"" PRIMARY KEY,
                    ""SegmentId"" TEXT NULL,
                    ""VoiceId"" TEXT NOT NULL DEFAULT '',
                    ""VoiceProvider"" TEXT NOT NULL DEFAULT 'qwen',
                    ""TextVariantIndex"" INTEGER NOT NULL DEFAULT 0,
                    ""RewrittenText"" TEXT NOT NULL DEFAULT '',
                    ""AudioFilePath"" TEXT NULL,
                    ""AudioDuration"" REAL NOT NULL DEFAULT 0,
                    ""AtempoFactor"" REAL NOT NULL DEFAULT 1.0,
                    ""FreezePadFrames"" INTEGER NOT NULL DEFAULT 0,
                    ""TrailingSilence"" REAL NOT NULL DEFAULT 0,
                    ""GeneratedForStartFrame"" INTEGER NOT NULL DEFAULT -1,
                    ""GeneratedForEndFrame"" INTEGER NOT NULL DEFAULT -1,
                    ""GeneratedForTextHash"" TEXT NOT NULL DEFAULT '',
                    ""StatusRaw"" TEXT NOT NULL DEFAULT 'Pending',
                    ""ParticipatesInCombination"" INTEGER NOT NULL DEFAULT 0,
                    ""CaptionLinesJson"" TEXT,
                    CONSTRAINT ""FK_SegmentDubs_Segments_SegmentId"" FOREIGN KEY (""SegmentId"")
                        REFERENCES ""Segments"" (""Id"") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ""IX_SegmentDubs_SegmentId"" ON ""SegmentDubs"" (""SegmentId"");");
            // issue #21：SegmentDubs 建表完成后再补列（先建表后补列铁律）。兼顾两种老库：
            // ① 从没建过 SegmentDubs 的库 → 上面 CreateTableIfMissing 已建全（DDL 含下列两列），补列跳过；
            // ② 中间态库（SegmentDubs 已存在但因历史顺序 bug 缺列）→ 这里把缺的列补上。
            AddColumnIfMissing(db, "SegmentDubs", "ParticipatesInCombination", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(db, "SegmentDubs", "CaptionLinesJson", "TEXT");

            // 清理上次未正常完成的中间态视频（崩溃/强退导致状态卡在分析中）。
            // 对齐 macOS 版 MixCutApp.resetStaleAnalyzingStatus。
            ResetStaleAnalyzingStatus(db);
            // P1-73：同样恢复卡在「生成方案中」(Generating) 的项目 —— AI 生成方案过程中
            // 应用崩溃 / 断电 / 强退时，项目状态会永久卡 Generating，用户无法再次生成（无解死锁）。
            ResetStaleGeneratingProjects(db);

            // v0.3.0 对齐迁移：老项目补建「自定义组合」策略
            var settingsForMigration = _host.Services.GetRequiredService<AppSettings>();
            var loggerForMigration = _host.Services.GetRequiredService<ILogger<App>>();
            EnsureCustomGroupStrategy(db, settingsForMigration, loggerForMigration);

            // issue #7 帧精确重构：把旧分镜的「秒」边界按视频 fps 回填为帧号（一次性，防重）
            BackfillSegmentFrames(db, settingsForMigration, loggerForMigration);

            // 「归档」功能移除后的收尾迁移：issue #16 已删掉归档入口，但历史库里可能仍存着
            // Status='Archived' 的项目 —— 它们会被主列表过滤掉，用户看不见也删不掉，等于数据被吞了。
            // 这里把它们统一恢复成「已完成」，之后 ProjectStatus.Archived 枚举才能安全删除。
            RestoreArchivedProjects(db, loggerForMigration);

            // 认领崩溃前已提交的 AI 画面生成任务（涉及真金白银，必须恢复）。
            ResetStaleGeneratingVariants(db, loggerForMigration);
        }

        // issue #6 叙事结构 AI 自测：`--selftest-narrative` 用真 DeepSeek 跑一次生成后退出（构建机验证）。
        if (Array.IndexOf(e.Args, "--selftest-narrative") >= 0)
        {
            await RunNarrativeSelfTestAsync();
            Shutdown();
            return;
        }

        // ① ASR 重识别自测：`--selftest-reasr` 真调 Paraformer 对一个分镜重识别后退出（还原文本，不改数据）。
        if (Array.IndexOf(e.Args, "--selftest-reasr") >= 0)
        {
            await RunReAsrSelfTestAsync();
            Shutdown();
            return;
        }

        // ① 手动编辑台词自测：`--selftest-edittext` 写带标记文本后回查落库再还原（验证 §3 编辑台词数据链路）。
        if (Array.IndexOf(e.Args, "--selftest-edittext") >= 0)
        {
            await RunEditTextSelfTestAsync();
            Shutdown();
            return;
        }

        // ① 逐分镜阿里精识别批量自测：`--selftest-reidbatch` 对第一个视频重跑精识别、回查文本变化、还原原文本。
        if (Array.IndexOf(e.Args, "--selftest-reidbatch") >= 0)
        {
            await RunReidBatchSelfTestAsync();
            Shutdown();
            return;
        }

        // v0.5.0 配音自测：`--selftest-dub` 对真实视频跑一遍 RewriteAll 全链路后退出（构建机验证服务引擎）。
        if (Array.IndexOf(e.Args, "--selftest-dub") >= 0)
        {
            await RunDubSelfTestAsync();
            Shutdown();
            return;
        }

        // v0.5.0 配音组合导出自测：`--selftest-dubexport` 导出一条配音成片后退出。
        if (Array.IndexOf(e.Args, "--selftest-dubexport") >= 0)
        {
            await RunDubExportSelfTestAsync();
            Shutdown();
            return;
        }

        // v0.5.0 配音换声预览自测：`--selftest-dubpreview` 离屏播一段「克隆配音 + BGM」换声预览后退出。
        if (Array.IndexOf(e.Args, "--selftest-dubpreview") >= 0)
        {
            await RunDubPreviewSelfTestAsync();
            Shutdown();
            return;
        }

        // v0.7.x 人声分离进度自测：`--selftest-sep <wav>` 对给定短 wav 跑一遍 demucs 分离，
        // 把实时百分比回调打到日志（[SepProgress]），验证「进度解析 + 事件读流 + 回调链」端到端在工作。
        var sepIdx = Array.IndexOf(e.Args, "--selftest-sep");
        if (sepIdx >= 0)
        {
            await RunSepSelfTestAsync(sepIdx + 1 < e.Args.Length ? e.Args[sepIdx + 1] : "");
            Shutdown();
            return;
        }

        // v0.7.x 手动添加变体自测：`--selftest-manualadd` 种假克隆后验证手动添加能加出空白版，再还原。
        if (Array.IndexOf(e.Args, "--selftest-manualadd") >= 0)
        {
            await RunManualAddSelfTestAsync();
            Shutdown();
            return;
        }

        // v0.13.x 对话框自测：`--selftest-dialog` 直接弹一个「危险操作确认」对话框并停住，
        // 供构建机截图核对视觉（自绘对话框替换 MessageBox 后，光看代码看不出渲染对不对）。
        // 不碰任何用户数据；截图完 kill 进程即可。默认演危险态，传 `alert` 演提示态。
        var dlgIdx = Array.IndexOf(e.Args, "--selftest-dialog");
        if (dlgIdx >= 0)
        {
            var mode = dlgIdx + 1 < e.Args.Length ? e.Args[dlgIdx + 1] : "confirm";
            // 关掉闪屏前必须先改 ShutdownMode：默认 OnLastWindowClose 下，闪屏一关就「最后一个窗口
            // 关闭了」→ WPF 立刻 Shutdown，对话框根本来不及显示（实测应用直接静默退出）。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { splash?.Close(); } catch { /* 自测路径，关不掉也不影响 */ }
            if (mode == "progress")
            {
                // 进度条自检：自定义 ControlTemplate 会替换掉默认模板里的不确定动画，
                // 漏补触发器时「转圈」态会退化成静止空槽（闪屏 / 导入 / 生成 / 导出全中）。
                // 这里同屏摆出确定 + 不确定两种，截图即可核对后者在动。
                var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(28) };
                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "确定进度（60%）", Margin = new Thickness(0, 0, 0, 6),
                });
                panel.Children.Add(new System.Windows.Controls.ProgressBar
                {
                    Minimum = 0, Maximum = 1, Value = 0.6, Width = 320,
                });
                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "不确定进度（应呼吸闪动）", Margin = new Thickness(0, 18, 0, 6),
                });
                panel.Children.Add(new System.Windows.Controls.ProgressBar
                {
                    IsIndeterminate = true, Width = 320,
                });
                new Window
                {
                    Title = "ProgressBar 自检", Width = 420, Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = panel,
                }.ShowDialog();
                Shutdown();
                return;
            }
            if (mode == "alert")
            {
                Views.Shared.MixCutDialog.Alert(null, "模型未配置",
                    "还没有配置 AI 模型的 API Key，无法生成混剪方案。\n\n请到「设置 → AI 模型」里填写后重试。");
            }
            else
            {
                Views.Shared.MixCutDialog.Confirm(null, "删除项目「双十一投放」",
                    "该项目下的 3 个视频 · 21 个分镜 · 5 个方案都会一并删除，此操作不可撤销。",
                    confirmText: "删除项目", cancelText: "取消", destructive: true, icon: "🗑");
            }
            Shutdown();
            return;
        }

        // v0.3.1 对齐迁移：把已有分镜缩略图全部重生为「首帧」（用户升级 v0.6.0 后第一次启动自动跑一次）
        // fire-and-forget，不阻塞 UI 启动；用 AppSettings flag 防重，失败不阻断启动。
        _ = Task.Run(async () => await RegenerateSegmentThumbnailsToFirstFrameAsync(
            _host.Services.GetRequiredService<IDbContextFactory<MixCutDbContext>>(),
            _host.Services.GetRequiredService<FFmpegRunner>(),
            _host.Services.GetRequiredService<AppSettings>(),
            _host.Services.GetRequiredService<ILogger<App>>()));

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();
        // QW-10：主窗口已就绪 → 关闭启动闪屏。先 Show 主窗口再关闭闪屏，避免中间出现空屏闪烁。
        try { splash?.Close(); }
        catch (Exception ex) { Log.Warning(ex, "[Splash] 关闭闪屏失败，忽略"); }

        // 把全局 Toast Overlay 附到主窗口（之后任何代码可以 ToastCenter.Shared.Show(...)）。
        Views.Shared.ToastCenter.Shared.AttachTo(window);

        // 首次启动：展示 4 步使用引导（macOS @AppStorage("hasCompletedOnboarding") 对应）。
        var settings = _host.Services.GetRequiredService<AppSettings>();

        // 全局字幕字号比例：从设置读初值 + 绑定落盘回调（滑条 / 预览 / 导出烧录共用一份）。
        ViewModels.SubtitleFontState.Shared.Attach(settings);

        // issue #23 Agent 接入：主窗口就绪后装配并启动内嵌 MCP server（按设置开关/端口；
        // 端口被占用只 Toast 提示不崩溃）。
        _host.Services.GetRequiredService<Services.Agent.AgentGateway>().Configure();

        if (!settings.HasCompletedOnboarding)
        {
            var onboarding = _host.Services.GetRequiredService<OnboardingWindow>();
            // 兜底：仅当主窗口已就绪（有 HWND）才设 Owner；否则设 Owner 会抛「无法将 Owner 设置为之前
            // 未显示的 Window」（见 RunDataMigrationWithProgress 的 ShutdownMode 说明）。设不了就不设，不阻断引导。
            if (System.Windows.PresentationSource.FromVisual(window) is not null)
            {
                onboarding.Owner = window;
            }
            onboarding.ShowDialog();
        }

        base.OnStartup(e);
    }

    /// <summary>统一记录致命异常到日志（强制 flush）+ 弹窗告知用户，不让进程静默退出。</summary>
    /// <summary>
    /// 执行待处理的数据目录迁移，期间弹一个带进度条的小窗（拷 GB 级视频/模型可能要几分钟，不能让用户以为卡死）。
    /// 同步阻塞启动直到搬完 —— 此刻 host 还没起、Serilog 未就绪，迁移器自己写 migration.log，不依赖 Log.X。
    /// 用 DispatcherFrame 泵消息，让进度条在后台拷贝时能实时刷新。失败时给人话提示（旧数据未动）。
    /// </summary>
    private void RunDataMigrationWithProgress()
    {
        // 关键修复（v0.10.1）：此刻是启动最早期，splash / MainWindow 都还没建，下面这个进度窗是 App 里
        // 唯一的窗口。默认 ShutdownMode=OnLastWindowClose 下，win.Close() 关掉「最后一个窗口」会触发
        // Application 关闭流程 → 之后 MainWindow.Show() 拿不到 HWND → onboarding.Owner=window 抛
        // 「无法将 Owner 设置为之前未显示的 Window」→ 启动崩溃。迁移期间临时改 OnExplicitShutdown，finally 还原。
        var prevShutdownMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
        var win = new Window
        {
            Title = "MixCut 数据迁移",
            Width = 480,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Topmost = true,
            ShowInTaskbar = true,
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        var text = new TextBlock
        {
            Text = "正在把数据迁移到新位置，请勿关机或关闭窗口…",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14),
            TextWrapping = TextWrapping.Wrap,
        };
        var bar = new ProgressBar { Height = 20, Minimum = 0, Maximum = 1 };
        panel.Children.Add(text);
        panel.Children.Add(bar);
        win.Content = panel;
        win.Show();

        Infrastructure.DataDirectoryMigrator.MigrationResult? result = null;
        var frame = new DispatcherFrame();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            result = Infrastructure.DataDirectoryMigrator.RunPendingIfAny((msg, p) =>
                Dispatcher.Invoke(() => { text.Text = msg; bar.Value = p; }));
        }).ContinueWith(_ => Dispatcher.Invoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); // 泵消息等迁移完成（进度条可刷新），完成后 Continue=false 退出
        try { win.Close(); } catch { }

        if (result is { Outcome: Infrastructure.DataDirectoryMigrator.MigrationOutcome.Failed })
        {
            MessageBox.Show(
                $"数据迁移未完成，已保留原位置的数据（未丢失）。\n\n原因：{result.Error}\n\n" +
                "可稍后在「设置 → 存储」里重试，或换一个空间更充足的盘。",
                "MixCut 数据迁移", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        }
        finally
        {
            ShutdownMode = prevShutdownMode; // 还原为默认（OnLastWindowClose），让 MainWindow 关闭时能正常退出
        }
    }

    private static void LogFatalAndShow(Exception ex, string source)
    {
        try
        {
            Log.Fatal(ex, "[{Source}] 未处理异常: {Message}", source, ex.Message);
            Log.CloseAndFlush();
            // 重新初始化 Logger，避免后续日志写不出
            Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(AppPaths.LogDirectory, "mixcut-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch
        {
            // 日志失败也不能影响弹窗
        }
        try
        {
            // §红线：不把异常类型名（如 System.Text.Json.JsonException）/ stack trace 丢用户脸上。
            // 完整堆栈已由上面的 Log.Fatal 写进日志；这里只给人话 + 日志位置 + 下一步。
            MessageBox.Show(
                "MixCut 遇到意外错误，已自动记录诊断信息。\n\n" +
                "你可以尝试重新打开应用继续使用；若反复出现，请把下面文件夹里的日志发给我们协助排查：\n\n" +
                AppPaths.LogDirectory,
                "MixCut",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // 弹窗也可能失败（如崩在 dispatcher 关闭后）
        }
    }

    /// <summary>
    /// 在 SQLite 表上幂等加列（v0.6.0 升级老库用）。EF Core 的 EnsureCreated 跳过已存在表，
    /// 不会自动 ALTER，所以新加字段必须手动补，否则老用户升级后会撞 SqliteException。
    /// </summary>
    private static void AddColumnIfMissing(MixCutDbContext db, string table, string column, string columnDef)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();

            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = checkCmd.ExecuteReader();
            var exists = false;
            while (reader.Read())
            {
                if (string.Equals(reader["name"]?.ToString(), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            reader.Close();

            if (!exists)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDef}";
                alter.ExecuteNonQuery();
                Log.Information("[SchemaMigration] 补列: {Table}.{Column}", table, column);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SchemaMigration] 补列失败 {Table}.{Column}", table, column);
        }
    }

    /// <summary>
    /// 在 SQLite 上幂等建表（升级老库用）。EF Core 的 <c>EnsureCreated</c> 只在数据库<b>整体不存在</b>时
    /// 才建全部表；老用户库已存在 → 新加的实体表（如 SegmentDubs）不会被创建，写入即撞「no such table」。
    /// 故新表必须显式 <c>CREATE TABLE IF NOT EXISTS</c>。DDL 需与 EF 映射一致（Guid→TEXT、double→REAL、
    /// int/bool→INTEGER、string→TEXT），列顺序无所谓，EF 按列名读写。
    /// </summary>
    private static void CreateTableIfMissing(MixCutDbContext db, string table, string createSql)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();

            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name=$n";
            var p = checkCmd.CreateParameter();
            p.ParameterName = "$n";
            p.Value = table;
            checkCmd.Parameters.Add(p);
            var exists = checkCmd.ExecuteScalar() != null;

            if (!exists)
            {
                using var create = conn.CreateCommand();
                create.CommandText = createSql;
                create.ExecuteNonQuery();
                Log.Information("[SchemaMigration] 建表: {Table}", table);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SchemaMigration] 建表失败 {Table}", table);
        }
    }

    /// <summary>
    /// 为所有老项目补建「自定义组合」容器策略（首次升级后跑一次）。
    /// 对齐 Mac v0.3.0 ensureCustomGroupStrategy。
    /// 用 AppSettings.DidEnsureCustomGroupStrategyV1 防重；失败不阻断启动。
    /// </summary>
    private static void EnsureCustomGroupStrategy(MixCutDbContext db, AppSettings settings, Microsoft.Extensions.Logging.ILogger logger)
    {
        if (settings.DidEnsureCustomGroupStrategyV1)
        {
            return;
        }

        try
        {
            var projects = db.Projects.Include(p => p.Strategies).ToList();
            var added = 0;
            foreach (var p in projects)
            {
                if (p.Strategies.Any(s => s.IsCustomGroup))
                {
                    continue;
                }
                db.Strategies.Add(new MixStrategy
                {
                    Name = "自定义组合",
                    Style = string.Empty,
                    StrategyDescription = "手动挑选分镜组合的方案",
                    TargetAudience = string.Empty,
                    NarrativeStructure = string.Empty,
                    TargetDuration = 0,
                    IsCustomGroup = true,
                    ProjectId = p.Id,
                });
                added++;
            }

            if (added > 0)
            {
                db.SaveChanges();
                logger.LogInformation("[CustomGroupMigration] 已为 {Count} 个老项目补建「自定义组合」策略", added);
            }
            else
            {
                logger.LogInformation("[CustomGroupMigration] 无需补建（已有 / 无老项目）");
            }

            settings.DidEnsureCustomGroupStrategyV1 = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CustomGroupMigration] 迁移失败（不阻断启动）");
        }
    }

    /// <summary>
    /// issue #7 帧精确重构：把旧分镜的「秒」边界按所属视频 fps 回填为帧号（StartFrame/EndFrame/Fps），
    /// 并把秒量化到帧网格（StartTime = StartFrame/Fps），消除历史浮点累积误差。
    /// 用 AppSettings.DidBackfillSegmentFramesV1 防重；视频 fps 未知（=0）的分镜跳过、留待下次；失败不阻断启动。
    /// </summary>
    private static void BackfillSegmentFrames(MixCutDbContext db, AppSettings settings, Microsoft.Extensions.Logging.ILogger logger)
    {
        if (settings.DidBackfillSegmentFramesV1)
        {
            return;
        }

        try
        {
            // 只回填尚未回填（Fps==0）的分镜；连带其视频拿 fps。
            var segments = db.Segments.Include(s => s.Video).Where(s => s.Fps <= 0).ToList();
            var filled = 0;
            var skippedNoFps = 0;
            foreach (var seg in segments)
            {
                var fps = seg.Video?.Fps ?? 0;
                if (fps <= 0)
                {
                    skippedNoFps++;
                    continue;
                }
                seg.SetBoundsSeconds(seg.StartTime, seg.EndTime, fps);
                filled++;
            }

            if (filled > 0)
            {
                db.SaveChanges();
            }
            logger.LogInformation(
                "[FrameBackfill] 回填 {Filled} 个分镜帧号；跳过 {Skipped} 个（视频 fps 未知，留待下次）",
                filled, skippedNoFps);

            // 仅当没有「因 fps 缺失而跳过」的分镜时才标记完成，否则下次启动继续补。
            if (skippedNoFps == 0)
            {
                settings.DidBackfillSegmentFramesV1 = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[FrameBackfill] 帧号回填失败（不阻断启动）");
        }
    }

    /// <summary>
    /// 把已有分镜缩略图全部重生为「首帧」（v0.3.1 对齐 Mac regenerateSegmentThumbnailsToFirstFrame）。
    /// 用 AppSettings.DidRegenerateSegmentThumbnailsToFirstFrameV1 防重；并发 fire-and-forget 不阻塞 UI；
    /// 单个 segment 失败不阻断整体（用户后续手动操作触发 RepairMissingThumbnailsAsync 兜底）。
    /// </summary>
    private static async Task RegenerateSegmentThumbnailsToFirstFrameAsync(
        IDbContextFactory<MixCutDbContext> dbFactory,
        FFmpegRunner ffmpeg,
        AppSettings settings,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        if (settings.DidRegenerateSegmentThumbnailsToFirstFrameV1)
        {
            return;
        }

        try
        {
            using var db = dbFactory.CreateDbContext();
            var segments = await db.Segments
                .Include(s => s.Video)
                .ToListAsync();

            var total = segments.Count;
            if (total == 0)
            {
                settings.DidRegenerateSegmentThumbnailsToFirstFrameV1 = true;
                logger.LogInformation("[ThumbMigration] 无分镜需要迁移（新用户）");
                return;
            }

            var thumbDir = Path.Combine(AppPaths.Root, "Thumbnails");
            Directory.CreateDirectory(thumbDir);

            var regenerated = 0;
            var sem = new System.Threading.SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
            var tasks = segments
                .Where(s => s.Video is not null && File.Exists(s.Video.LocalPath))
                .Select(async s =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        if (!string.IsNullOrEmpty(s.ThumbnailPath) && File.Exists(s.ThumbnailPath))
                        {
                            try { File.Delete(s.ThumbnailPath); } catch { /* 已被占用等情况忽略 */ }
                        }
                        var firstFrameTime = Math.Max(0, s.StartTime + 0.1);
                        var outPath = Path.Combine(thumbDir, $"seg_{s.Id}.jpg");
                        await ffmpeg.GenerateThumbnailAsync(s.Video!.LocalPath, outPath, firstFrameTime);
                        s.ThumbnailPath = outPath;
                        System.Threading.Interlocked.Increment(ref regenerated);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[ThumbMigration] 单分镜失败 id={Id}", s.Id);
                    }
                    finally
                    {
                        sem.Release();
                    }
                });

            await Task.WhenAll(tasks);
            await db.SaveChangesAsync();
            Infrastructure.ThumbnailCache.Shared.Clear();
            logger.LogInformation("[ThumbMigration] 重生完成: {Done}/{Total}", regenerated, total);

            settings.DidRegenerateSegmentThumbnailsToFirstFrameV1 = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ThumbMigration] 迁移失败（不阻断启动）");
        }
    }

    /// <summary>
    /// 重置上次未正常完成（卡在 DetectingScenes / Transcribing / Analyzing）的视频状态。
    /// 若已有分镜则视为已完成；否则视为失败。对齐 macOS 版 resetStaleAnalyzingStatus。
    /// </summary>
    private static void ResetStaleAnalyzingStatus(Data.MixCutDbContext db)
    {
        try
        {
            var stale = db.Videos
                .Where(v => v.Status == Models.VideoStatus.DetectingScenes
                         || v.Status == Models.VideoStatus.Transcribing
                         || v.Status == Models.VideoStatus.Analyzing)
                .ToList();
            if (stale.Count == 0)
            {
                return;
            }
            foreach (var v in stale)
            {
                var segCount = db.Segments.Count(s => s.VideoId == v.Id);
                if (segCount > 0)
                {
                    v.Status = Models.VideoStatus.Completed;
                }
                else
                {
                    v.Status = Models.VideoStatus.Failed;
                    if (string.IsNullOrEmpty(v.ErrorMessage))
                    {
                        v.ErrorMessage = "上次分析未完成（应用异常退出）";
                    }
                }
            }
            db.SaveChanges();
            Log.Information("已重置 {Count} 个卡死的视频状态", stale.Count);
        }
        catch (Exception ex)
        {
            // 状态清理失败不能阻止应用启动 —— 大不了让用户在 ImportView 看到「分析中」假象。
            Log.Warning(ex, "重置卡死视频状态失败，忽略");
        }
    }

    /// <summary>
    /// 把历史库里 Status='Archived' 的项目恢复成 'Completed'（一次性收尾迁移，幂等）。
    ///
    /// 背景：issue #16 移除了「归档」功能 —— 归档后项目从列表消失且无从恢复，是个只会误伤用户的功能。
    /// 但入口删了之后，之前已经被归档的项目仍然躺在库里、仍然被主列表的 `!= Archived` 过滤掉：
    /// 用户看不见它、也没有任何入口把它拿回来，等于数据被静默吞掉。
    ///
    /// 这里用原生 SQL 而不是 LINQ + 枚举，是因为迁移跑完之后 <c>ProjectStatus.Archived</c> 这个
    /// 枚举值本身就会被删除 —— 迁移代码不能依赖一个即将不存在的符号。
    /// </summary>
    private static void RestoreArchivedProjects(Data.MixCutDbContext db, ILogger<App> logger)
    {
        try
        {
            var n = db.Database.ExecuteSqlRaw(
                "UPDATE Projects SET Status = 'Completed' WHERE Status = 'Archived'");
            if (n > 0)
            {
                logger.LogInformation("[ArchiveCleanup] 已把 {Count} 个历史归档项目恢复为「已完成」，重新出现在项目列表", n);
            }
        }
        catch (Exception ex)
        {
            // 迁移失败不该挡住启动：最坏情况是那几个老项目继续不显示，与迁移前一致。
            logger.LogWarning(ex, "[ArchiveCleanup] 恢复历史归档项目失败，忽略");
        }
    }

    /// <summary>
    /// 认领崩溃/强退前卡在「生成中」的 AI 画面变体（<see cref="Models.ShotVariantStatus.Generating"/>）。
    ///
    /// 为什么必须做：Wan 视频编辑是**异步付费任务** —— 提交成功就已经产生费用，taskId 会立刻落库
    /// （见 ShotVariantService「提交成功 —— 立刻落库 taskId」）。但如果应用在轮询期间挂掉，
    /// 这条记录会永远停在 Generating：UI 只对 TimedOut 状态显示「重试」按钮，于是
    /// **taskId 明明在库里、云端结果 24 小时内可免费取回，用户却拿不到，钱白花了**。
    ///
    /// 处理方式与是否已扣费严格对应：
    ///   有 taskId → TimedOut：UI 出现「重试」，走已有的 ResumeAsync 用同一 taskId 续查，不重复扣费；
    ///   无 taskId → Failed：说明还没提交成功，没产生费用，让用户重新发起即可。
    /// </summary>
    private static void ResetStaleGeneratingVariants(Data.MixCutDbContext db, ILogger<App> logger)
    {
        try
        {
            var generating = nameof(Models.ShotVariantStatus.Generating);
            var stuck = db.ShotVariants.Where(v => v.StatusRaw == generating).ToList();
            if (stuck.Count == 0)
            {
                return;
            }

            var claimable = 0;
            foreach (var v in stuck)
            {
                if (!string.IsNullOrEmpty(v.TaskId))
                {
                    v.Status = Models.ShotVariantStatus.TimedOut;
                    claimable++;
                }
                else
                {
                    v.Status = Models.ShotVariantStatus.Failed;
                }
            }
            db.SaveChanges();
            logger.LogInformation(
                "[VariantRecover] 上次异常退出时有 {Total} 个 AI 画面任务在生成中：{Claimable} 个可凭 taskId 免费取回（已置为可重试），{Lost} 个未提交成功（已置失败）",
                stuck.Count, claimable, stuck.Count - claimable);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[VariantRecover] 恢复 AI 画面生成任务失败，忽略");
        }
    }

    /// <summary>
    /// 重置上次卡在「生成方案中」(Generating) 的项目状态 → Ready（P1-73）。
    /// 场景：AI 生成方案过程中应用崩溃 / 断电 / 强退，项目永久卡 Generating，用户无解。
    /// 配合 SchemeViewModel 失败/取消时的状态回滚，构成「生成中断」双端兜底。
    /// </summary>
    private static void ResetStaleGeneratingProjects(Data.MixCutDbContext db)
    {
        try
        {
            var stuck = db.Projects
                .Where(p => p.Status == Models.ProjectStatus.Generating)
                .ToList();
            if (stuck.Count == 0)
            {
                return;
            }
            foreach (var p in stuck)
            {
                p.Status = Models.ProjectStatus.Ready;
            }
            db.SaveChanges();
            Log.Information("已重置 {Count} 个卡在「生成方案中」的项目状态 → Ready", stuck.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "重置卡死项目状态失败，忽略");
        }
    }

    /// <summary>
    /// 根据 EnvironmentDiagnostics 结果按需弹窗。原则：
    /// - 致命（binaries 缺失 / AppData 不可写）= 阻塞 OK 弹窗，用户点完仍可继续（让他看 log 自救）
    /// - 中等（CPU 不支持 AVX2 / OS 太老）= 非阻塞警告，ASR 功能后续会失败但其它能用
    /// - 低（VC Runtime 缺 / OneDrive 目录）= 仅日志，不打扰
    /// </summary>
    private static void ShowEnvDialogsIfNeeded(Infrastructure.EnvironmentDiagnostics.Report r)
    {
        // 致命：内置二进制缺
        if (!r.BinariesOk)
        {
            MessageBox.Show(
                "MixCut 安装包不完整：检测到以下文件缺失\n\n" +
                string.Join("\n", r.BinariesMissing) +
                "\n\n请重新下载安装包，或将 MixCut 解压目录加入杀软白名单后再试。",
                "MixCut 启动异常", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // 致命：AppData 不可写
        if (!r.AppDataWritable)
        {
            MessageBox.Show(
                "MixCut 数据目录无法写入：\n\n" + r.AppDataRoot +
                "\n\n可能原因：被杀软拦截、权限被回收、磁盘已满或漫游目录损坏。\n" +
                "请检查权限或联系管理员。",
                "MixCut 数据目录错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // 中等：CPU 不支持 AVX2 —— ASR 会跑不起来
        if (!r.CpuAvx2)
        {
            MessageBox.Show(
                "您的 CPU 不支持 AVX2 指令集。\n\n" +
                "MixCut 的语音识别（Whisper）需要 AVX2，运行时会立即崩溃。\n" +
                "其它功能（视频导入 / AI 切分 / 方案生成 / 导出）仍可正常使用。\n\n" +
                "建议在 Intel Haswell（2013+）/ AMD Excavator（2015+）及更新 CPU 上运行。",
                "MixCut 兼容性提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 中等：OS 太老（Win10 1809 以下）
        if (r.OsBuild > 0 && r.OsBuild < 17763)
        {
            MessageBox.Show(
                $"MixCut 要求 Windows 10 1809 (Build 17763) 或更高，当前为 Build {r.OsBuild}。\n\n" +
                "应用可能可以启动但部分系统 API 不可用，建议升级操作系统。",
                "MixCut 兼容性提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("MixCut 退出");
        // issue #23：先停内嵌 MCP server 的监听（listener 不随 host 生命周期管理）。
        try
        {
            _host.Services.GetRequiredService<Services.Agent.AgentGateway>().Stop();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MCP] 停止 Agent 服务失败，忽略");
        }
        using (_host)
        {
            await _host.StopAsync();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
