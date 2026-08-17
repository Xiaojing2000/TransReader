using System.Text.Json;
using TransReader.Core.Storage;
using TransReader.Core.Translation;
using Windows.Security.Credentials;

namespace TransReader.App.Services;

internal sealed class TranslationSettingsStore
{
    private const string ModelKeyResource = "TransReader.ModelKey";
    private const int CurrentVersion = 4;

    // 旧版单 profile 凭据（仅用于一次性迁移）。
    private const string LegacyCredentialResource = "TransReader.TranslationApi";
    private const string LegacyCredentialUser = "api-key";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public TranslationSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    /// <summary>
    /// 加载活动模型的 profile。会自动从旧版单 profile 格式与旧凭据迁移。
    /// API Key 只从 Windows 凭据库读取，源码和设置文件均不保存密钥。
    /// 仅迁移/规范化真正改变了内容时才回写（启动不再无条件覆盖写）。
    /// </summary>
    public async Task<TranslationProfile> LoadAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, migratedFromLegacy) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored, out var modelsChanged);

            // 旧版凭据迁移：TransReader.TranslationApi/api-key -> TransReader.ModelKey/mimo
            if (migratedFromLegacy)
            {
                MigrateLegacyApiKeyToMimo();
            }

            // mimo-v2.5-pro -> mimo-v2.5（在新格式内对 mimo 项执行）
            var renamed = MigrateMimoProModelName(stored);
            if (migratedFromLegacy || modelsChanged || renamed)
            {
                await WriteStoredAsync(stored);
            }

            var activeId = ResolveActiveId(stored);
            var custom = (stored.CustomProviders ?? []).FirstOrDefault(p => p.Id == activeId);
            if (custom is not null)
            {
                var customStatus = TryReadApiKey(custom.Id, out var customKey);
                return BuildCustomProfile(custom, customKey ?? string.Empty, customStatus != ApiKeyReadStatus.StoreUnavailable);
            }
            var model = stored.Models.First(m => m.Id == activeId);
            var status = TryReadApiKey(model.Id, out var apiKey);
            return BuildProfile(model, apiKey ?? string.Empty, status != ApiKeyReadStatus.StoreUnavailable);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<TranslationExecutionMode> LoadExecutionModeAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return stored.TranslationMode;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveExecutionModeAsync(TranslationExecutionMode mode)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored) with { TranslationMode = mode };
            await WriteStoredAsync(stored);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>是否开启"空闲时预翻译下一页"（仅在线模式生效；默认关闭以尊重 API 计费）。</summary>
    public async Task<bool> LoadPrefetchTranslationAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return stored.PrefetchTranslation;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SavePrefetchTranslationAsync(bool enabled)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored) with { PrefetchTranslation = enabled };
            await WriteStoredAsync(stored);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>返回全部预设各自的活动 profile（用于设置对话框多模型切换展示）。</summary>
    public async Task<IReadOnlyList<TranslationProfile>> LoadAllAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored);
            // 不写回：LoadAllAsync 是只读展示用途，写回交给 Save* 调用。
            var result = new List<TranslationProfile>(TranslationModelPresets.Defaults.Count);
            foreach (var preset in TranslationModelPresets.Defaults)
            {
                var model = stored.Models.FirstOrDefault(m => m.Id == preset.Id) ?? ToStored(preset);
                var status = TryReadApiKey(preset.Id, out var apiKey);
                result.Add(BuildProfile(model, apiKey ?? string.Empty, status != ApiKeyReadStatus.StoreUnavailable));
            }
            return result;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>保存活动模型（写 settings + key，并把 ActiveModelId 设为该模型）。</summary>
    public async Task SaveAsync(TranslationProfile profile)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored);
            stored = stored with { ActiveModelId = profile.Id };
            UpsertModel(stored, profile.Id, profile.Settings);
            await WriteStoredAsync(stored);
            WriteApiKey(profile.Id, profile.ApiKey);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>保存单个模型的设置与 key（不改变 ActiveModelId）。</summary>
    public async Task SaveModelAsync(string id, TranslationSettings settings, string apiKey)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored);
            UpsertModel(stored, id, settings);
            await WriteStoredAsync(stored);
            WriteApiKey(id, apiKey);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>全部自定义在线端点（含 Key 与凭据可用性）。</summary>
    public async Task<IReadOnlyList<TranslationProfile>> LoadCustomProvidersAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return (stored.CustomProviders ?? [])
                .Select(provider =>
                {
                    var status = TryReadApiKey(provider.Id, out var apiKey);
                    return BuildCustomProfile(provider, apiKey ?? string.Empty, status != ApiKeyReadStatus.StoreUnavailable);
                })
                .ToList();
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>新增或更新自定义端点（按 Id upsert），Key 写凭据库。</summary>
    public async Task SaveCustomProviderAsync(StoredCustomProvider provider, string apiKey)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored);
            var customs = (stored.CustomProviders ?? []).ToList();
            var index = customs.FindIndex(p => p.Id == provider.Id);
            if (index >= 0) customs[index] = provider; else customs.Add(provider);
            await WriteStoredAsync(stored with { CustomProviders = customs });
            WriteApiKey(provider.Id, apiKey);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>删除自定义端点并清除其 Key；若它是活动模型则回落到 mimo。</summary>
    public async Task DeleteCustomProviderAsync(string id)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored);
            var customs = (stored.CustomProviders ?? []).Where(p => p.Id != id).ToList();
            var activeId = string.Equals(stored.ActiveModelId, id, StringComparison.Ordinal)
                ? "mimo"
                : stored.ActiveModelId;
            await WriteStoredAsync(stored with { CustomProviders = customs, ActiveModelId = activeId });
            WriteApiKey(id, null);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>设置活动模型（预设或自定义端点 Id）。</summary>
    public async Task SetActiveModelAsync(string id)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored) with { ActiveModelId = id };
            await WriteStoredAsync(stored);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>按 Id 读取 profile（预设或自定义端点；不存在返回 null）。问答钉选与兜底用。</summary>
    public async Task<TranslationProfile?> LoadProfileByIdAsync(string id)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored);
            var custom = (stored.CustomProviders ?? []).FirstOrDefault(p => p.Id == id);
            if (custom is not null)
            {
                var customStatus = TryReadApiKey(custom.Id, out var customKey);
                return BuildCustomProfile(custom, customKey ?? string.Empty, customStatus != ApiKeyReadStatus.StoreUnavailable);
            }
            var model = stored.Models.FirstOrDefault(m => m.Id == id);
            if (model is null) return null;
            var status = TryReadApiKey(model.Id, out var apiKey);
            return BuildProfile(model, apiKey ?? string.Empty, status != ApiKeyReadStatus.StoreUnavailable);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>问答模型来源："follow"（跟随翻译模式）/ "local"（固定本地）/ 在线 provider Id。</summary>
    public async Task<string> LoadAssistantModelSourceAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return stored.AssistantModelSource;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveAssistantModelSourceAsync(string source)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored) with { AssistantModelSource = source };
            await WriteStoredAsync(stored);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>文献库自动分析开关（手动分析不受此限）。</summary>
    public async Task<bool> LoadLibraryAutoAnalysisEnabledAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return stored.LibraryAutoAnalysisEnabled;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveLibraryAutoAnalysisEnabledAsync(bool enabled)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored) with { LibraryAutoAnalysisEnabled = enabled };
            await WriteStoredAsync(stored);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>文献库整理模型来源："local"（本地 Qwen3）/ "follow"（跟随当前活动在线模型）/ 在线 provider Id。</summary>
    public async Task<string> LoadLibraryAnalysisSourceAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return stored.LibraryAnalysisSource;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveLibraryAnalysisSourceAsync(string source)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored) with { LibraryAnalysisSource = source };
            await WriteStoredAsync(stored);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>翻译类型偏好："auto"（跟随 AI 文献分析）/ "general" / "math" / "computer_science" / 其他领域键。</summary>
    public async Task<string> LoadTranslationDomainPreferenceAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return stored.TranslationDomainPreference;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveTranslationDomainPreferenceAsync(string preference)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored) with { TranslationDomainPreference = preference };
            await WriteStoredAsync(stored);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>用户自定义领域提示词覆盖（键=领域键；返回副本，无覆盖时为 null）。</summary>
    public async Task<Dictionary<string, string>?> LoadDomainPromptHintsAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return stored.TranslationDomainHints is null
                ? null
                : new Dictionary<string, string>(stored.TranslationDomainHints, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveDomainPromptHintsAsync(Dictionary<string, string>? hints)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored) with { TranslationDomainHints = hints };
            await WriteStoredAsync(stored);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>在线翻译瞬时故障时自动改用本地模型兜底（需已安装本地模型）。</summary>
    public async Task<bool> LoadLocalFallbackEnabledAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return stored.LocalFallbackEnabled;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveLocalFallbackEnabledAsync(bool enabled)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored) with { LocalFallbackEnabled = enabled };
            await WriteStoredAsync(stored);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// 读取 API Key，找不到或凭据库不可用时返回空串（兼容旧行为）。
    /// 需要区分"未配置"与"凭据库故障"时请改用 <see cref="TryReadApiKey"/>。
    /// </summary>
    public string ReadApiKey(string modelId) =>
        TryReadApiKey(modelId, out var apiKey) == ApiKeyReadStatus.Found
            ? apiKey!
            : string.Empty;

    /// <summary>
    /// 读取 API Key 并区分三种状态：找到 / 未配置 / 凭据库不可用。
    /// </summary>
    public ApiKeyReadStatus TryReadApiKey(string modelId, out string? apiKey)
    {
        apiKey = null;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return ApiKeyReadStatus.NotConfigured;
        }
        try
        {
            var credential = new PasswordVault().Retrieve(ModelKeyResource, modelId);
            credential.RetrievePassword();
            apiKey = credential.Password ?? string.Empty;
            return ApiKeyReadStatus.Found;
        }
        catch (Exception ex) when (IsCredentialNotFound(ex))
        {
            return ApiKeyReadStatus.NotConfigured;
        }
        catch (Exception ex)
        {
            AppLog.Error($"读取 API Key ({modelId})", ex);
            return ApiKeyReadStatus.StoreUnavailable;
        }
    }

    /// <summary>
    /// 写入 API Key（空值表示删除）。先暂存旧凭据，写入失败时尽力回滚，避免保存失败即丢 key。
    /// 凭据库本身故障时抛异常，由调用方提示保存失败。
    /// </summary>
    public void WriteApiKey(string modelId, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }
        var vault = new PasswordVault();

        // 先 Retrieve 旧凭据并暂存密码，供写入失败时回滚（Remove+Add 不是原子操作）。
        string? previousPassword = null;
        try
        {
            var existing = vault.Retrieve(ModelKeyResource, modelId);
            existing.RetrievePassword();
            previousPassword = existing.Password;
            vault.Remove(existing);
        }
        catch (Exception ex) when (IsCredentialNotFound(ex))
        {
            // 旧凭据不存在：正常路径，无需处理。
        }
        catch (Exception ex)
        {
            // 凭据库故障：旧 key 状态未知，放弃写入以免误删。
            AppLog.Error($"删除旧 API Key ({modelId})", ex);
            throw;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                vault.Add(new PasswordCredential(
                    ModelKeyResource,
                    modelId,
                    apiKey.Trim()));
            }
            catch (Exception ex)
            {
                AppLog.Error($"写入 API Key ({modelId})", ex);
                // 尽力回滚旧凭据。
                if (!string.IsNullOrEmpty(previousPassword))
                {
                    try
                    {
                        vault.Add(new PasswordCredential(ModelKeyResource, modelId, previousPassword));
                    }
                    catch (Exception rollbackEx)
                    {
                        AppLog.Error($"回滚 API Key ({modelId})", rollbackEx);
                    }
                }
                throw;
            }
        }
    }

    // PasswordVault 在凭据不存在时抛出 HRESULT 0x80070490 (ERROR_NOT_FOUND)。
    private static bool IsCredentialNotFound(Exception ex) =>
        ex.HResult == unchecked((int)0x80070490);

    // ---------- 内部存储类型 ----------

    internal sealed record StoredSettings(
        string? ActiveModelId,
        int Version,
        List<StoredModel> Models,
        TranslationExecutionMode TranslationMode = TranslationExecutionMode.Online,
        bool PrefetchTranslation = false,
        List<StoredCustomProvider>? CustomProviders = null,
        string AssistantModelSource = "follow",
        bool LibraryAutoAnalysisEnabled = true,
        string LibraryAnalysisSource = "local",
        bool LocalFallbackEnabled = false,
        string TranslationDomainPreference = "auto",
        Dictionary<string, string>? TranslationDomainHints = null);

    internal sealed record StoredModel(
        string Id,
        string DisplayName,
        string BaseUrl,
        string Model,
        string AuthenticationMode,
        bool IsMultimodal,
        string TargetLanguage);

    /// <summary>自定义在线端点（预设之外的任意 OpenAI 兼容服务）。Id 形如 "custom-{guid:N}"，Key 按 Id 存凭据库。</summary>
    internal sealed record StoredCustomProvider(
        string Id,
        string DisplayName,
        string BaseUrl,
        string Model,
        string AuthenticationMode,
        bool IsMultimodal,
        double Temperature,
        string TargetLanguage);

    // ---------- 加载 / 写入 ----------

    private async Task<(StoredSettings Stored, bool MigratedFromLegacy)> LoadStoredAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return (FreshDefaults(), MigratedFromLegacy: false);
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(_settingsPath);
        }
        catch (IOException)
        {
            return (FreshDefaults(), MigratedFromLegacy: false);
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var hasModels = root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("models", out var modelsProp) &&
                        modelsProp.ValueKind == JsonValueKind.Array;

        if (hasModels)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<StoredSettings>(text, SerializerOptions);
                if (parsed is { Models: { Count: > 0 } })
                {
                    return (parsed, MigratedFromLegacy: false);
                }
            }
            catch (JsonException)
            {
            }
            return (FreshDefaults(), MigratedFromLegacy: false);
        }

        // 旧版单 profile 格式：直接是一个 TranslationSettings 对象。
        return (MigrateFromLegacy(text), MigratedFromLegacy: true);
    }

    private static StoredSettings FreshDefaults()
    {
        var models = TranslationModelPresets.Defaults
            .Select(ToStored)
            .ToList();
        return new StoredSettings("mimo", CurrentVersion, models, TranslationExecutionMode.Online);
    }

    private static StoredSettings MigrateFromLegacy(string text)
    {
        TranslationSettings? legacy = null;
        try
        {
            legacy = JsonSerializer.Deserialize<TranslationSettings>(text, SerializerOptions);
        }
        catch (JsonException)
        {
        }
        legacy ??= TranslationSettings.MiMoDefault;

        // 旧格式里 mimo-v2.5-pro 也一并规整为 mimo-v2.5。
        if (legacy.BaseUrl.Contains("xiaomimimo.com", StringComparison.OrdinalIgnoreCase) &&
            legacy.Model.Equals("mimo-v2.5-pro", StringComparison.OrdinalIgnoreCase))
        {
            legacy = legacy with { Model = "mimo-v2.5" };
        }

        // 按预设匹配活动模型：默认 mimo，若 BaseUrl+Model 命中其他预设则用之。
        var activeId = "mimo";
        foreach (var preset in TranslationModelPresets.Defaults)
        {
            if (preset.BaseUrl.Equals(legacy.BaseUrl, StringComparison.OrdinalIgnoreCase) &&
                preset.Model.Equals(legacy.Model, StringComparison.OrdinalIgnoreCase))
            {
                activeId = preset.Id;
                break;
            }
        }

        var models = TranslationModelPresets.Defaults
            .Select(preset => preset.Id == activeId ? ToStored(preset, legacy) : ToStored(preset))
            .ToList();

        return new StoredSettings(activeId, CurrentVersion, models, TranslationExecutionMode.Online);
    }

    private static StoredSettings EnsureCompleteModels(StoredSettings stored) =>
        EnsureCompleteModels(stored, out _);

    private static StoredSettings EnsureCompleteModels(StoredSettings stored, out bool changed)
    {
        // 保证 4 个预设全部存在（升级到新格式后补齐缺失项）。
        // 去重以防损坏的文件里出现重复 Id（ToDictionary 否则会抛异常）。
        var models = stored.Models ?? [];
        var byId = models
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToDictionary(m => m.Id, m => m);
        var merged = new List<StoredModel>(TranslationModelPresets.Defaults.Count);
        foreach (var preset in TranslationModelPresets.Defaults)
        {
            if (byId.TryGetValue(preset.Id, out var existing))
            {
                // 保留用户已存储的值，但补齐缺失字段（旧文件可能缺 TargetLanguage/IsMultimodal）。
                merged.Add(new StoredModel(
                    existing.Id,
                    string.IsNullOrWhiteSpace(existing.DisplayName) ? preset.DisplayName : existing.DisplayName,
                    string.IsNullOrWhiteSpace(existing.BaseUrl) ? preset.BaseUrl : existing.BaseUrl,
                    string.IsNullOrWhiteSpace(existing.Model) ? preset.Model : existing.Model,
                    string.IsNullOrWhiteSpace(existing.AuthenticationMode) ? preset.AuthenticationMode : existing.AuthenticationMode,
                    existing.IsMultimodal,
                    string.IsNullOrWhiteSpace(existing.TargetLanguage) ? "简体中文" : existing.TargetLanguage));
            }
            else
            {
                merged.Add(ToStored(preset));
            }
        }
        var completed = stored with { Models = merged, Version = CurrentVersion };
        changed = stored.Version != CurrentVersion || !models.SequenceEqual(merged);
        return completed;
    }

    private static bool MigrateMimoProModelName(StoredSettings stored)
    {
        var mimo = stored.Models.FirstOrDefault(m => m.Id == "mimo");
        if (mimo is not null &&
            mimo.BaseUrl.Contains("xiaomimimo.com", StringComparison.OrdinalIgnoreCase) &&
            mimo.Model.Equals("mimo-v2.5-pro", StringComparison.OrdinalIgnoreCase))
        {
            var index = stored.Models.IndexOf(mimo);
            stored.Models[index] = mimo with { Model = "mimo-v2.5" };
            return true;
        }
        return false;
    }

    private void MigrateLegacyApiKeyToMimo()
    {
        string? legacyKey;
        try
        {
            var credential = new PasswordVault().Retrieve(LegacyCredentialResource, LegacyCredentialUser);
            credential.RetrievePassword();
            legacyKey = credential.Password;
        }
        catch
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(legacyKey))
        {
            return;
        }
        try
        {
            WriteApiKey("mimo", legacyKey);
        }
        catch (Exception ex)
        {
            // 迁移失败不应阻断启动：旧凭据仍在，下次启动会重试。
            AppLog.Error("迁移旧版 API Key", ex);
            return;
        }
        try
        {
            var vault = new PasswordVault();
            var existing = vault.Retrieve(LegacyCredentialResource, LegacyCredentialUser);
            vault.Remove(existing);
        }
        catch
        {
        }
    }

    private static void UpsertModel(StoredSettings stored, string id, TranslationSettings settings)
    {
        var preset = TranslationModelPresets.Find(id);
        var displayName = preset?.DisplayName
            ?? (string.IsNullOrWhiteSpace(settings.Model) ? id : settings.Model);
        var updated = new StoredModel(
            id,
            displayName,
            settings.BaseUrl,
            settings.Model,
            settings.AuthenticationMode,
            settings.IsMultimodal,
            settings.TargetLanguage);
        var index = stored.Models.FindIndex(m => m.Id == id);
        if (index >= 0)
        {
            stored.Models[index] = updated;
        }
        else
        {
            stored.Models.Add(updated);
        }
    }

    private static string ResolveActiveId(StoredSettings stored)
    {
        if (!string.IsNullOrWhiteSpace(stored.ActiveModelId) &&
            (stored.Models.Any(m => m.Id == stored.ActiveModelId) ||
             (stored.CustomProviders ?? []).Any(p => p.Id == stored.ActiveModelId)))
        {
            return stored.ActiveModelId!;
        }
        return "mimo";
    }

    private static TranslationProfile BuildProfile(StoredModel model, string apiKey, bool credentialStoreAvailable = true)
    {
        var preset = TranslationModelPresets.Find(model.Id);
        var settings = new TranslationSettings(
            model.BaseUrl,
            model.Model,
            string.IsNullOrWhiteSpace(model.TargetLanguage) ? "简体中文" : model.TargetLanguage,
            string.IsNullOrWhiteSpace(model.AuthenticationMode) ? "api-key" : model.AuthenticationMode,
            model.IsMultimodal,
            preset?.Temperature ?? 0.1,
            preset?.DisableThinking ?? true,
            // ProviderId 只作用量统计标签（不进缓存 key）；CacheIdentity 留空以维持既有缓存兼容。
            ProviderId: preset?.Id ?? model.Id);
        return new TranslationProfile(model.Id, settings, apiKey)
        {
            IsCredentialStoreAvailable = credentialStoreAvailable,
            StoredDisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName,
        };
    }

    private static TranslationProfile BuildCustomProfile(StoredCustomProvider provider, string apiKey, bool credentialStoreAvailable = true)
    {
        var settings = new TranslationSettings(
            provider.BaseUrl,
            provider.Model,
            string.IsNullOrWhiteSpace(provider.TargetLanguage) ? "简体中文" : provider.TargetLanguage,
            string.IsNullOrWhiteSpace(provider.AuthenticationMode) ? "api-key" : provider.AuthenticationMode,
            provider.IsMultimodal,
            provider.Temperature,
            DisableThinking: false,
            ProviderId: provider.Id);
        return new TranslationProfile(provider.Id, settings, apiKey)
        {
            IsCredentialStoreAvailable = credentialStoreAvailable,
            CustomDisplayName = provider.DisplayName
        };
    }

    private static StoredModel ToStored(TranslationModelPreset preset) => new(
        preset.Id,
        preset.DisplayName,
        preset.BaseUrl,
        preset.Model,
        preset.AuthenticationMode,
        preset.IsMultimodal,
        "简体中文");

    private static StoredModel ToStored(TranslationModelPreset preset, TranslationSettings overrideSettings) => new(
        preset.Id,
        preset.DisplayName,
        overrideSettings.BaseUrl,
        overrideSettings.Model,
        overrideSettings.AuthenticationMode,
        overrideSettings.IsMultimodal,
        string.IsNullOrWhiteSpace(overrideSettings.TargetLanguage) ? "简体中文" : overrideSettings.TargetLanguage);

    private async Task WriteStoredAsync(StoredSettings stored) =>
        await AtomicJsonFile.WriteAsync(_settingsPath, stored, SerializerOptions);
}

