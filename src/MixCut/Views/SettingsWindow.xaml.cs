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

namespace MixCut.Views;

/// <summary>设置窗口（AI 提供商 + 通用配置）。对应 macOS 版 SettingsView。</summary>
public partial class SettingsWindow : Window
{
    private const string MaskedKey = "••••••••••••••••••••";

    private readonly AppSettings _settings;
    private readonly ASRService _asrService;
    private bool _loaded;
    private bool _apiKeyHidden = true;
    private string? _actualKey;

    public SettingsWindow(AppSettings settings, ASRService asrService)
    {
        _settings = settings;
        _asrService = asrService;
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
                MessageBox.Show("请输入 API Key", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // 用户没改 key，但可能改了别的字段 → 保留原 key
            typed = _actualKey;
        }
        if (provider == AIProviderType.Custom
            && (string.IsNullOrWhiteSpace(CustomUrlBox.Text) || string.IsNullOrWhiteSpace(CustomModelBox.Text)))
        {
            MessageBox.Show("自定义提供商需要同时填写 API 地址和模型名称",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (provider == AIProviderType.ClaudeRelay && string.IsNullOrWhiteSpace(RelayUrlBox.Text))
        {
            MessageBox.Show("转发网关需要填写网关地址", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var confirm = MessageBox.Show(
            $"确定要清除 {provider.DisplayName()} 的 API Key 吗？",
            "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
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

        AddDependencyRow(0, "FFmpeg", BundledBinaries.FfmpegAvailable);
        AddDependencyRow(1, "ffprobe", BundledBinaries.FfprobeAvailable);
        AddDependencyRow(2, "whisper-cli", BundledBinaries.WhisperAvailable);

        var modelPath = FindWhisperModel();
        AddDependencyRow(3, "语音模型", modelPath is not null,
            modelPath is null ? "未下载" : "已就绪");

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

        // 系统信息。
        var cores = Environment.ProcessorCount;
        var maxAnalyze = Math.Max(1, cores / 2);
        var maxExport = Math.Max(1, Math.Min(8, (cores - 2) / 2));
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

        SystemInfoPanel.Children.Clear();
        AddInfoRow(SystemInfoPanel, "CPU 核心数", $"{cores} 核");
        AddInfoRow(SystemInfoPanel, "同时分析视频数", $"最多 {maxAnalyze} 个");
        AddInfoRow(SystemInfoPanel, "同时导出视频数", $"最多 {maxExport} 个");
        AddInfoRow(SystemInfoPanel, "版本", version);
        AddInfoRow(SystemInfoPanel, "操作系统", Environment.OSVersion.VersionString);

        // 关于。
        AboutPanel.Children.Clear();
        AddOnboardingResetRow(AboutPanel);
        AddInfoRow(AboutPanel, "开发者", "MengGang");
        AddInfoRow(AboutPanel, "联系方式", "13462890087");
        AddLinkRow(AboutPanel, "GitHub", "RoshanGH/mixed_cut", "https://github.com/RoshanGH/mixed_cut");
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
            MessageBox.Show("已重置使用引导。下次启动 MixCut 时会再次显示。",
                "MixCut", MessageBoxButton.OK, MessageBoxImage.Information);
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

        var statusText = statusOverride ?? (available ? "✓ 已安装" : "✗ 未找到");
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
            ModelErrorText.Text = "下载失败：" + ex.Message;
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
            MessageBox.Show("打开数据目录失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
