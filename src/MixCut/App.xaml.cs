using System.IO;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MixCut.Data;
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

        // ViewModel（单例，主窗口持有）。
        services.AddSingleton<ProjectViewModel>();
        services.AddSingleton<ImportViewModel>();
        services.AddSingleton<SegmentLibraryViewModel>();
        services.AddSingleton<SchemeViewModel>();
        services.AddSingleton<MainViewModel>();

        // 视图。
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<OnboardingWindow>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();
        Log.Information("MixCut 启动，数据目录: {Root}", AppPaths.Root);

        // 启动期环境一站式体检（v0.4.0）：OS/CPU/AVX2/VC Runtime/Binaries/AppData/InstallDir。
        // 一行汇总 [EnvDiag] 写日志，关键失败立即弹窗。让用户在撞运行时崩溃前先看到清晰指引。
        var envReport = Infrastructure.EnvironmentDiagnostics.RunAndLog();
        ShowEnvDialogsIfNeeded(envReport);

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

            // 清理上次未正常完成的中间态视频（崩溃/强退导致状态卡在分析中）。
            // 对齐 macOS 版 MixCutApp.resetStaleAnalyzingStatus。
            ResetStaleAnalyzingStatus(db);
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.Show();
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
