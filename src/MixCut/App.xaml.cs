using System.IO;
using System.Windows;
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
        services.AddSingleton<SchemeGenerationService>();
        services.AddSingleton<BoundaryOptimizerService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<BatchSegmentExportService>();
        // v0.3.1 对齐：启动期版本检测（GitHub 优先 / Gitee 容灾）
        services.AddSingleton<Services.UpdateChecker.UpdateChecker>();

        // v0.5.0 分镜级 AI 配音：分离 / 克隆 / 合成 / 对齐 / 改写（单例）。
        services.AddSingleton<Services.Dubbing.VocalSeparationService>();
        services.AddSingleton<Services.Dubbing.VoiceCloneService>();
        services.AddSingleton<Services.Dubbing.CloneTtsClient>();
        services.AddSingleton<Services.Dubbing.DubAudioFinalizer>();
        services.AddSingleton<Services.Dubbing.ScriptRewriteService>();

        // ViewModel（单例，主窗口持有）。
        services.AddSingleton<ProjectViewModel>();
        services.AddSingleton<ImportViewModel>();
        services.AddSingleton<SegmentLibraryViewModel>();
        services.AddSingleton<SchemeViewModel>();
        services.AddSingleton<DubbingViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<UpdateBannerViewModel>();

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
        _ = Task.Run(() => Infrastructure.HardwareEncoderProbe.EagerInit());

        // 统一捕获未处理异常 —— 之前出过 async void 事件处理器异常直接终止进程的事故，
        // 看 EventLog 也拿不到堆栈。三个 hook 兜底：
        //   - DispatcherUnhandledException：UI 线程同步异常 / async void 抛出
        //   - TaskScheduler.UnobservedTaskException：Task 抛了但没人 await
        //   - AppDomain.UnhandledException：以上漏网（包括子线程崩溃）
        // 都 Log.Fatal 写盘 + 弹 MessageBox + flush，让用户看得到错误而不是窗口神秘消失。
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
            LogFatalAndShow(args.Exception, "未观察的 Task 异常");
            args.SetObserved();
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
            AddColumnIfMissing(db, "SchemeSegments", "SelectedSegmentDubId", "TEXT");
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
                    CONSTRAINT ""FK_SegmentDubs_Segments_SegmentId"" FOREIGN KEY (""SegmentId"")
                        REFERENCES ""Segments"" (""Id"") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ""IX_SegmentDubs_SegmentId"" ON ""SegmentDubs"" (""SegmentId"");");

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
        }

        // issue #6 叙事结构 AI 自测：`--selftest-narrative` 用真 DeepSeek 跑一次生成后退出（构建机验证）。
        if (Array.IndexOf(e.Args, "--selftest-narrative") >= 0)
        {
            await RunNarrativeSelfTestAsync();
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
        if (!settings.HasCompletedOnboarding)
        {
            var onboarding = _host.Services.GetRequiredService<OnboardingWindow>();
            onboarding.Owner = window;
            onboarding.ShowDialog();
        }

        base.OnStartup(e);
    }

    /// <summary>统一记录致命异常到日志（强制 flush）+ 弹窗告知用户，不让进程静默退出。</summary>
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
            MessageBox.Show(
                $"MixCut 遇到错误（{source}）：\n\n{ex.Message}\n\n{ex.GetType().FullName}\n详细堆栈已写入日志：\n{AppPaths.LogDirectory}",
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
        using (_host)
        {
            await _host.StopAsync();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
