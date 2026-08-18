using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TransReader.App.Services;
using TransReader.Core.Storage;
using TransReader.Core.Translation;
using Windows.ApplicationModel.DataTransfer;

namespace TransReader.App.Views;

/// <summary>
/// AI 中心设置视图：单页滚动（概览 / 在线 API / 本地 AI / AI 能力 顺序堆叠、占满宽度）。
/// 服务与回调全部由宿主通过 <see cref="SettingsViewContext"/> 注入；视图直接调用
/// <see cref="TranslationSettingsStore"/> 保存，保存成功后触发 <see cref="SettingsChanged"/>。
/// 所有 async void 事件处理内部自行捕获异常并显示到本页 InfoBar，不向外抛。
/// </summary>
internal sealed partial class SettingsView : UserControl
{
    private static readonly TimeSpan ConnectionTestTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly SettingsViewContext _context;
    private readonly ObservableCollection<ProviderRowItem> _providerRows = new();
    private IReadOnlyList<TranslationProfile> _presets = [];
    private IReadOnlyList<TranslationProfile> _customs = [];
    private string? _editingId;
    private bool _isNewModel;
    private string _existingApiKey = string.Empty;
    private Uri? _providerHomepageUri;
    private string _assistantSource = "follow";
    private string _libraryAnalysisSource = "local";
    private string _domainPreference = "auto";
    private Dictionary<string, string> _domainHints = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressEvents;
    private bool _loaded;
    private bool _subscribedToLocal;
    private UpdateRelease? _availableUpdate;

    public SettingsView(SettingsViewContext context)
    {
        _context = context;
        InitializeComponent();
        ProviderList.ItemsSource = _providerRows;
        LocalModelNameText.Text = LocalAiManifest.ModelDisplayName;
        CurrentVersionText.Text = $"当前版本：{_context.UpdateService.CurrentVersionText}";
        ApplyAvailableUpdate(_context.UpdateService.LastAvailableRelease);
        Loaded += SettingsView_Loaded;
        Unloaded += SettingsView_Unloaded;
    }

    /// <summary>点"返回"时触发，由宿主切回阅读界面。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>任何设置保存成功后触发（可多次），由宿主重新加载并刷新。</summary>
    public event EventHandler? SettingsChanged;

    private void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    // ---------- 生命周期 ----------

