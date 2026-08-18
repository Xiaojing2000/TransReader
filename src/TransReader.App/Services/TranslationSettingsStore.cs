using System.Text.Json;
using TransReader.Core.Storage;
using TransReader.Core.Translation;
using Windows.Security.Credentials;

namespace TransReader.App.Services;

internal sealed class TranslationSettingsStore
{
    // v2 intentionally starts with an empty credential namespace. Development
    // builds and the old 0.3.0 installer shared the v1 resource, which could
    // make a newly installed app appear to contain a bundled API key.
    private const string ModelKeyResource = "TransReader.ModelKey.v2";
    private const int CurrentVersion = 5;

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
    /// 加载活动模型的 profile。旧设置文件只迁移公开配置，不迁移或回显旧 API Key。
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

            // mimo-v2.5-pro -> mimo-v2.5（在新格式内对 mimo 项执行）
            var renamed = MigrateMimoProModelName(stored);
            if (migratedFromLegacy || modelsChanged || renamed)
            {
                await WriteStoredAsync(stored);
            }

            var activeId = ResolveActiveId(stored);
            if (activeId is null)
            {
                return TranslationProfile.Unconfigured;
            }
            var custom = (stored.CustomProviders ?? []).FirstOrDefault(p => p.Id == activeId);
            if (custom is not null)
            {
                var customStatus = TryReadApiKey(custom.Id, out var customKey);
                var profile = BuildCustomProfile(custom, customKey ?? string.Empty, customStatus != ApiKeyReadStatus.StoreUnavailable);
                return customStatus == ApiKeyReadStatus.StoreUnavailable || profile.IsConfigured
                    ? profile
                    : TranslationProfile.Unconfigured;
            }
            var model = stored.Models.FirstOrDefault(m => m.Id == activeId);
            if (model is null)
            {
                return TranslationProfile.Unconfigured;
            }
            var status = TryReadApiKey(model.Id, out var apiKey);
            var presetProfile = BuildProfile(model, apiKey ?? string.Empty, status != ApiKeyReadStatus.StoreUnavailable);
            return status == ApiKeyReadStatus.StoreUnavailable || presetProfile.IsConfigured
                ? presetProfile
                : TranslationProfile.Unconfigured;
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

