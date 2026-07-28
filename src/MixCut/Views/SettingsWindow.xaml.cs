using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using MixCut.Infrastructure;
using MixCut.Services.AI;
using MixCut.Services.ASR;
using MixCut.Utilities;
using MixCut.Views.Shared;

namespace MixCut.Views;

/// <summary>设置窗口（AI 提供商 + 通用配置）。对应 macOS 版 SettingsView。</summary>
public partial class SettingsWindow : Window
{
    private const string MaskedKey = "••••••••••••••••••••";

    private readonly AppSettings _settings;
    private readonly ASRService _asrService;
    private readonly Services.Agent.AgentGateway _agentGateway;
    private bool _loaded;
    private bool _apiKeyHidden = true;
    private string? _actualKey;
    private bool _agentLoaded;

    public SettingsWindow(AppSettings settings, ASRService asrService, Services.Agent.AgentGateway agentGateway)
    {
        _settings = settings;
        _asrService = asrService;
        _agentGateway = agentGateway;
        InitializeComponent();

        foreach (var provider in Enum.GetValues<AIProviderType>())
        {
            ProviderCombo.Items.Add(provider.DisplayName());
        }
        foreach (var platform in Enum.GetValues<RelayPlatform>())
        {
            RelayPlatformCombo.Items.Add(platform.DisplayName());
        }

        _loaded = true;
        ProviderCombo.SelectedIndex = (int)_settings.ActiveProvider;

        BuildGeneralTab();
        BuildAgentTab();
    }

    private AIProviderType CurrentProvider =>
        ProviderCombo.SelectedIndex >= 0 ? (AIProviderType)ProviderCombo.SelectedIndex : AIProviderType.Qwen;