    private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribedToLocal)
        {
            _subscribedToLocal = true;
            _context.LocalModels.StatusChanged += LocalModels_StatusChanged;
        }
        RefreshLocalUi(_context.LocalModels.Status);
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        try
        {
            var mode = await _context.SettingsStore.LoadExecutionModeAsync();
            _suppressEvents = true;
            try
            {
                ModeToggle.IsOn = mode == TranslationExecutionMode.Online;
            }
            finally
            {
                _suppressEvents = false;
            }
            await ReloadProvidersAsync();
            await LoadCapabilitiesAsync();
            await RefreshOverviewAsync();
        }
        catch (Exception ex)
        {
            ShowInfo(OverviewInfoBar, InfoBarSeverity.Error, "加载设置失败", ex.Message);
        }
    }

    private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedToLocal)
        {
            _subscribedToLocal = false;
            _context.LocalModels.StatusChanged -= LocalModels_StatusChanged;
        }
    }

    // ---------- 导航（单页滚动：锚点跳转） ----------

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void InstallLocalModelLink_Click(object sender, RoutedEventArgs e) => SectionLocal.StartBringIntoView();

    // ---------- 总览 ----------

    private async void ModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        try
        {
            var mode = ModeToggle.IsOn ? TranslationExecutionMode.Online : TranslationExecutionMode.Local;
            await _context.SettingsStore.SaveExecutionModeAsync(mode);
            RaiseSettingsChanged();
            await RefreshOverviewAsync();
        }
        catch (Exception ex)
        {
            ShowInfo(OverviewInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    private async Task RefreshOverviewAsync()
    {
        var profile = await _context.SettingsStore.LoadAsync();
        OverviewOnlineModelName.Text = profile.IsConfigured ? profile.DisplayName : "未配置在线模型";
        var modality = profile.IsConfigured
            ? profile.IsMultimodal ? "多模态（图文）" : "纯文本（OCR + 翻译）"
            : "已配置模型 0 个";
        var status = !profile.IsCredentialStoreAvailable
            ? "凭据库不可用"
            : profile.IsConfigured ? "已配置" : "未配置";
        OverviewOnlineModelMeta.Text = $"{modality} · {status}";
        RefreshLocalUi(_context.LocalModels.Status);

        var usage = _context.UsageStore.GetSummary();
        OverviewUsageText.Text = usage.TotalTokens > 0
            ? $"今日 {usage.TodayTotalTokens:N0} tokens · 累计 {usage.TotalTokens:N0} tokens" +
              (usage.Models.Count > 0
                  ? "\n" + string.Join("　", usage.Models.Take(3).Select(m => $"{m.Model} {m.TotalTokens:N0}"))
                  : string.Empty)
            : "暂无在线翻译用量记录。";
    }

    // ---------- 在线 API：列表 ----------

    private async Task ReloadProvidersAsync()
    {
        _presets = await _context.SettingsStore.LoadAllAsync();
        _customs = await _context.SettingsStore.LoadCustomProvidersAsync();
        var activeId = (await _context.SettingsStore.LoadAsync()).Id;
        var usage = _context.UsageStore.GetSummary();

        _providerRows.Clear();
        foreach (var profile in _presets.Concat(_customs))
        {
            _providerRows.Add(CreateRow(profile, activeId, usage));
        }
        ProviderCountText.Text = $"已配置模型（{_providerRows.Count}）";
        var configuredCount = _providerRows.Count(row => row.StatusText == "已配置");
        ProviderSummaryText.Text =
            configuredCount == 0
                ? "还没有模型。点击“添加模型”并输入自己的 API Key。"
                : $"自定义 {_customs.Count} 个 · 当前活动：{_providerRows.FirstOrDefault(row => row.IsActive)?.DisplayName ?? "未设置"}";
        RefreshAssistantSourceItems();
        RefreshLibraryAnalysisSourceItems();
        // 表单只在用户显式点「编辑/添加」时展开；重载后若表单开着则刷新其内容。
        if (ProviderFormCard.Visibility == Visibility.Visible && !_isNewModel && _editingId is not null)
        {
            var row = _providerRows.FirstOrDefault(r => r.Id == _editingId);
            if (row is not null) LoadForm(row);
        }
    }

    private ProviderRowItem CreateRow(TranslationProfile profile, string activeId, TranslationUsageSummary usage)
    {
        var (statusText, brushKey) = !profile.IsCredentialStoreAvailable
            ? ("凭据库不可用", "StatusCautionBrush")
            : profile.IsConfigured
                ? ("已配置", "StatusSuccessBrush")
                : ("未配置", "StatusNeutralBrush");
        var modelUsage = usage.Models.FirstOrDefault(m =>
            string.Equals(m.Model, profile.Settings.Model, StringComparison.Ordinal));
        return new ProviderRowItem
        {
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            EndpointText = $"{profile.Settings.Model} · {profile.Settings.BaseUrl}",
            StatusText = statusText,
            StatusBrush = (Brush)Resources[brushKey],
            SuccessBrush = (Brush)Resources["StatusSuccessBrush"],
            CautionBrush = (Brush)Resources["StatusCautionBrush"],
            IsCustom = IsCustomId(profile.Id),
            IsActive = string.Equals(profile.Id, activeId, StringComparison.Ordinal),
            UsageText = modelUsage is null ? string.Empty : $"累计 {modelUsage.TotalTokens:N0} tokens",
            HasUsage = modelUsage is { TotalTokens: > 0 },
        };
    }

    private static bool IsCustomId(string id) => id.StartsWith("custom-", StringComparison.Ordinal);

    private TranslationProfile? FindProfile(string id) =>
        _presets.Concat(_customs).FirstOrDefault(p => p.Id == id);

    private void ShowProviderForm()
    {
        ProviderFormCard.Visibility = Visibility.Visible;
        ProviderFormCard.StartBringIntoView();
    }

    private void ProviderFormCancel_Click(object sender, RoutedEventArgs e)
    {
        _isNewModel = false;
        _editingId = null;
        _existingApiKey = string.Empty;
        ProviderFormCard.Visibility = Visibility.Collapsed;
    }

    private async void ProviderTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button)
        {
            return;
        }
        var row = _providerRows.FirstOrDefault(r => r.Id == id);
        var profile = FindProfile(id);
        if (row is null || profile is null)
        {
            return;
        }
        button.IsEnabled = false;
        var originalContent = button.Content;
        button.Content = "测试中…";
        row.ClearTestResult();
        using var timeout = new CancellationTokenSource(ConnectionTestTimeout);
        try
        {
            await new OpenAiCompatibleTranslator().TestAsync(profile.Settings, profile.ApiKey, timeout.Token);
            row.SetTestResult(true, "连接正常", "地址、鉴权和模型名称均可用。");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            row.SetTestResult(false, "连接超时", "20 秒内没有收到响应，请检查 API 地址、网络或模型 ID。");
        }
        catch (Exception ex)
        {
            row.SetTestResult(false, "连接失败", ex.Message);
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
        }
    }

    private void ProviderEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id)
        {
            return;
        }
        var row = _providerRows.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            return;
        }
        LoadForm(row);
        ShowProviderForm();
    }

    private async void ProviderSetActive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id)
        {
            return;
        }
        try
        {
            await _context.SettingsStore.SetActiveModelAsync(id);
            await ReloadProvidersAsync();
            await RefreshOverviewAsync();
            RaiseSettingsChanged();
        }
        catch (Exception ex)
        {
            ShowInfo(OnlineInfoBar, InfoBarSeverity.Error, "操作失败", ex.Message);
        }
    }

    private async void ProviderDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id)
        {
            return;
        }
        var row = _providerRows.FirstOrDefault(r => r.Id == id);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除模型",
            Content = $"确定删除「{row?.DisplayName ?? id}」吗？其 API Key 将一并清除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            await _context.SettingsStore.DeleteProviderAsync(id);
            if (_editingId == id)
            {
                _editingId = null;
                ProviderFormCard.Visibility = Visibility.Collapsed;
            }
            await ReloadProvidersAsync();
            await RefreshOverviewAsync();
            RaiseSettingsChanged();
        }
        catch (Exception ex)
        {
            ShowInfo(OnlineInfoBar, InfoBarSeverity.Error, "删除失败", ex.Message);
        }
    }

    // ---------- 在线 API：编辑表单 ----------

    private void LoadForm(ProviderRowItem row)
    {
        var profile = FindProfile(row.Id);
        if (profile is null)
        {
            return;
        }
        _isNewModel = false;
        _editingId = row.Id;
        _existingApiKey = profile.ApiKey;
        _suppressEvents = true;
        try
        {
            FormTitleText.Text = $"编辑：{profile.DisplayName}";
            SelectComboByTag(ProviderTemplateBox, row.IsCustom ? TranslationModelPresets.CustomId : row.Id);
            ProviderTemplateBox.IsEnabled = false;
            DisplayNameBox.Text = profile.DisplayName;
            BaseUrlBox.Text = profile.Settings.BaseUrl;
            ModelBox.Text = profile.Settings.Model;
            ApiKeyBox.Password = string.Empty;
            ApiKeyStatusText.Text = string.IsNullOrWhiteSpace(profile.ApiKey)
                ? "尚未保存 API Key。"
                : "API Key 已安全保存；留空可保持原 Key。";
            SelectComboByContent(ProviderLanguageBox, profile.Settings.TargetLanguage);
            ImageInputCheckBox.IsChecked = profile.Settings.IsMultimodal;
            ReasoningToggle.IsOn = !profile.Settings.DisableThinking;
            ResetDetectedModels();
            ProviderFormStatusBar.IsOpen = false;
            UpdateProviderHomepage();
        }
        finally
        {
            _suppressEvents = false;
        }
        UpdateGeneratedJson();
    }

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        _isNewModel = true;
        _editingId = null;
        _existingApiKey = string.Empty;
        _suppressEvents = true;
        try
        {
            FormTitleText.Text = "添加模型";
            ProviderTemplateBox.IsEnabled = true;
            SelectComboByTag(ProviderTemplateBox, "mimo");
            ProviderLanguageBox.SelectedIndex = 0;
            ApplySelectedTemplate();
            ApiKeyStatusText.Text = "尚未保存；API Key 不会出现在 JSON 中。";
            ResetDetectedModels();
            ProviderFormStatusBar.IsOpen = false;
        }
        finally
        {
            _suppressEvents = false;
        }
        UpdateGeneratedJson();
        ShowProviderForm();
    }

    private ProviderFormValues ReadForm(bool requireModel = true)
    {
        var baseUrl = BaseUrlBox.Text.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("请输入有效的 HTTP 或 HTTPS API 地址。");
        }
        var model = ModelBox.Text.Trim();
        if (requireModel && model.Length == 0)
        {
            throw new InvalidOperationException("模型 ID 不能为空。可以点击“检测模型”自动获取。");
        }
        var authMode = ResolveAuthenticationMode();
        var apiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Password)
            ? _existingApiKey
            : ApiKeyBox.Password.Trim();
        if (!authMode.Equals("none", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("API Key 不能为空。");
        }
        var language = (ProviderLanguageBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "简体中文";
        return new ProviderFormValues(
            baseUrl,
            model,
            apiKey,
            authMode,
            language,
            ImageInputCheckBox.IsChecked == true,
            ReasoningToggle.IsOn);
    }

    private async void ProviderSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var form = ReadForm();
            var templateId = SelectedTemplateId();
            var id = _isNewModel
                ? templateId == TranslationModelPresets.CustomId
                    ? $"custom-{Guid.NewGuid():N}"
                    : templateId
                : _editingId;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("请选择供应商模板。");
            }
            var displayName = DisplayNameBox.Text.Trim();
            if (displayName.Length == 0)
            {
                throw new InvalidOperationException("请输入模型的显示名称。");
            }
            if (IsCustomId(id))
            {
                await _context.SettingsStore.SaveCustomProviderAsync(
                    new TranslationSettingsStore.StoredCustomProvider(
                        id, displayName, form.BaseUrl, form.Model, form.AuthMode,
                        form.IsMultimodal, 0.1, form.Language,
                        DisableThinking: !form.Reasoning),
                    form.ApiKey);
            }
            else
            {
                await _context.SettingsStore.SaveModelAsync(
                    id,
                    BuildSettings(form, id),
                    form.ApiKey,
                    displayName);
            }
            await _context.SettingsStore.SetActiveModelAsync(id);
            _isNewModel = false;
            _editingId = null;
            _existingApiKey = string.Empty;
            ProviderFormCard.Visibility = Visibility.Collapsed;
            await ReloadProvidersAsync();
            await RefreshOverviewAsync();
            RaiseSettingsChanged();
            ShowInfo(OnlineInfoBar, InfoBarSeverity.Success, "已保存", "设置已保存。");
        }
        catch (Exception ex)
        {
            ShowInfo(OnlineInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    private async void ProviderFormTest_Click(object sender, RoutedEventArgs e)
    {
        ProviderFormTestButton.IsEnabled = false;
        ProviderFormTestButton.Content = "正在测试…";
        TestConnectionProgressRing.Visibility = Visibility.Visible;
        TestConnectionProgressRing.IsActive = true;
        ShowInfo(ProviderFormStatusBar, InfoBarSeverity.Informational, "正在测试连接", "正在请求所选模型，最长等待 20 秒…");
        using var timeout = new CancellationTokenSource(ConnectionTestTimeout);
        try
        {
            await Task.Yield();
            var form = ReadForm();
            var settings = BuildSettings(form, _editingId ?? SelectedTemplateId());
            await new OpenAiCompatibleTranslator().TestAsync(settings, form.ApiKey, timeout.Token);
            ShowInfo(ProviderFormStatusBar, InfoBarSeverity.Success, "连接成功", "API 地址、Key 和模型 ID 均可用。");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            ShowInfo(ProviderFormStatusBar, InfoBarSeverity.Error, "连接超时", "20 秒内没有收到响应，请检查 API 地址、网络或模型 ID。");
        }
        catch (Exception ex)
        {
            ShowInfo(ProviderFormStatusBar, InfoBarSeverity.Error, "连接失败", ex.Message);
        }
        finally
        {
            TestConnectionProgressRing.IsActive = false;
            TestConnectionProgressRing.Visibility = Visibility.Collapsed;
            ProviderFormTestButton.Content = "测试连接";
            ProviderFormTestButton.IsEnabled = true;
        }
    }

    private async void DetectModels_Click(object sender, RoutedEventArgs e)
    {
        DetectModelsButton.IsEnabled = false;
        DetectModelsButton.Content = "检测中…";
        ShowInfo(ProviderFormStatusBar, InfoBarSeverity.Informational, "正在检测模型", "正在读取 API 的 /models 列表，最长等待 20 秒…");
        using var timeout = new CancellationTokenSource(ConnectionTestTimeout);
        try
        {
            await Task.Yield();
            var form = ReadForm(requireModel: false);
            var settings = BuildSettings(form with
            {
                Model = string.IsNullOrWhiteSpace(form.Model) ? "model-discovery" : form.Model
            }, _editingId ?? SelectedTemplateId());
            var models = await new OpenAiCompatibleTranslator().DiscoverModelsAsync(settings, form.ApiKey, timeout.Token);
            DetectedModelBox.Items.Clear();
            foreach (var model in models)
            {
                DetectedModelBox.Items.Add(model);
            }
            DetectedModelBox.Visibility = Visibility.Visible;
            var currentIndex = models.ToList().FindIndex(model =>
                string.Equals(model, ModelBox.Text.Trim(), StringComparison.Ordinal));
            DetectedModelBox.SelectedIndex = currentIndex >= 0 ? currentIndex : 0;
            ShowInfo(ProviderFormStatusBar, InfoBarSeverity.Success, "检测完成", $"共发现 {models.Count} 个模型，请从列表中选择。");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            ResetDetectedModels();
            ShowInfo(ProviderFormStatusBar, InfoBarSeverity.Error, "检测超时", "20 秒内没有收到响应，请检查 API 地址、Key 或网络。");
        }
        catch (Exception ex)
        {
            ResetDetectedModels();
            ShowInfo(ProviderFormStatusBar, InfoBarSeverity.Error, "检测模型失败", ex.Message);
        }
        finally
        {
            DetectModelsButton.Content = "检测模型";
            DetectModelsButton.IsEnabled = true;
        }
    }

    private void DetectedModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || DetectedModelBox.SelectedItem is not string model)
        {
            return;
        }
        ModelBox.Text = model;
    }

    private void ProviderTemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        _existingApiKey = string.Empty;
        ApplySelectedTemplate();
        ResetDetectedModels();
        UpdateGeneratedJson();
    }

    private void ApplySelectedTemplate()
    {
        var preset = TranslationModelPresets.Find(SelectedTemplateId());
        if (preset is null)
        {
            DisplayNameBox.Text = string.Empty;
            BaseUrlBox.Text = string.Empty;
            ModelBox.Text = string.Empty;
            ApiKeyBox.Password = string.Empty;
            ImageInputCheckBox.IsChecked = false;
            ReasoningToggle.IsOn = true;
        }
        else
        {
            DisplayNameBox.Text = preset.DisplayName;
            BaseUrlBox.Text = preset.BaseUrl;
            ModelBox.Text = preset.Model;
            ApiKeyBox.Password = string.Empty;
            ImageInputCheckBox.IsChecked = preset.IsMultimodal;
            ReasoningToggle.IsOn = !preset.DisableThinking;
        }
        ApiKeyStatusText.Text = "尚未保存；API Key 不会出现在 JSON 中。";
        UpdateProviderHomepage();
    }

    private static TranslationSettings BuildSettings(ProviderFormValues form, string providerId) => new(
        form.BaseUrl,
        form.Model,
        form.Language,
        form.AuthMode,
        IsMultimodal: form.IsMultimodal,
        Temperature: 0.1,
        DisableThinking: !form.Reasoning,
        ProviderId: providerId);

    private string ResolveAuthenticationMode() =>
        TranslationModelPresets.Find(SelectedTemplateId())?.AuthenticationMode ?? "bearer";

    private string SelectedTemplateId() =>
        (ProviderTemplateBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
        ?? TranslationModelPresets.CustomId;

    private void ResetDetectedModels()
    {
        DetectedModelBox.Items.Clear();
        DetectedModelBox.Visibility = Visibility.Collapsed;
    }

    private void ProviderFormValue_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents)
        {
            UpdateGeneratedJson();
        }
    }

    private void UpdateGeneratedJson()
    {
        if (GeneratedJsonBox is null)
        {
            return;
        }
        var modelId = ModelBox.Text.Trim();
        var models = new List<Dictionary<string, object?>>();
        if (modelId.Length > 0)
        {
            models.Add(new Dictionary<string, object?>
            {
                ["id"] = modelId,
                ["name"] = DisplayNameBox.Text.Trim(),
                ["input"] = ImageInputCheckBox.IsChecked == true ? new[] { "text", "image" } : new[] { "text" },
                ["reasoning"] = ReasoningToggle.IsOn,
                ["targetLanguage"] = (ProviderLanguageBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "简体中文"
            });
        }
        var preview = new Dictionary<string, object?>
        {
            ["baseUrl"] = BaseUrlBox.Text.Trim(),
            ["apiKey"] = string.Empty,
            ["api"] = "openai-completions",
            ["models"] = models
        };
        GeneratedJsonBox.Text = JsonSerializer.Serialize(preview, PreviewJsonOptions);
    }

    private void CopyGeneratedJson_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(GeneratedJsonBox.Text);
        Clipboard.SetContent(package);
        ShowInfo(OnlineInfoBar, InfoBarSeverity.Success, "已复制", "配置 JSON 已复制；其中不包含 API Key。 ");
    }

    private async void ProviderHomepageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_providerHomepageUri is null)
        {
            return;
        }
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(_providerHomepageUri);
        }
        catch (Exception ex)
        {
            ShowInfo(OnlineInfoBar, InfoBarSeverity.Error, "无法打开官网", ex.Message);
        }
    }

    private void UpdateProviderHomepage()
    {
        var preset = TranslationModelPresets.Find(SelectedTemplateId());
        _providerHomepageUri = preset is null ? null : new Uri(preset.HomepageUrl);
        ProviderHomepageButton.IsEnabled = _providerHomepageUri is not null;
        ProviderHomepageButton.Content = _providerHomepageUri is null
            ? "自定义供应商（无官网链接）"
            : $"官网：{preset!.HomepageUrl}";
    }

    private sealed record ProviderFormValues(
        string BaseUrl,
        string Model,
        string ApiKey,
        string AuthMode,
        string Language,
        bool IsMultimodal,
        bool Reasoning);

    // ---------- 本地 AI ----------

    private void LocalModels_StatusChanged(object? sender, LocalAiStatus status) =>
        DispatcherQueue.TryEnqueue(() => RefreshLocalUi(status));

    private void RefreshLocalUi(LocalAiStatus status)
    {
        var localModels = _context.LocalModels;
        var downloading = status.State == LocalAiState.Installing && status.TotalBytes > 0;
        LocalStatusText.Text = downloading
            ? $"{status.Message}（{status.BytesReceived / 1048576d:0.#} / {status.TotalBytes / 1048576d:0.#} MB）"
            : status.Message;
        LocalInstallProgress.Visibility = status.State == LocalAiState.Installing
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (status.TotalBytes > 0)
        {
            LocalInstallProgress.IsIndeterminate = false;
            LocalInstallProgress.Value = status.Progress;
        }
        else
        {
            LocalInstallProgress.IsIndeterminate = status.State == LocalAiState.Installing;
        }
        var installedSize = localModels.InstalledSize / 1048576d;
        LocalInstalledSizeText.Text = $"已安装体积：{installedSize:0.#} MB";
        OverviewLocalModelText.Text = $"{LocalAiManifest.ModelDisplayName} · {status.Message} · 已安装 {installedSize:0.#} MB";

        var busy = status.State is LocalAiState.Installing or LocalAiState.Starting;
        LocalInstallButton.IsEnabled = !busy;
        LocalVerifyButton.IsEnabled = !busy && localModels.IsInstalled;
        LocalUninstallButton.IsEnabled = !busy && localModels.IsInstalled;

        UpdatePendingAnalysisText();
    }

    private async void LocalInstall_Click(object sender, RoutedEventArgs e)
    {
        LocalInstallButton.IsEnabled = false;
        try
        {
            await _context.LocalModels.InstallAsync();
            await _context.EnqueuePendingAnalysesAsync();
            UpdatePendingAnalysisText();
            ShowInfo(LocalInfoBar, InfoBarSeverity.Success, "安装完成", _context.LocalModels.Status.Message);
        }
        catch (OperationCanceledException)
        {
            // 用户取消：状态机已回落到 未安装/已安装，无需提示。
        }
        catch (Exception ex)
        {
            ShowInfo(LocalInfoBar, InfoBarSeverity.Error, "本地 AI 安装失败", ex.Message);
        }
        finally
        {
            RefreshLocalUi(_context.LocalModels.Status);
        }
    }

    private async void LocalVerify_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var valid = await _context.LocalModels.VerifyAsync();
            ShowInfo(LocalInfoBar, valid ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                valid ? "校验通过" : "校验失败", _context.LocalModels.Status.Message);
        }
        catch (Exception ex)
        {
            ShowInfo(LocalInfoBar, InfoBarSeverity.Error, "校验失败", ex.Message);
        }
        finally
        {
            RefreshLocalUi(_context.LocalModels.Status);
        }
    }

    private async void LocalUninstall_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "卸载本地模型",
            Content = "将删除本地模型与推理运行时（约 1.3 GB），之后可重新安装。确定卸载吗？",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            await _context.LocalModels.UninstallAsync();
            ShowInfo(LocalInfoBar, InfoBarSeverity.Success, "已卸载", _context.LocalModels.Status.Message);
        }
        catch (Exception ex)
        {
            ShowInfo(LocalInfoBar, InfoBarSeverity.Error, "卸载失败", ex.Message);
        }
        finally
        {
            RefreshLocalUi(_context.LocalModels.Status);
        }
    }

    // ---------- AI 能力 ----------

    private async Task LoadCapabilitiesAsync()
    {
        var active = await _context.SettingsStore.LoadAsync();
        var prefetch = await _context.SettingsStore.LoadPrefetchTranslationAsync();
        var fallback = await _context.SettingsStore.LoadLocalFallbackEnabledAsync();
        var autoAnalysis = await _context.SettingsStore.LoadLibraryAutoAnalysisEnabledAsync();
        _assistantSource = await _context.SettingsStore.LoadAssistantModelSourceAsync();
        _libraryAnalysisSource = await _context.SettingsStore.LoadLibraryAnalysisSourceAsync();
        _domainPreference = await _context.SettingsStore.LoadTranslationDomainPreferenceAsync();
        _domainHints = await _context.SettingsStore.LoadDomainPromptHintsAsync()
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _suppressEvents = true;
        try
        {
            SelectComboByContent(CapabilityLanguageBox, active.Settings.TargetLanguage);
            PrefetchBox.IsChecked = prefetch;
            LocalFallbackBox.IsChecked = fallback;
            AutoAnalysisToggle.IsOn = autoAnalysis;
            RefreshAssistantSourceItems();
            RefreshLibraryAnalysisSourceItems();
            RefreshDomainPreferenceItems();
            RefreshDomainHintEditor();
        }
        finally
        {
            _suppressEvents = false;
        }
        UpdatePendingAnalysisText();
    }

    private void RefreshDomainPreferenceItems()
    {
        _suppressEvents = true;
        try
        {
            DomainPreferenceBox.Items.Clear();
            DomainPreferenceBox.Items.Add(new ComboBoxItem { Content = "自动（跟随 AI 文献分析）", Tag = "auto" });
            foreach (var profile in TranslationDomainProfiles.All)
            {
                DomainPreferenceBox.Items.Add(new ComboBoxItem { Content = profile.DisplayName, Tag = profile.Key });
            }
            SelectComboByTag(DomainPreferenceBox, _domainPreference);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private async void DomainPreferenceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !_loaded)
        {
            return;
        }
        if ((DomainPreferenceBox.SelectedItem as ComboBoxItem)?.Tag is not string preference)
        {
            return;
        }
        try
        {
            _domainPreference = preference;
            await _context.SettingsStore.SaveTranslationDomainPreferenceAsync(preference);
            RaiseSettingsChanged();
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    // ---------- 领域提示词 ----------

    private void RefreshDomainHintEditor()
    {
        DomainHintPickerBox.Items.Clear();
        // 通用（general）排最前，方便设置全局追加提示；其余按注册表顺序。
        foreach (var profile in TranslationDomainProfiles.All.OrderBy(p => p.Key == "general" ? 0 : 1))
        {
            DomainHintPickerBox.Items.Add(new ComboBoxItem { Content = profile.DisplayName, Tag = profile.Key });
        }
        DomainHintPickerBox.SelectedIndex = 0;
        LoadDomainHintIntoEditor(CurrentDomainHintKey());
    }

    private string CurrentDomainHintKey() =>
        (DomainHintPickerBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "general";

    private void LoadDomainHintIntoEditor(string key)
    {
        var profile = TranslationDomainProfiles.Find(key);
        var resolvedKey = profile?.Key ?? key;
        var hasCustom = _domainHints.TryGetValue(resolvedKey, out var custom) && !string.IsNullOrWhiteSpace(custom);
        DomainHintTextBox.Text = hasCustom ? custom : profile?.PromptHint ?? string.Empty;
        DomainHintTextBox.PlaceholderText = profile?.PromptHint is { Length: > 0 } builtin
            ? $"内置默认：{builtin}"
            : "内置默认：无（不追加提示）";
        DomainHintStateText.Text = hasCustom ? "当前：自定义" : "当前：内置默认";
    }

    private void DomainHintPickerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }
        LoadDomainHintIntoEditor(CurrentDomainHintKey());
    }

    private async void DomainHintSave_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        var key = CurrentDomainHintKey();
        var text = DomainHintTextBox.Text.Trim();
        if (text.Length == 0) _domainHints.Remove(key); else _domainHints[key] = text;
        try
        {
            await _context.SettingsStore.SaveDomainPromptHintsAsync(
                _domainHints.Count == 0 ? null : new Dictionary<string, string>(_domainHints));
            TranslationDomainProfiles.SetOverrides(_domainHints);
            LoadDomainHintIntoEditor(key);
            RaiseSettingsChanged();
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Success, "已保存", "领域提示词已保存，相关页面将按新提示词重新翻译。");
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    private async void DomainHintReset_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        var key = CurrentDomainHintKey();
        _domainHints.Remove(key);
        try
        {
            await _context.SettingsStore.SaveDomainPromptHintsAsync(
                _domainHints.Count == 0 ? null : new Dictionary<string, string>(_domainHints));
            TranslationDomainProfiles.SetOverrides(_domainHints);
            LoadDomainHintIntoEditor(key);
            RaiseSettingsChanged();
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Success, "已恢复默认", "该领域已恢复内置默认提示词。");
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "操作失败", ex.Message);
        }
    }

    private async void CapabilityLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !_loaded)
        {
            return;
        }
        try
        {
            var language = (CapabilityLanguageBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "简体中文";
            var active = await _context.SettingsStore.LoadAsync();
            if (!active.IsConfigured)
            {
                return;
            }
            if (string.Equals(active.Settings.TargetLanguage, language, StringComparison.Ordinal))
            {
                return;
            }
            if (IsCustomId(active.Id))
            {
                // 自定义活动端点：按 StoredCustomProvider 形状回写，保留其余字段。
                await _context.SettingsStore.SaveCustomProviderAsync(
                    new TranslationSettingsStore.StoredCustomProvider(
                        active.Id, active.CustomDisplayName ?? active.DisplayName,
                        active.Settings.BaseUrl, active.Settings.Model, active.Settings.AuthenticationMode,
                        active.Settings.IsMultimodal, active.Settings.Temperature, language,
                        active.Settings.DisableThinking),
                    active.ApiKey);
            }
            else
            {
                await _context.SettingsStore.SaveModelAsync(active.Id,
                    active.Settings with { TargetLanguage = language }, active.ApiKey);
            }
            await ReloadProvidersAsync();
            RaiseSettingsChanged();
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    private async void PrefetchBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !_loaded)
        {
            return;
        }
        try
        {
            await _context.SettingsStore.SavePrefetchTranslationAsync(PrefetchBox.IsChecked == true);
            RaiseSettingsChanged();
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    private async void LocalFallbackBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !_loaded)
        {
            return;
        }
        try
        {
            await _context.SettingsStore.SaveLocalFallbackEnabledAsync(LocalFallbackBox.IsChecked == true);
            RaiseSettingsChanged();
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    private void RefreshAssistantSourceItems()
    {
        _suppressEvents = true;
        try
        {
            AssistantSourceBox.Items.Clear();
            AssistantSourceBox.Items.Add(new ComboBoxItem { Content = "跟随翻译模式", Tag = "follow" });
            AssistantSourceBox.Items.Add(new ComboBoxItem { Content = "固定本地 Qwen3", Tag = "local" });
            foreach (var profile in _presets.Concat(_customs))
            {
                AssistantSourceBox.Items.Add(new ComboBoxItem { Content = profile.DisplayName, Tag = profile.Id });
            }
            SelectComboByTag(AssistantSourceBox, _assistantSource);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void RefreshLibraryAnalysisSourceItems()
    {
        _suppressEvents = true;
        try
        {
            LibraryAnalysisSourceBox.Items.Clear();
            LibraryAnalysisSourceBox.Items.Add(new ComboBoxItem { Content = "本地 Qwen3（离线，免费）", Tag = "local" });
            LibraryAnalysisSourceBox.Items.Add(new ComboBoxItem { Content = "跟随翻译模式（当前活动在线模型）", Tag = "follow" });
            foreach (var profile in _presets.Concat(_customs))
            {
                LibraryAnalysisSourceBox.Items.Add(new ComboBoxItem { Content = profile.DisplayName, Tag = profile.Id });
            }
            SelectComboByTag(LibraryAnalysisSourceBox, _libraryAnalysisSource);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private async void LibraryAnalysisSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !_loaded)
        {
            return;
        }
        if ((LibraryAnalysisSourceBox.SelectedItem as ComboBoxItem)?.Tag is not string source)
        {
            return;
        }
        try
        {
            _libraryAnalysisSource = source;
            await _context.SettingsStore.SaveLibraryAnalysisSourceAsync(source);
            RaiseSettingsChanged();
            UpdatePendingAnalysisText();
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    private async void AssistantSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || !_loaded)
        {
            return;
        }
        if ((AssistantSourceBox.SelectedItem as ComboBoxItem)?.Tag is not string source)
        {
            return;
        }
        try
        {
            _assistantSource = source;
            await _context.SettingsStore.SaveAssistantModelSourceAsync(source);
            RaiseSettingsChanged();
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    private async void ClearAssistantHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空问答历史",
            Content = "将删除全部文献的问答历史，此操作不可恢复。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            // 历史存储即构造即用；路径与 MainWindow 装配 ReaderAssistantService 时一致。
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TransReader", "cache", "assistant");
            new ReaderAssistantHistoryStore(root).Clear();
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Success, "已清空", "问答历史已全部删除。");
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "清空失败", ex.Message);
        }
    }

    private async void AutoAnalysisToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !_loaded)
        {
            return;
        }
        try
        {
            await _context.SettingsStore.SaveLibraryAutoAnalysisEnabledAsync(AutoAnalysisToggle.IsOn);
            RaiseSettingsChanged();
            UpdatePendingAnalysisText();
        }
        catch (Exception ex)
        {
            ShowInfo(CapabilitiesInfoBar, InfoBarSeverity.Error, "无法保存设置", ex.Message);
        }
    }

    private void UpdatePendingAnalysisText()
    {
        var pending = _context.GetPendingAnalysisCount();
        PendingAnalysisText.Text = pending > 0
            ? $"当前有 {pending} 篇文献等待分析。"
            : "当前没有等待分析的文献。";
        // 安装引导仅在来源为本地且未安装时显示；在线来源不依赖本地模型。
        InstallLocalModelLink.Visibility = _libraryAnalysisSource == "local" && !_context.LocalModels.IsInstalled
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------- 应用更新 ----------

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查 GitHub Releases…";
        UpdateInfoBar.IsOpen = false;
        try
        {
            var release = await _context.UpdateService.CheckForUpdateAsync();
            ApplyAvailableUpdate(release);
            if (release is null)
            {
                ShowInfo(UpdateInfoBar, InfoBarSeverity.Success, "已是最新版", "当前没有可用的稳定版更新。");
            }
            else
            {
                ShowInfo(UpdateInfoBar, InfoBarSeverity.Informational, "发现新版本", $"版本 {release.Version} 已可下载。安装前会验证 SHA-256。 ");
            }
        }
        catch (Exception ex)
        {
            ShowInfo(UpdateInfoBar, InfoBarSeverity.Error, "检查更新失败", ex.Message);
            UpdateStatusText.Text = "暂时无法获取版本信息，请稍后重试。";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = _availableUpdate is not null;
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = true;
        UpdateInfoBar.IsOpen = false;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (value.Percentage is double percentage)
                {
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = percentage;
                    UpdateStatusText.Text = $"正在下载安装包… {percentage:0}%";
                }
                else
                {
                    UpdateStatusText.Text = "正在下载安装包…";
                }
            });
            var installerPath = await _context.UpdateService.DownloadAndVerifyAsync(_availableUpdate, progress);
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 100;
            UpdateStatusText.Text = "校验通过，正在启动安装程序…";
            _context.UpdateService.LaunchInstaller(installerPath);
            _context.ExitApplication();
        }
        catch (Exception ex)
        {
            ShowInfo(UpdateInfoBar, InfoBarSeverity.Error, "更新失败", ex.Message);
            UpdateStatusText.Text = "更新未安装，当前版本不受影响。";
            CheckUpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyAvailableUpdate(UpdateRelease? release)
    {
        _availableUpdate = release;
        InstallUpdateButton.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        InstallUpdateButton.IsEnabled = release is not null;
        UpdateStatusText.Text = release is null
            ? "可手动检查 GitHub Releases 中的稳定版本。"
            : $"发现版本 {release.Version}，安装包约 {FormatFileSize(release.InstallerSize)}。";
    }

    private static string FormatFileSize(long bytes) => bytes > 0
        ? $"{bytes / 1024d / 1024d:0.0} MiB"
        : "未知大小";

    // ---------- 辅助 ----------

    private static void ShowInfo(InfoBar bar, InfoBarSeverity severity, string title, string message)
    {
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;
    }

    private static void SelectComboByContent(ComboBox box, string content)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), content, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static void SelectComboByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }
}