    /// <summary>返回用户已经完成配置的预设模型。内置预设只是新建模板，不算已配置模型。</summary>
    public async Task<IReadOnlyList<TranslationProfile>> LoadAllAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored);
            // 不写回：LoadAllAsync 是只读展示用途，写回交给 Save* 调用。
            var result = new List<TranslationProfile>(stored.Models.Count);
            foreach (var model in stored.Models)
            {
                var status = TryReadApiKey(model.Id, out var apiKey);
                var profile = BuildProfile(model, apiKey ?? string.Empty, status != ApiKeyReadStatus.StoreUnavailable);
                if (profile.IsConfigured)
                {
                    result.Add(profile);
                }
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
    public async Task SaveModelAsync(
        string id,
        TranslationSettings settings,
        string apiKey,
        string? displayName = null)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored);
            UpsertModel(stored, id, settings, displayName);
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

    /// <summary>删除一个已配置模型（预设或自定义）并清除其凭据。</summary>
    public async Task DeleteProviderAsync(string id)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            stored = EnsureCompleteModels(stored);
            var models = stored.Models.Where(model => model.Id != id).ToList();
            var customs = (stored.CustomProviders ?? []).Where(provider => provider.Id != id).ToList();
            var remainingIds = models.Select(model => model.Id).Concat(customs.Select(provider => provider.Id)).ToList();
            var activeId = string.Equals(stored.ActiveModelId, id, StringComparison.Ordinal)
                ? remainingIds.FirstOrDefault()
                : stored.ActiveModelId;
            await WriteStoredAsync(stored with
            {
                Models = models,
                CustomProviders = customs,
                ActiveModelId = activeId
            });
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

    /// <summary>Null means the setting predates component switches and needs migration.</summary>
    public async Task<bool?> LoadOcrEnabledAsync()
    {
        await _ioGate.WaitAsync();
        try { return await LoadOptionalBooleanAsync("ocrEnabled"); }
        finally { _ioGate.Release(); }
    }

    public async Task SaveOcrEnabledAsync(bool enabled)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            await WriteStoredAsync(EnsureCompleteModels(stored) with { OcrEnabled = enabled });
        }
        finally { _ioGate.Release(); }
    }

    /// <summary>Null means the setting predates component switches and needs migration.</summary>
    public async Task<bool?> LoadLocalAiEnabledAsync()
    {
        await _ioGate.WaitAsync();
        try { return await LoadOptionalBooleanAsync("localAiEnabled"); }
        finally { _ioGate.Release(); }
    }

    private async Task<bool?> LoadOptionalBooleanAsync(string propertyName)
    {
        if (!File.Exists(_settingsPath)) return null;
        try
        {
            await using var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(propertyName, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public async Task SaveLocalAiEnabledAsync(bool enabled)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            await WriteStoredAsync(EnsureCompleteModels(stored) with { LocalAiEnabled = enabled });
        }
        finally { _ioGate.Release(); }
    }

    public async Task<string?> LoadSelectedLocalModelIdAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            return string.IsNullOrWhiteSpace(stored.SelectedLocalModelId)
                ? null
                : stored.SelectedLocalModelId;
        }
        finally { _ioGate.Release(); }
    }

    public async Task SaveSelectedLocalModelIdAsync(string modelId)
    {
        await _ioGate.WaitAsync();
        try
        {
            var (stored, _) = await LoadStoredAsync();
            await WriteStoredAsync(EnsureCompleteModels(stored) with
            {
                SelectedLocalModelId = modelId
            });
        }
        finally { _ioGate.Release(); }
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
        Dictionary<string, string>? TranslationDomainHints = null,
        bool? OcrEnabled = null,
        bool? LocalAiEnabled = null,
        string? SelectedLocalModelId = null);

    internal sealed record StoredModel(
        string Id,
        string DisplayName,
        string BaseUrl,
        string Model,
        string AuthenticationMode,
        bool IsMultimodal,
        string TargetLanguage,
        bool DisableThinking = true,
        double Temperature = 0.1);

    /// <summary>自定义在线端点（预设之外的任意 OpenAI 兼容服务）。Id 形如 "custom-{guid:N}"，Key 按 Id 存凭据库。</summary>
    internal sealed record StoredCustomProvider(
        string Id,
        string DisplayName,
        string BaseUrl,
        string Model,
        string AuthenticationMode,
        bool IsMultimodal,
        double Temperature,
        string TargetLanguage,
        bool DisableThinking = false);

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
                if (parsed is { Models: not null })
                {
                    // v5 separates built-in templates from user models and starts a new
                    // credential namespace. Reset only provider configuration once so an
                    // upgrade cannot make an old development Key look preinstalled.
                    if (parsed.Version < CurrentVersion)
                    {
                        return (parsed with
                        {
                            ActiveModelId = null,
                            Version = CurrentVersion,
                            Models = [],
                            CustomProviders = []
                        }, MigratedFromLegacy: true);
                    }
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
        return new StoredSettings(null, CurrentVersion, [], TranslationExecutionMode.Online);
    }

    private static StoredSettings MigrateFromLegacy(string _) => FreshDefaults();

    private static StoredSettings EnsureCompleteModels(StoredSettings stored) =>
        EnsureCompleteModels(stored, out _);

    private static StoredSettings EnsureCompleteModels(StoredSettings stored, out bool changed)
    {
        // 内置预设只是“添加模型”时的模板，不自动写成用户模型。这里只去重、
        // 补齐已存项目的缺失字段，并保留用户已经修改过的值。
        var models = stored.Models ?? [];
        var merged = models
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(existing =>
            {
                var preset = TranslationModelPresets.Find(existing.Id);
                return new StoredModel(
                    existing.Id,
                    string.IsNullOrWhiteSpace(existing.DisplayName) ? preset?.DisplayName ?? existing.Id : existing.DisplayName,
                    string.IsNullOrWhiteSpace(existing.BaseUrl) ? preset?.BaseUrl ?? string.Empty : existing.BaseUrl,
                    string.IsNullOrWhiteSpace(existing.Model) ? preset?.Model ?? string.Empty : existing.Model,
                    string.IsNullOrWhiteSpace(existing.AuthenticationMode) ? preset?.AuthenticationMode ?? "bearer" : existing.AuthenticationMode,
                    existing.IsMultimodal,
                    string.IsNullOrWhiteSpace(existing.TargetLanguage) ? "简体中文" : existing.TargetLanguage,
                    existing.DisableThinking,
                    existing.Temperature);
            })
            .ToList();
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

    private static void UpsertModel(
        StoredSettings stored,
        string id,
        TranslationSettings settings,
        string? displayName = null)
    {
        var preset = TranslationModelPresets.Find(id);
        var existing = stored.Models.FirstOrDefault(model => model.Id == id);
        var resolvedDisplayName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : !string.IsNullOrWhiteSpace(existing?.DisplayName)
                ? existing.DisplayName
                : preset?.DisplayName ?? (string.IsNullOrWhiteSpace(settings.Model) ? id : settings.Model);
        var updated = new StoredModel(
            id,
            resolvedDisplayName,
            settings.BaseUrl,
            settings.Model,
            settings.AuthenticationMode,
            settings.IsMultimodal,
            settings.TargetLanguage,
            settings.DisableThinking,
            settings.Temperature);
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

    private static string? ResolveActiveId(StoredSettings stored)
    {
        if (!string.IsNullOrWhiteSpace(stored.ActiveModelId) &&
            (stored.Models.Any(m => m.Id == stored.ActiveModelId) ||
             (stored.CustomProviders ?? []).Any(p => p.Id == stored.ActiveModelId)))
        {
            return stored.ActiveModelId!;
        }
        return stored.Models.FirstOrDefault()?.Id ?? stored.CustomProviders?.FirstOrDefault()?.Id;
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
            model.Temperature,
            model.DisableThinking,
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
            provider.DisableThinking,
            ProviderId: provider.Id);
        return new TranslationProfile(provider.Id, settings, apiKey)
        {
            IsCredentialStoreAvailable = credentialStoreAvailable,
            CustomDisplayName = provider.DisplayName
        };
    }

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
    public static TranslationProfile Unconfigured { get; } = new(
        "unconfigured",
        new TranslationSettings(string.Empty, string.Empty, "简体中文", "bearer", IsMultimodal: false),
        string.Empty);

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
