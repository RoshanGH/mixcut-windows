using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MixCut.Services.AI;

namespace MixCut.Utilities;

/// <summary>
/// 应用设置存储。对应 macOS 版 KeychainHelper（用 UserDefaults 存 API Key）。
/// Windows 版以 JSON 文件持久化到 <c>%APPDATA%\MixCut\settings.json</c>。
/// QW-13：API Key 用 Windows DPAPI（当前用户作用域）加密存储，不再明文落盘 ——
/// 防止本地恶意程序（普通用户权限）直接读 settings.json 盗刷 key。老明文数据启动时自动迁移加密。
/// 注册为单例服务。
/// </summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.Root, "settings.json");
    private static readonly object Gate = new();

    /// <summary>DPAPI 密文前缀标记，用于区分「已加密」与「老明文」值（QW-13 迁移用）。</summary>
    private const string EncPrefix = "dpapi:";

    private Dictionary<string, string> _values = new();

    public AppSettings()
    {
        Load();
        MigratePlaintextApiKeys();
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
        catch (Exception ex)
        {
            // QW-15/P0-26：settings.json 损坏（写一半断电 / 磁盘坏块 / 被杀软改）时，绝不能空 catch 吞掉 ——
            // 否则下一次 Save 会用空 json 覆盖损坏文件，用户所有配置（含 API Key / 模型选择 / 全部偏好）
            // 永久丢失。先把损坏文件备份到 settings.json.broken-{时间戳}，再用默认配置启动，给用户留挽回余地。
            _values = new();
            TryBackupBrokenSettings(ex);
        }
    }

    /// <summary>把损坏的 settings.json 备份到 .broken-{时间戳}，避免被默认配置覆盖后无法挽回（QW-15/P0-26）。</summary>
    private static void TryBackupBrokenSettings(Exception cause)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }
            var backup = $"{FilePath}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(FilePath, backup, overwrite: true);
            Serilog.Log.Error(cause, "[AppSettings] settings.json 损坏，已备份到 {Backup}，本次用默认配置启动", backup);
        }
        catch (Exception backupEx)
        {
            Serilog.Log.Warning(backupEx, "[AppSettings] 备份损坏的 settings.json 失败");
        }
    }

    // ---- DPAPI 加密（QW-13）----

    /// <summary>用 Windows DPAPI（当前用户作用域）加密敏感值，带 dpapi: 前缀。加密失败兜底存明文，避免 key 丢。</summary>
    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return plain;
        }
        try
        {
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return EncPrefix + Convert.ToBase64String(enc);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[AppSettings] API Key 加密失败，回退明文存储");
            return plain;
        }
    }

    /// <summary>解密 DPAPI 值；无前缀的视为老明文（迁移兼容）原样返回；解密失败（换 Windows 用户/机器）返回空。</summary>
    private static string Unprotect(string stored)
    {
        if (!stored.StartsWith(EncPrefix, StringComparison.Ordinal))
        {
            return stored; // 老明文 —— 迁移前数据，原样返回（GetApiKey 仍能用）
        }
        try
        {
            var enc = Convert.FromBase64String(stored[EncPrefix.Length..]);
            var bytes = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[AppSettings] API Key 解密失败（可能换了 Windows 用户/机器），需重新填写");
            return string.Empty;
        }
    }

    /// <summary>启动期把历史明文 API Key 一次性重新用 DPAPI 加密（QW-13 迁移），消除磁盘上的明文 key。</summary>
    private void MigratePlaintextApiKeys()
    {
        try
        {
            var plaintext = _values
                .Where(kv => kv.Key.StartsWith("api_key_", StringComparison.Ordinal)
                          && !string.IsNullOrEmpty(kv.Value)
                          && !kv.Value.StartsWith(EncPrefix, StringComparison.Ordinal))
                .ToList();
            if (plaintext.Count == 0)
            {
                return;
            }
            foreach (var kv in plaintext)
            {
                _values[kv.Key] = Protect(kv.Value);
            }
            Save();
            Serilog.Log.Information("[AppSettings] 已加密迁移 {Count} 个明文 API Key（DPAPI）", plaintext.Count);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[AppSettings] 明文 API Key 加密迁移失败，忽略");
        }
    }

    private void Save()
    {
        lock (Gate)
        {
            var json = JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
            // QW-18：原子写。原来直接 WriteAllText(FilePath) 若中途断电 / 杀进程会把 settings.json
            // 截断成无效 JSON，下次启动反序列化失败 → 全部设置丢。改「先写 .tmp 再原子 rename」：
            // 崩在写 .tmp 阶段时，原 settings.json 完好无损。
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
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

    // ---- API Key（QW-13：DPAPI 加密存储）----

    public void SaveApiKey(string key, AIProviderType provider) =>
        Set("api_key_" + provider.Key(), Protect(key));

    public string? GetApiKey(AIProviderType provider)
    {
        var stored = Get("api_key_" + provider.Key());
        if (stored is null)
        {
            return null;
        }
        var plain = Unprotect(stored);
        return string.IsNullOrEmpty(plain) ? null : plain;
    }

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

    /// <summary>方案筛选导出（ExportView）上次选择的输出目录（QW-3：跨会话记忆，与批量导出对话框各自独立）。</summary>
    public string LastExportDirForSchemes
    {
        get => Get("last_export_dir_schemes") ?? string.Empty;
        set => Set("last_export_dir_schemes", string.IsNullOrEmpty(value) ? null : value);
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

    /// <summary>
    /// 配音台词变体数（v0.5.0 配音）：每个非「保留原声」分镜要产出几套改写版。
    /// 默认 2，夹紧 1~5。对齐 macOS DubbingViewModel.variantCount（UserDefaults["dubVariantCount"]）。
    /// </summary>
    public int DubVariantCount
    {
        get => int.TryParse(Get("dub_variant_count"), out var n) ? Math.Clamp(n, 1, 5) : 2;
        set => Set("dub_variant_count", Math.Clamp(value, 1, 5).ToString());
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

    /// <summary>是否已把旧分镜的「秒」边界回填为帧号（issue #7 帧精确重构对齐迁移）。</summary>
    public bool DidBackfillSegmentFramesV1
    {
        get => string.Equals(Get("did_backfill_segment_frames_v1"), "true", StringComparison.OrdinalIgnoreCase);
        set => Set("did_backfill_segment_frames_v1", value ? "true" : null);
    }

    /// <summary>用户已点 ✕ 屏蔽提示的新版本号（如 "0.6.0"）。下次启动若远端版本相同则不再 banner。</summary>
    public string? DismissedUpdateVersion
    {
        get => Get("dismissed_update_version");
        set => Set("dismissed_update_version", string.IsNullOrEmpty(value) ? null : value);
    }
}