/// <summary>API Key 读取结果状态。</summary>
internal enum ApiKeyReadStatus
{
    /// <summary>凭据库中找到了 API Key。</summary>
    Found,
    /// <summary>凭据库可用，但该模型从未配置过 Key。</summary>
    NotConfigured,
    /// <summary>凭据库本身不可用（读故障），与"未配置"相区分。</summary>
    StoreUnavailable,
}

/// <summary>
/// 活动翻译 profile：携带模型 Id（对应预设或 custom）、设置与 API Key。
/// 调用链（PageProcessingService.GetTranslationAsync(pageIndex, profile, ...)）只读取
/// Settings 与 ApiKey，新增 Id 不影响既有行为。
/// </summary>
internal sealed record TranslationProfile(string Id, TranslationSettings Settings, string ApiKey)
{
    /// <summary>
    /// 读取本 profile 的 Key 时凭据库是否可用。为 false 时 ApiKey 为空不代表"未配置"，
    /// 调用方应提示凭据库故障而非引导重新配置。
    /// </summary>
    public bool IsCredentialStoreAvailable { get; init; } = true;

    /// <summary>自定义端点的显示名（预设 profile 不用设置，走预设 DisplayName）。</summary>
    public string? CustomDisplayName { get; init; }

    /// <summary>用户已存储的显示名：优先于预设默认名——预设只作首次安装默认值，改名不影响已有用户。</summary>
    public string? StoredDisplayName { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Settings.BaseUrl) &&
        !string.IsNullOrWhiteSpace(Settings.Model) &&
        (Settings.AuthenticationMode.Equals("none", StringComparison.OrdinalIgnoreCase) ||
         !string.IsNullOrWhiteSpace(ApiKey));

    public bool IsMultimodal => Settings.IsMultimodal;

    public string DisplayName =>
        CustomDisplayName
        ?? (string.IsNullOrWhiteSpace(StoredDisplayName) ? null : StoredDisplayName)
        ?? TranslationModelPresets.Find(Id)?.DisplayName
        ?? (string.IsNullOrWhiteSpace(Settings.Model) ? "未配置" : Settings.Model);
}