/// <summary>在线 API 列表行（预设 + 自定义端点）。测试结果通过 INPC 就地更新。</summary>
internal sealed class ProviderRowItem : INotifyPropertyChanged
{
    private bool _hasTestResult;
    private string _testResultGlyph = string.Empty;
    private string _testResultText = string.Empty;
    private string _testResultMessage = string.Empty;
    private Brush _testResultBrush = new SolidColorBrush();

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string EndpointText { get; init; }
    public required string StatusText { get; init; }
    public required Brush StatusBrush { get; init; }
    public required Brush SuccessBrush { get; init; }
    public required Brush CautionBrush { get; init; }
    public required bool IsCustom { get; init; }
    public required bool IsActive { get; init; }
    public required string UsageText { get; init; }
    public required bool HasUsage { get; init; }

    public bool IsNotActive => !IsActive;

    public bool HasTestResult
    {
        get => _hasTestResult;
        private set => SetField(ref _hasTestResult, value, nameof(HasTestResult));
    }

    public string TestResultGlyph
    {
        get => _testResultGlyph;
        private set => SetField(ref _testResultGlyph, value, nameof(TestResultGlyph));
    }

    public string TestResultText
    {
        get => _testResultText;
        private set => SetField(ref _testResultText, value, nameof(TestResultText));
    }

    public string TestResultMessage
    {
        get => _testResultMessage;
        private set => SetField(ref _testResultMessage, value, nameof(TestResultMessage));
    }

    public Brush TestResultBrush
    {
        get => _testResultBrush;
        private set => SetField(ref _testResultBrush, value, nameof(TestResultBrush));
    }

    public void SetTestResult(bool success, string text, string message)
    {
        TestResultGlyph = success ? "\uE73E" : "\uE711";
        TestResultText = text;
        TestResultMessage = message;
        TestResultBrush = success ? SuccessBrush : CautionBrush;
        HasTestResult = true;
    }

    public void ClearTestResult() => HasTestResult = false;

    private void SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