    // ---- API tab ----

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || ProviderCombo.SelectedIndex < 0)
        {
            return;
        }
        var provider = CurrentProvider;
        SectionHeader.Text = provider.DisplayName() + " 配置";

        _actualKey = _settings.GetApiKey(provider);
        var hasKey = !string.IsNullOrEmpty(_actualKey);
        _apiKeyHidden = hasKey; // 已有 key 默认隐藏；空则显示空框
        ApiKeyBox.Text = hasKey ? MaskedKey : string.Empty;
        EyeIcon.Text = _apiKeyHidden ? "👁" : "🙈";

        SavedBadge.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        ClearKeyButton.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;

        RelayPanel.Visibility = provider == AIProviderType.ClaudeRelay
            ? Visibility.Visible : Visibility.Collapsed;
        CustomPanel.Visibility = provider == AIProviderType.Custom
            ? Visibility.Visible : Visibility.Collapsed;

        RelayUrlBox.Text = _settings.RelayBaseUrl;
        RelayPlatformCombo.SelectedIndex = (int)_settings.RelayPlatform;
        CustomUrlBox.Text = _settings.CustomBaseUrl;
        CustomModelBox.Text = _settings.CustomModelName;

        RefreshModels();
    }

    private void OnRelayPlatformChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && CurrentProvider == AIProviderType.ClaudeRelay)
        {
            RefreshModels();
        }
    }

    private void OnToggleVisibility(object sender, RoutedEventArgs e)
    {
        _apiKeyHidden = !_apiKeyHidden;
        EyeIcon.Text = _apiKeyHidden ? "👁" : "🙈";
        if (_apiKeyHidden)
        {
            if (!string.IsNullOrEmpty(_actualKey) && ApiKeyBox.Text == _actualKey)
            {
                ApiKeyBox.Text = MaskedKey;
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(_actualKey) && ApiKeyBox.Text.StartsWith("•", StringComparison.Ordinal))
            {
                ApiKeyBox.Text = _actualKey;
            }
        }
    }

    private void RefreshModels()
    {
        var provider = CurrentProvider;
        ModelCombo.Items.Clear();

        IReadOnlyList<string> models = provider switch
        {
            AIProviderType.ClaudeRelay => RelayPlatformCombo.SelectedIndex >= 0
                ? ((RelayPlatform)RelayPlatformCombo.SelectedIndex).Models()
                : Array.Empty<string>(),
            AIProviderType.Custom => Array.Empty<string>(),
            _ => provider.StaticModels(),
        };
        // 下拉里显示 "{ID} — {中文说明}"，文本可编辑（自定义模型 ID）。
        // 对齐 macOS modelDisplayName，下拉看说明，文本框存 ID。
        foreach (var model in models)
        {
            var label = AIProviderCatalog.ModelDisplayName(model);
            ModelCombo.Items.Add(label == model ? model : $"{model}    — {label}");
        }
        ModelCombo.Text = _settings.SelectedModel(provider);
        UpdateModelHelpText();
    }

    /// <summary>更新「模型」下方的中文说明（基于当前 ModelCombo.Text 中的 ID 部分）。</summary>
    private void UpdateModelHelpText()
    {
        if (ModelHelpText is null)
        {
            return;
        }
        var raw = ExtractModelId(ModelCombo.Text);
        var label = AIProviderCatalog.ModelDisplayName(raw);
        ModelHelpText.Text = string.Equals(label, raw, StringComparison.Ordinal)
            ? string.Empty
            : "💡 " + label;
        ModelHelpText.Visibility = string.IsNullOrEmpty(ModelHelpText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>从 "qwen-plus-latest    — Qwen Plus (主力 · 推荐)" 这种文本中取出真实 ID。</summary>
    private static string ExtractModelId(string text)
    {
        text = text.Trim();
        var sepIdx = text.IndexOf("    — ", StringComparison.Ordinal);
        return sepIdx > 0 ? text[..sepIdx].Trim() : text;
    }

    private void OnModelComboSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateModelHelpText();

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        var provider = CurrentProvider;
        var typed = ApiKeyBox.Text;
        if (string.IsNullOrEmpty(typed) || typed.StartsWith("•", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(_actualKey))
            {
                MixCutDialog.Alert(this, "请先填写 API Key",
                    "保存前需要填入 API Key，否则无法调用 AI 生成方案。可在提供商的控制台复制后粘贴到上方输入框。",
                    icon: "🔑");
                return;
            }
            // 用户没改 key，但可能改了别的字段 → 保留原 key
            typed = _actualKey;
        }
        if (provider == AIProviderType.Custom
            && (string.IsNullOrWhiteSpace(CustomUrlBox.Text) || string.IsNullOrWhiteSpace(CustomModelBox.Text)))
        {
            MixCutDialog.Alert(this, "自定义提供商还缺少必填项",
                "使用自定义提供商时，API 地址和模型名称都要填写，MixCut 才知道把请求发到哪里、用哪个模型。",
                icon: "🔑");
            return;
        }
        if (provider == AIProviderType.ClaudeRelay && string.IsNullOrWhiteSpace(RelayUrlBox.Text))
        {
            MixCutDialog.Alert(this, "请填写转发网关地址",
                "选择「转发网关」时必须填入网关地址，MixCut 才能把 AI 请求转发出去。",
                icon: "🔑");
            return;
        }

        _settings.ActiveProvider = provider;
        _settings.SaveApiKey(typed.Trim(), provider);

        if (provider == AIProviderType.ClaudeRelay)
        {
            _settings.RelayBaseUrl = RelayUrlBox.Text.Trim();
            if (RelayPlatformCombo.SelectedIndex >= 0)
            {
                _settings.RelayPlatform = (RelayPlatform)RelayPlatformCombo.SelectedIndex;
            }
        }
        else if (provider == AIProviderType.Custom)
        {
            _settings.CustomBaseUrl = CustomUrlBox.Text.Trim();
            _settings.CustomModelName = CustomModelBox.Text.Trim();
        }

        // 模型 ID 从文本中提取（去掉中文说明部分）。
        var model = ExtractModelId(ModelCombo.Text);
        if (model.Length > 0)
        {
            _settings.SetSelectedModel(model, provider);
        }

        _actualKey = typed.Trim();
        _apiKeyHidden = true;
        ApiKeyBox.Text = MaskedKey;
        EyeIcon.Text = "👁";
        SavedBadge.Visibility = Visibility.Visible;
        ClearKeyButton.Visibility = Visibility.Visible;
    }

    private void OnClearKey(object sender, RoutedEventArgs e)
    {
        var provider = CurrentProvider;
        if (!MixCutDialog.Confirm(this,
                $"清除 {provider.DisplayName()} 的 API Key？",
                "清除后需要重新填写 Key 才能继续用该提供商生成方案，已保存的项目不受影响。",
                confirmText: "清除 Key", cancelText: "保留",
                destructive: true, icon: "🔑"))
        {
            return;
        }
        _settings.RemoveApiKey(provider);
        _actualKey = null;
        ApiKeyBox.Text = string.Empty;
        SavedBadge.Visibility = Visibility.Collapsed;
        ClearKeyButton.Visibility = Visibility.Collapsed;
    }

    // ---- 通用 tab ----

    private void BuildGeneralTab()
    {
        DependencyGrid.RowDefinitions.Clear();
        DependencyGrid.Children.Clear();

        // 名称用「能力」而不是二进制文件名 —— 用户不需要知道 ffprobe 是什么，
        // 他需要知道的是「视频处理能不能用」。缺失时的后果也写清楚。
        AddDependencyRow(0, "视频处理引擎", BundledBinaries.FfmpegAvailable);
        AddDependencyRow(1, "视频信息读取", BundledBinaries.FfprobeAvailable);
        AddDependencyRow(2, "语音识别引擎", BundledBinaries.WhisperAvailable);
        // demucs 之前没列出来 —— 它缺失时「AI 配音」会失败，用户在这页却看不出问题。
        AddDependencyRow(3, "人声分离引擎", BundledBinaries.DemucsAvailable);

        var modelPath = FindWhisperModel();
        AddDependencyRow(4, "语音模型", modelPath is not null,
            modelPath is null ? "未下载（首次分析时自动下载）" : "已就绪");

        // 模型下载横幅。
        if (modelPath is null)
        {
            ModelDownloadBox.Visibility = Visibility.Visible;
            ModelStatusText.Text = "⚠ 语音识别需要先下载模型（首次 AI 分析时也会自动下载）";
            ModelProgress.Value = 0;
            DownloadModelButton.IsEnabled = true;
        }
        else
        {
            ModelDownloadBox.Visibility = Visibility.Collapsed;
        }

        PathPanel.Children.Clear();
        AddPathRow("数据目录", AppPaths.Root);
        AddPathRow("日志目录", AppPaths.LogDirectory);
        AddPathRow("视频目录", AppPaths.VideosDirectory);
        AddPathRow("数据库", AppPaths.DatabaseFile);
        AddPathRow("Whisper 模型目录", AppPaths.WhisperModelsDirectory);

        // 系统信息（v0.5.0：加 GPU 状态 + 并发数透明拆解）。
        var cores = Environment.ProcessorCount;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

        SystemInfoPanel.Children.Clear();
        AddInfoRow(SystemInfoPanel, "CPU 核心数", $"{cores} 核");

        // GPU 状态（编码 / 解码 / Whisper 后端）
        var encDesc = Infrastructure.HardwareEncoderProbe.HardwareDescription;
        var encDetail = Infrastructure.HardwareEncoderProbe.H264Hardware is { } h264
            ? $"{encDesc}（{h264}"
              + (Infrastructure.HardwareEncoderProbe.H265Hardware is { } h265 ? $", {h265}）" : "）")
            : encDesc;
        AddInfoRow(SystemInfoPanel, "GPU 编码加速",
            Infrastructure.HardwareEncoderProbe.H264Hardware is not null
                ? $"✓ {encDetail}"
                : $"✗ {encDesc}");

        AddInfoRow(SystemInfoPanel, "GPU 解码加速",
            Infrastructure.HardwareEncoderProbe.DecodeHwaccel is not null
                ? $"✓ {Infrastructure.HardwareEncoderProbe.DecodeHwaccelDescription}"
                : $"✗ {Infrastructure.HardwareEncoderProbe.DecodeHwaccelDescription}");

        AddInfoRow(SystemInfoPanel, "语音识别后端",
            Infrastructure.HardwareEncoderProbe.WhisperBackendDescription);

        // 并发数（v0.5.0 起 ConcurrencyPolicy 单一来源，含 GPU 加成透明拆解）
        AddInfoRow(SystemInfoPanel, "同时分析视频数",
            Infrastructure.ConcurrencyPolicy.ExplainAnalyzeFormula());
        AddInfoRow(SystemInfoPanel, "导出方式",
            Infrastructure.ConcurrencyPolicy.ExplainExportFormula());

        AddInfoRow(SystemInfoPanel, "版本", version);
        AddInfoRow(SystemInfoPanel, "操作系统", Environment.OSVersion.VersionString);

        // 关于。
        AboutPanel.Children.Clear();
        AddOnboardingResetRow(AboutPanel);
        AddDiagnosticExportRow(AboutPanel);
        AddInfoRow(AboutPanel, "开发者", "MengGang");
        AddInfoRow(AboutPanel, "微信", "13462890087");
        // 这里原本指向 RoshanGH/mixed_cut —— 那是 **macOS 版**仓库，
        // Windows 用户点进去只能看到 dmg，下不到自己能用的版本。
        AddLinkRow(AboutPanel, "下载 / 更新", "GitHub Releases",
            "https://github.com/RoshanGH/mixcut-windows/releases/latest");
        AddLinkRow(AboutPanel, "项目主页", "RoshanGH/mixcut-windows",
            "https://github.com/RoshanGH/mixcut-windows");
        AddLinkRow(AboutPanel, "问题反馈", "提交 Issue",
            "https://github.com/RoshanGH/mixcut-windows/issues");
    }

    /// <summary>
    /// 在「关于」区添加「使用引导 → 重新查看」按钮 —— 对齐 macOS SettingsView，
    /// 点击后清掉已完成标志并关闭设置窗口，让用户下次启动时再次看到引导。
    /// </summary>
    private void AddOnboardingResetRow(Panel host)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = "使用引导", FontSize = 12, Foreground = Brushes.Gray,
            Margin = new Thickness(0, 5, 8, 0),
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var btn = new Button
        {
            Content = "❓ 重新查看",
            Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        };
        btn.Click += (_, _) =>
        {
            _settings.HasCompletedOnboarding = false;
            MixCutDialog.Alert(this, "使用引导已重置",
                "下次启动 MixCut 时会重新显示新手引导，带你走一遍完整流程。",
                icon: "❓");
        };
        Grid.SetColumn(btn, 1);
        row.Children.Add(btn);
        host.Children.Add(row);
    }

    /// <summary>
    /// 「导出诊断日志」按钮：一键把最近日志 + 系统/显卡/驱动/nvenc 信息打包成桌面 zip，
    /// 提示用户通过侧边栏「微信」发给开发者。导出失败这类 GPU 路径问题靠这个回收现场。
    /// </summary>
    private void AddDiagnosticExportRow(Panel host)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = "遇到问题", FontSize = 12, Foreground = Brushes.Gray,
            Margin = new Thickness(0, 5, 8, 0),
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var btn = new Button
        {
            Content = "📋 导出诊断日志",
            Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        };
        btn.Click += async (_, _) =>
        {
            // async void 事件处理器：全程 try/catch，异常不许逃逸。
            try
            {
                btn.IsEnabled = false;
                btn.Content = "正在导出…";
                var zipPath = await Infrastructure.DiagnosticExport.ExportAsync();
                btn.Content = "📋 导出诊断日志";
                btn.IsEnabled = true;

                if (MixCutDialog.Confirm(this,
                        "诊断日志已导出到桌面",
                        "文件名：" + System.IO.Path.GetFileName(zipPath) + "\n\n" +
                        "请通过侧边栏「微信」联系开发者，并把这个文件发过去，便于排查问题。",
                        confirmText: "在文件夹中显示", cancelText: "稍后再说", icon: "📋"))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe", Arguments = $"/select,\"{zipPath}\"", UseShellExecute = true,
                    });
                }
            }
            catch (Exception ex)
            {
                btn.Content = "📋 导出诊断日志";
                btn.IsEnabled = true;
                Serilog.Log.Warning(ex, "[DiagnosticExport] 导出诊断日志失败");
                MixCutDialog.Error(this, "诊断日志导出失败",
                    MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex) + "\n\n" +
                    "你也可以手动把下面这个文件夹里最新的 .log 文件发给开发者：\n" + Utilities.AppPaths.LogDirectory);
            }
        };
        Grid.SetColumn(btn, 1);
        row.Children.Add(btn);
        host.Children.Add(row);
    }

    private void AddDependencyRow(int row, string name, bool available, string? statusOverride = null)
    {
        DependencyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var nameTb = new TextBlock
        {
            Text = name, FontSize = 12, Margin = new Thickness(0, 4, 8, 4),
        };
        Grid.SetRow(nameTb, row);
        Grid.SetColumn(nameTb, 0);
        DependencyGrid.Children.Add(nameTb);

        // 「已安装」对内置组件是个奇怪的说法（用户没装过它们，是随包带的）。
        // 缺失才是异常情况，此时提示重装 —— 用户看到「✗ 未找到」也知道该怎么办。
        var statusText = statusOverride ?? (available ? "✓ 已就绪" : "✗ 缺失（请重新安装 MixCut）");
        var color = available ? Color.FromRgb(0x2E, 0x8B, 0x57) : Color.FromRgb(0xD3, 0x3A, 0x3A);
        var statusTb = new TextBlock
        {
            Text = statusText, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color), Margin = new Thickness(0, 4, 0, 4),
        };
        Grid.SetRow(statusTb, row);
        Grid.SetColumn(statusTb, 1);
        DependencyGrid.Children.Add(statusTb);
    }

    private void AddPathRow(string label, string path)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label_ = new TextBlock { Text = label + "：", FontSize = 12 };
        Grid.SetColumn(label_, 0);
        row.Children.Add(label_);

        var pathTb = new TextBlock
        {
            Text = path, FontSize = 11,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = path,
        };
        Grid.SetColumn(pathTb, 1);
        row.Children.Add(pathTb);

        PathPanel.Children.Add(row);
    }

    private static void AddInfoRow(Panel panel, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label_ = new TextBlock { Text = label + "：", FontSize = 12 };
        Grid.SetColumn(label_, 0);
        row.Children.Add(label_);

        var value_ = new TextBlock { Text = value, FontSize = 12, Foreground = Brushes.Gray };
        Grid.SetColumn(value_, 1);
        row.Children.Add(value_);

        panel.Children.Add(row);
    }

    private static void AddLinkRow(Panel panel, string label, string text, string url)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label_ = new TextBlock { Text = label + "：", FontSize = 12 };
        Grid.SetColumn(label_, 0);
        row.Children.Add(label_);

        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            }
            catch (Exception)
            {
                // ignore
            }
            e.Handled = true;
        };
        var linkTb = new TextBlock { FontSize = 12 };
        linkTb.Inlines.Add(link);
        Grid.SetColumn(linkTb, 1);
        row.Children.Add(linkTb);

        panel.Children.Add(row);
    }

    private static string? FindWhisperModel()
    {
        string[] names = { "ggml-large-v3-turbo", "ggml-medium", "ggml-small", "ggml-base" };
        foreach (var name in names)
        {
            var bundled = Path.Combine(BundledBinaries.BinDirectory, name + ".bin");
            if (File.Exists(bundled))
            {
                return bundled;
            }
            var cached = Path.Combine(AppPaths.WhisperModelsDirectory, name + ".bin");
            if (File.Exists(cached))
            {
                return cached;
            }
        }
        return null;
    }

    private CancellationTokenSource? _downloadCts;
    private DateTime _downloadStartTime;
    private long _lastReceived;
    private DateTime _lastTickTime;

    private async void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        DownloadModelButton.IsEnabled = false;
        DownloadModelButton.Visibility = Visibility.Collapsed;
        CancelDownloadButton.Visibility = Visibility.Visible;
        CancelDownloadButton.IsEnabled = true;
        ModelErrorText.Visibility = Visibility.Collapsed;
        ModelStatusText.Text = "正在连接国内镜像源（hf-mirror.com）...";
        ModelSpeedText.Visibility = Visibility.Visible;
        ModelSpeedText.Text = string.Empty;
        ModelProgress.Value = 0;

        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        _downloadStartTime = DateTime.UtcNow;
        _lastReceived = 0;
        _lastTickTime = DateTime.UtcNow;

        try
        {
            await _asrService.DownloadModelIfNeededAsync(
                "ggml-large-v3-turbo",
                progress => Dispatcher.Invoke(() =>
                {
                    ModelProgress.Value = progress.Percent;
                    if (progress.Total > 0)
                    {
                        var recvMB = progress.Received / 1024.0 / 1024.0;
                        var totalMB = progress.Total / 1024.0 / 1024.0;
                        ModelStatusText.Text = $"下载中 {(int)(progress.Percent * 100)}%  " +
                                               $"({recvMB.ToString("F1", CultureInfo.InvariantCulture)} / " +
                                               $"{totalMB.ToString("F0", CultureInfo.InvariantCulture)} MB)";
                        // 计算瞬时速度 + 预估剩余时间
                        var now = DateTime.UtcNow;
                        var deltaT = (now - _lastTickTime).TotalSeconds;
                        if (deltaT >= 0.5)
                        {
                            var deltaBytes = progress.Received - _lastReceived;
                            var mbps = deltaBytes / 1024.0 / 1024.0 / Math.Max(deltaT, 0.001);
                            var remaining = progress.Total - progress.Received;
                            var etaSec = mbps > 0.01 ? remaining / 1024.0 / 1024.0 / mbps : 0;
                            ModelSpeedText.Text = $"{mbps.ToString("F2", CultureInfo.InvariantCulture)} MB/s" +
                                                  (etaSec > 0 ? $" · 剩余约 {FormatEta(etaSec)}" : string.Empty);
                            _lastReceived = progress.Received;
                            _lastTickTime = now;
                        }
                    }
                    else
                    {
                        ModelStatusText.Text = $"下载中... {(int)(progress.Percent * 100)}%";
                    }
                }),
                cancellationToken: _downloadCts.Token);

            BuildGeneralTab();
            Components.ToastService.Show("✓ 模型下载完成", Components.ToastStyle.Success);
        }
        catch (OperationCanceledException)
        {
            ModelStatusText.Text = "已取消";
            ModelSpeedText.Visibility = Visibility.Collapsed;
            DownloadModelButton.IsEnabled = true;
            DownloadModelButton.Visibility = Visibility.Visible;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
            Components.ToastService.Show("已取消下载", Components.ToastStyle.Warning);
        }
        catch (Exception ex)
        {
            // §红线：ex.Message 对网络异常常是英文（HttpRequestException 等）—— 翻成人话，完整异常进日志。
            Serilog.Log.Error(ex, "[ModelDownload] 模型下载失败");
            ModelErrorText.Text = "下载失败，请检查网络连接后重试（详情见日志）";
            ModelErrorText.Visibility = Visibility.Visible;
            DownloadModelButton.IsEnabled = true;
            DownloadModelButton.Visibility = Visibility.Visible;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
            ModelSpeedText.Visibility = Visibility.Collapsed;
            ModelStatusText.Text = "⚠ 模型下载失败，请重试或检查网络";
        }
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        CancelDownloadButton.IsEnabled = false;
    }

    private static string FormatEta(double seconds)
    {
        if (seconds < 60) return $"{(int)seconds}s";
        if (seconds < 3600) return $"{(int)(seconds / 60)}分{(int)(seconds % 60)}秒";
        return $"{(int)(seconds / 3600)}时{(int)((seconds % 3600) / 60)}分";
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe", Arguments = AppPaths.Root, UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Settings] 打开数据目录失败");
            MixCutDialog.Error(this, "无法打开数据目录",
                MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex) + "\n\n" +
                "你可以复制下面这个路径手动在文件资源管理器中打开：\n" + AppPaths.Root);
        }
    }

    /// <summary>更改数据存储位置：选盘 → 在其中建 MixCut 文件夹 → 校验空间 → 登记迁移 → 重启执行。</summary>
    private void OnChangeDataDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择新的数据存储位置（将在其中创建 MixCut 文件夹）",
        };
        if (dlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dlg.FolderName)) return;

        var toRoot = Path.Combine(dlg.FolderName, "MixCut");
        var current = AppPaths.Root;

        if (PathEquals(toRoot, current))
        {
            MixCutDialog.Alert(this, "所选位置就是当前数据目录",
                "数据已经存放在这里了，无需迁移。想换到别的盘，请选择另一个位置。",
                icon: "📁");
            return;
        }
        // 不允许选到当前数据目录里面（会把自己往自己里拷，死循环）。
        if (DataDirectoryMigrator.IsUnder(toRoot, current))
        {
            MixCutDialog.Error(this, "不能选择当前数据目录里的文件夹",
                "把数据搬到它自己内部会导致无限复制。请换一个位置，建议选另一个盘的根目录（例如 D:\\）。");
            return;
        }

        var toIsDefault = PathEquals(toRoot, DataDirectoryMigrator.ComputeDefaultRoot());
        StartMigrationTo(toRoot, toIsDefault);
    }

    /// <summary>恢复默认位置（C 盘 %APPDATA%）。已在默认位置则提示无需操作。</summary>
    private void OnResetDataDir(object sender, RoutedEventArgs e)
    {
        var def = DataDirectoryMigrator.ComputeDefaultRoot();
        if (!AppPaths.IsCustomRoot || PathEquals(def, AppPaths.Root))
        {
            MixCutDialog.Alert(this, "数据已在默认位置",
                "当前数据目录就是系统默认位置，不需要恢复。",
                icon: "📁");
            return;
        }
        StartMigrationTo(def, toIsDefault: true);
    }

    /// <summary>算数据量 + 校验目标盘空间 → 二次确认 → 登记待迁移 → 重启（迁移在启动最前面执行）。</summary>
    private void StartMigrationTo(string toRoot, bool toIsDefault)
    {
        var current = AppPaths.Root;

        // 要一并搬的模型目录：目标是自定义 → 放 toRoot\{sub}；目标是默认 → 放 %LOCALAPPDATA%\MixCut\{sub}（默认模型落点）。
        var pairs = new List<(string from, string to)>();
        long bytes = DataDirectoryMigrator.DirectorySize(current); // 已含「在 Root 之下」的模型（自定义模式）
        foreach (var (srcDir, sub) in new[]
                 {
                     (AppPaths.WhisperModelsDirectory, "whisper-models"),
                     (AppPaths.DemucsModelsDirectory, "demucs-models"),
                 })
        {
            if (!Directory.Exists(srcDir)) continue;
            var dst = toIsDefault
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixCut", sub)
                : Path.Combine(toRoot, sub);
            if (PathEquals(srcDir, dst)) continue;
            pairs.Add((srcDir, dst));
            // 模型若不在 current 之下（默认模式在 %LOCALAPPDATA%），单独计一份空间；在 Root 之下的已被上面算过。
            if (!DataDirectoryMigrator.IsUnder(srcDir, current))
                bytes += DataDirectoryMigrator.DirectorySize(srcDir);
        }

        var free = DataDirectoryMigrator.GetDriveFreeBytes(toRoot);
        if (free >= 0 && free < (long)(bytes * 1.05))
        {
            MixCutDialog.Error(this, "目标磁盘空间不足，无法迁移",
                $"这次迁移需要约 {FormatBytes(bytes)}，目标位置所在磁盘仅剩 {FormatBytes(free)}。\n\n" +
                "请先清理该盘的空间，或改选一个剩余空间更充足的磁盘。");
            return;
        }

        if (!MixCutDialog.Confirm(this,
                $"重启 MixCut 并迁移约 {FormatBytes(bytes)} 数据？",
                $"从：{current}\n到：{toRoot}\n\n" +
                "迁移会在重启后的启动阶段进行，期间请勿关机或断电。旧数据在迁移成功前不会删除。",
                confirmText: "重启并迁移", cancelText: "暂不迁移",
                destructive: true, icon: "💾"))
        {
            return;
        }

        DataDirectoryMigrator.RequestMigration(current, toRoot, toIsDefault, pairs);

        // 重启：迁移在新进程 OnStartup 最前面执行。
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Settings] 迁移后自动重启失败");
            MixCutDialog.Error(this, "自动重启失败，请手动重新打开 MixCut",
                MixCut.ViewModels.ExceptionTranslator.ToUserMessage(ex) + "\n\n" +
                "迁移任务已登记，下次手动启动 MixCut 时会自动继续，数据不会丢失。");
        }
        Application.Current.Shutdown();
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static string FormatBytes(long bytes)
    {
        double b = bytes;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }

    // ---- Agent tab（issue #23 内嵌 MCP server）----

    /// <summary>
    /// 各 Agent 客户端的注册片段（对齐 macOS AgentClientSnippet）。MCP 没有一条通用注册命令，
    /// 但核心信息只有服务地址一个；JSON（mcpServers 格式）是事实标准。
    /// </summary>
    private sealed record AgentClientSnippet(string Name, Func<string, string> Snippet, string Hint);

    private static readonly AgentClientSnippet[] AgentClients =
    {
        new("服务地址（通用）",
            url => url,
            "支持 MCP（Streamable HTTP）的客户端只需要这个地址；不懂 MCP 的 Agent 也可以直接对它 POST JSON-RPC。"),
        new("Claude Code",
            url => $"claude mcp add --transport http mixcut {url}",
            "在终端里执行这条命令即可。"),
        new("Cursor / Cline / Claude Desktop（JSON）",
            url => "{\n  \"mcpServers\": {\n    \"mixcut\": { \"url\": \"" + url + "\" }\n  }\n}",
            "合并进对应配置：Cursor 是项目下 .cursor/mcp.json；Cline 在扩展的 MCP 设置里；Claude Desktop 在 设置→连接器。"),
        new("Codex CLI / 桌面版",
            url => "[mcp_servers.mixcut]\ncommand = \"npx\"\nargs = [\"-y\", \"mcp-remote\", \"" + url + "\"]",
            "追加到 ~/.codex/config.toml，CLI 和桌面版共用这份配置（桌面版改完重启 app 生效）。Codex 走 stdio，用 mcp-remote 桥接，需要 Node.js。"),
        new("WorkBuddy（腾讯）",
            url => "{\n  \"mcpServers\": {\n    \"mixcut\": { \"url\": \"" + url + "\", \"transport\": \"http\" }\n  }\n}",
            "在 WorkBuddy 的 MCP 设置（mcp.json 或可视化 MCP 配置界面）里添加。"),
        new("Gemini CLI",
            url => "{\n  \"mcpServers\": {\n    \"mixcut\": { \"httpUrl\": \"" + url + "\" }\n  }\n}",
            "合并进 ~/.gemini/settings.json。"),
        new("VS Code Copilot",
            url => "{\n  \"servers\": {\n    \"mixcut\": { \"type\": \"http\", \"url\": \"" + url + "\" }\n  }\n}",
            "写入项目下 .vscode/mcp.json。"),
    };

    /// <summary>注册后的验证指令：用户复制给 Agent 执行，能返回项目清单即代表链路通了。</summary>
    private const string AgentVerifyPrompt =
        "请调用 mixcut 的 list_projects 工具，列出我的所有项目，每行输出：项目名｜状态｜视频数｜分镜数。";

    private void BuildAgentTab()
    {
        AgentEnabledCheck.IsChecked = _settings.AgentServerEnabled;
        AgentPortBox.Text = _settings.AgentServerPort.ToString();
        AgentPortBox.IsEnabled = _settings.AgentServerEnabled;
        AgentVerifyBox.Text = AgentVerifyPrompt;

        foreach (var client in AgentClients)
        {
            AgentClientCombo.Items.Add(client.Name);
        }
        AgentClientCombo.SelectedIndex = 1;   // 默认 Claude Code（对齐 macOS）

        // 能力清单：从工具目录（权威附件）动态渲染，加工具后自动同步，不手写死清单
        var tools = Services.Agent.AgentToolCatalog.All;
        AgentToolsHeader.Text = $"Agent 能调用的功能（{tools.Count} 个）";
        AgentToolsPanel.Children.Clear();
        foreach (var tool in tools)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            item.Children.Add(new TextBlock
            {
                Text = tool.Name,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                Foreground = (Brush)new BrushConverter().ConvertFromString("#333")!,
            });
            item.Children.Add(new TextBlock
            {
                Text = tool.Description,
                FontSize = 11,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#888")!,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
            AgentToolsPanel.Children.Add(item);
        }

        _agentLoaded = true;
        RefreshAgentSnippet();
    }

    private void RefreshAgentSnippet()
    {
        if (AgentClientCombo.SelectedIndex < 0)
        {
            return;
        }
        var client = AgentClients[AgentClientCombo.SelectedIndex];
        var url = Services.Agent.AgentGateway.EndpointUrl(_settings.AgentServerPort);
        AgentSnippetBox.Text = client.Snippet(url);
        AgentClientHint.Text = client.Hint;
    }

    private void OnAgentEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_agentLoaded)
        {
            return;
        }
        _settings.AgentServerEnabled = AgentEnabledCheck.IsChecked == true;
        AgentPortBox.IsEnabled = _settings.AgentServerEnabled;
        _agentGateway.RestartFromSettings();
    }

    private void OnAgentPortKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }
        if (int.TryParse(AgentPortBox.Text.Trim(), out var port) && port is > 0 and <= 65535)
        {
            _settings.AgentServerPort = port;
            _agentGateway.RestartFromSettings();
            RefreshAgentSnippet();
            Components.ToastService.Show($"Agent 服务已切到端口 {port}", Components.ToastStyle.Success);
        }
        else
        {
            Components.ToastService.Show("端口需要是 1~65535 的数字", Components.ToastStyle.Warning);
            AgentPortBox.Text = _settings.AgentServerPort.ToString();
        }
    }

    private void OnAgentPortLostFocus(object sender, RoutedEventArgs e)
    {
        // 未按回车离开输入框：还原为当前生效值（改端口需回车确认，避免误触）
        AgentPortBox.Text = _settings.AgentServerPort.ToString();
    }

    private void OnAgentClientChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_agentLoaded)
        {
            RefreshAgentSnippet();
        }
    }

    private void OnCopyAgentSnippet(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AgentSnippetBox.Text);
            Components.ToastService.Show("已复制注册配置", Components.ToastStyle.Success);
        }
        catch (Exception ex)
        {
            Components.ToastService.Show("复制失败：剪贴板被其它程序占用，请重试", Components.ToastStyle.Warning);
            Serilog.Log.Warning(ex, "[Agent] 复制注册片段失败");
        }
    }

    private void OnCopyAgentVerify(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AgentVerifyPrompt);
            Components.ToastService.Show("已复制验证指令", Components.ToastStyle.Success);
        }
        catch (Exception ex)
        {
            Components.ToastService.Show("复制失败：剪贴板被其它程序占用，请重试", Components.ToastStyle.Warning);
            Serilog.Log.Warning(ex, "[Agent] 复制验证指令失败");
        }
    }
}
