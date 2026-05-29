using System.IO;
using System.Text.Json;
using MixCut.Services.AI;

namespace MixCut.Utilities;

/// <summary>
/// 应用设置存储。对应 macOS 版 KeychainHelper（用 UserDefaults 存 API Key）。
/// Windows 版以 JSON 文件持久化到 <c>%APPDATA%\MixCut\settings.json</c>。
/// 与 macOS 版一致，API Key 以明文存储（开发期简化，避免凭据弹窗）。
/// 注册为单例服务。
/// </summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.Root, "settings.json");
    private static readonly object Gate = new();

    private Dictionary<string, string> _values = new();

    public AppSettings()
    {
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _values = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch (Exception)
        {
            _values = new();
        }
    }

    private void Save()
    {
        lock (Gate)
        {
            var json = JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }

    private string? Get(string key) =>
        _values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }
        Save();
    }

    // ---- API Key ----

    public void SaveApiKey(string key, AIProviderType provider) => Set("api_key_" + provider.Key(), key);

    public string? GetApiKey(AIProviderType provider) => Get("api_key_" + provider.Key());

    public void RemoveApiKey(AIProviderType provider) => Set("api_key_" + provider.Key(), null);

    public bool HasApiKey(AIProviderType provider) => !string.IsNullOrEmpty(GetApiKey(provider));

    // ---- 活跃提供商 ----

    public AIProviderType ActiveProvider
    {
        get => AIProviderCatalog.ProviderFromKey(Get("active_ai_provider"));
        set => Set("active_ai_provider", value.Key());
    }

    // ---- 自定义提供商配置 ----

    public string CustomBaseUrl
    {
        get => Get("custom_base_url") ?? string.Empty;
        set => Set("custom_base_url", value);
    }

    public string CustomModelName
    {
        get => Get("custom_model_name") ?? string.Empty;
        set => Set("custom_model_name", value);
    }

    // ---- 国内转发网关 ----

    public string RelayBaseUrl
    {
        get => Get("relay_base_url") ?? string.Empty;
        set => Set("relay_base_url", value);
    }

    public RelayPlatform RelayPlatform
    {
        get => AIProviderCatalog.RelayFromKey(Get("relay_platform"));
        set => Set("relay_platform", value.Key());
    }

    // ---- 模型选择 ----

    public string SelectedModel(AIProviderType provider)
    {
        var stored = Get("selected_model_" + provider.Key());
        if (!string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        return provider switch
        {
            AIProviderType.ClaudeRelay => RelayPlatform.DefaultModel(),
            AIProviderType.Custom => string.IsNullOrEmpty(CustomModelName) ? "custom-model" : CustomModelName,
            _ => provider.StaticDefaultModel(),
        };
    }

    public void SetSelectedModel(string model, AIProviderType provider) =>
        Set("selected_model_" + provider.Key(), model);

    // ---- 首次启动引导 ----

    /// <summary>
    /// 是否已完成首次启动引导。对应 macOS 版 @AppStorage("hasCompletedOnboarding")。
    /// 首次安装后用户走完 4 步引导（或主动跳过）后置为 true。
    /// </summary>
    public bool HasCompletedOnboarding
    {
        get => string.Equals(Get("has_completed_onboarding"), "true", StringComparison.OrdinalIgnoreCase);
        set => Set("has_completed_onboarding", value ? "true" : null);
    }

    /// <summary>批量导出对话框上次选择的目录（对齐 macOS @AppStorage("lastBatchExportDir")）。</summary>
    public string LastBatchExportDirectory
    {
        get => Get("last_batch_export_dir") ?? string.Empty;
        set => Set("last_batch_export_dir", string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>上次选中的项目 ID（启动时自动恢复）。对齐 macOS @AppStorage("lastSelectedProjectId")。</summary>
    public Guid? LastSelectedProjectId
    {
        get => Guid.TryParse(Get("last_selected_project_id"), out var id) ? id : null;
        set => Set("last_selected_project_id", value?.ToString());
    }

    /// <summary>
    /// 启用 SegmentLibrary V2（MVVM 数据驱动版）。默认 true（灰度中）。
    /// 修复 BorderBrush UnsetValue 后再启用。如出现 XAML 解析报错可设 "false" 回退 V1。
    /// </summary>
    public bool UseNewSegmentLibrary
    {
        get => !string.Equals(Get("use_new_segment_library"), "false", StringComparison.OrdinalIgnoreCase);
        set => Set("use_new_segment_library", value ? null : "false");
    }

    /// <summary>上次离开应用时停留的 NavigationItem 索引（0-4），启动时恢复。</summary>
    public int LastNavItem
    {
        get => int.TryParse(Get("last_nav_item"), out var i) ? Math.Clamp(i, 0, 4) : 0;
        set => Set("last_nav_item", value.ToString());
    }

    // ---- 迁移持久化 flag（v0.6.0 对齐 Mac v0.3.x） ----

    /// <summary>是否已为老项目补建「自定义组合」策略（Mac v0.3.0 对齐迁移）。</summary>
    public bool DidEnsureCustomGroupStrategyV1
    {
        get => string.Equals(Get("did_ensure_custom_group_strategy_v1"), "true", StringComparison.OrdinalIgnoreCase);
        set => Set("did_ensure_custom_group_strategy_v1", value ? "true" : null);
    }

    /// <summary>是否已把分镜缩略图迁移到「首帧」（Mac v0.3.1 对齐迁移）。</summary>
    public bool DidRegenerateSegmentThumbnailsToFirstFrameV1
    {
        get => string.Equals(Get("did_regenerate_segment_thumbnails_to_first_frame_v1"), "true", StringComparison.OrdinalIgnoreCase);
        set => Set("did_regenerate_segment_thumbnails_to_first_frame_v1", value ? "true" : null);
    }

    /// <summary>用户已点 ✕ 屏蔽提示的新版本号（如 "0.6.0"）。下次启动若远端版本相同则不再 banner。</summary>
    public string? DismissedUpdateVersion
    {
        get => Get("dismissed_update_version");
        set => Set("dismissed_update_version", string.IsNullOrEmpty(value) ? null : value);
    }
}
