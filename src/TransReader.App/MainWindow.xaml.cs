using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TransReader.App.Services;
using TransReader.Core.Documents;
using TransReader.Core.Library;
using TransReader.Core.Ocr;
using TransReader.Core.Storage;
using TransReader.Core.Translation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace TransReader.App;

public sealed partial class MainWindow : Window
{
    private readonly DocumentSession _session = new();
    private readonly PageProcessingService _processing;
    private readonly TranslationSettingsStore _settingsStore;
    private readonly string _legacyRecentFilesPath;
    private readonly string _cacheRoot;
    private readonly WindowStateStore _windowStateStore;
    private readonly MarkdownReaderController _markdownReader;
    private readonly ReaderAssistantService _readerAssistant;
    private readonly OcrCoordinator _ocrCoordinator;
    private readonly LocalModelManager _localModels;
    private readonly LocalTextTranslationService _localTranslator;
    private readonly TranslationUsageStore _translationUsage;
    private readonly UpdateService _updateService;
    private readonly CancellationTokenSource _updateCheckCancellation = new();
    private readonly LibraryAnalysisQueue _libraryAnalysisQueue;
    private readonly LibraryAnalysisOrchestrator _libraryAnalysisOrchestrator;
    private ObservableCollection<ThumbnailItem> _thumbnailItems = [];
    private readonly Dictionary<uint, CancellationTokenSource> _thumbnailRequests = [];
    private readonly HashSet<uint> _visibleThumbnailPages = [];
    private readonly LinkedList<uint> _thumbnailLru = [];
    private readonly DispatcherTimer _pageNumberDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };
    private readonly DispatcherTimer _librarySearchDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };
    private readonly DispatcherTimer _readingProgressTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(1500)
    };
    private uint? _pendingProgressPageIndex;
    private readonly OcrEngineProvider _ocrEngineProvider = new();
    private TranslationProfile _onlineProfile = TranslationProfile.Unconfigured;
    private TranslationExecutionMode _translationMode = TranslationExecutionMode.Online;
    private string _assistantModelSource = "follow";
    private bool _libraryAutoAnalysisEnabled = true;
    private string _libraryAnalysisSource = "local";
    private string _translationDomainPreference = "auto";
    private string _documentDomain = string.Empty;
    private readonly SyncLatch _quickPickerLatch = new();
    private readonly SyncLatch _translationModeLatch = new();
    private readonly LibraryRepository _libraryRepository;
    private readonly LibraryIngestionService _libraryIngestion;
    private readonly LibraryQueryService _libraryQuery;
    private readonly LibraryClassificationService _libraryClassification;
    private readonly LibraryMigrationService _libraryMigration;
    private readonly LibraryThumbnailService _libraryThumbnails = new();
    private readonly Task _libraryInitialization;
    private List<LibraryDocument> _libraryEntries = [];
    private List<LibraryFolder> _libraryFolders = [];
    private LibraryNavigationItem? _selectedLibraryNavigation;
    private string? _currentLibraryDocumentId;
    private bool _compactLibraryNavigationOpen;
    private bool _compactLibraryDetailsOpen;
    private CancellationTokenSource? _pageWorkCancellation;
    private CancellationTokenSource? _thumbnailWork;
    private int _renderVersion;
    private int _currentThumbnailIndex = -1;
    private readonly SyncLatch _pageNumberLatch = new();
    private readonly SyncLatch _thumbnailSyncLatch = new();
    private uint? _pendingPageIndex;
    private double _currentPageAspectRatio;
    private bool _fitToHeight = true;
    private readonly SyncLatch _fitLatch = new();
    private CancellationTokenSource? _assistantWork;
    private string _documentKey = string.Empty;
    private string _currentTranslationMarkdown = string.Empty;
    private string _currentSourceText = string.Empty;
    private bool _wideThumbnailPaneOpen = true;
    private readonly SyncLatch _thumbnailToggleLatch = new();
    private bool? _isWideReaderLayout;
    // XAML can raise SizeChanged/Toggled/SelectionChanged while LoadComponent is
    // still constructing the visual tree. Services are assigned only after
    // InitializeComponent returns, so handlers must not enter runtime logic yet.
    private bool _isInitializing = true;

    private TranslationProfile ActiveTranslationProfile => _translationMode == TranslationExecutionMode.Online
        ? _onlineProfile
        : _localTranslator.CreateProfile(_onlineProfile.Settings.TargetLanguage);
    private ReaderViewMode _readerViewMode = ReaderViewMode.Translation;

    public MainWindow()
    {
        InitializeComponent();

        // TRANSREADER_DATA_ROOT is intentionally supported for automated demos/tests and
        // portable troubleshooting. Normal users always fall back to LocalAppData.
        var localRoot = Environment.GetEnvironmentVariable("TRANSREADER_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(localRoot))
        {
            localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TransReader");
        }
        localRoot = Path.GetFullPath(localRoot);

        _markdownReader = new MarkdownReaderController(
            TranslationWebView,
            DispatcherQueue,
            Path.Combine(localRoot, "webview2"));
        _markdownReader.ReaderMessageReceived += MarkdownReader_ReaderMessageReceived;
        _ = InitializeMarkdownReaderAsync();

        // VirtualKey lacks an entry for VK_OEM_COMMA (0xBC); register Ctrl+, in code.
        var settingsAccelerator = new KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)0xBC,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        settingsAccelerator.Invoked += SettingsAccelerator_Invoked;
        RootGrid.KeyboardAccelerators.Add(settingsAccelerator);

        // ThemeResource cannot be assigned to a plain CLR object in markup,
        // so the converter brushes are set from code and track theme changes.
        UpdateThumbnailHighlightBrushes();
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateThumbnailHighlightBrushes();
            _ = _markdownReader.SetThemeAsync(RootGrid.ActualTheme);
        };

        ConfigureWindowChrome();

        _updateService = new UpdateService(localRoot);
        _settingsStore = new TranslationSettingsStore(Path.Combine(localRoot, "settings.json"));
        _legacyRecentFilesPath = Path.Combine(localRoot, "recent-files.json");
        _cacheRoot = Path.Combine(localRoot, "cache");
        _windowStateStore = new WindowStateStore(Path.Combine(localRoot, "window-state.json"));
        _localModels = new LocalModelManager(Path.Combine(localRoot, "local-ai"));
        _localModels.StatusChanged += LocalModels_StatusChanged;
        _ocrCoordinator = new OcrCoordinator(_ocrEngineProvider);
        var translator = new OpenAiCompatibleTranslator();
        _localTranslator = new LocalTextTranslationService(_localModels, translator);
        _translationUsage = new TranslationUsageStore(Path.Combine(_cacheRoot, "usage"));
        var libraryRoot = Path.Combine(localRoot, "library");
        _libraryRepository = new LibraryRepository(Path.Combine(libraryRoot, "library.db"));
        _libraryIngestion = new LibraryIngestionService(libraryRoot, _libraryRepository);
        _libraryQuery = new LibraryQueryService(_libraryRepository);
        _libraryClassification = new LibraryClassificationService(
            new PageOcrCache(Path.Combine(_cacheRoot, "ocr")), _ocrCoordinator, _localModels, translator);
        _libraryClassification.ResolveOnlineProfileAsync = ResolveLibraryAnalysisProfileAsync;
        _libraryMigration = new LibraryMigrationService(_libraryRepository, _libraryIngestion);
        _libraryAnalysisOrchestrator = new LibraryAnalysisOrchestrator(_libraryRepository, _libraryClassification);
        _libraryAnalysisQueue = new LibraryAnalysisQueue(_libraryAnalysisOrchestrator.AnalyzeAsync);
        _libraryAnalysisOrchestrator.BusyChanged += text => DispatcherQueue.TryEnqueue(() => LibraryBusyText.Text = text);
        _libraryAnalysisOrchestrator.ReenqueueRequested += (documentId, manual, delay) =>
            _ = _libraryAnalysisQueue.ReenqueueAfterAsync(documentId, manual, delay);
        _libraryAnalysisOrchestrator.Completed += () => DispatcherQueue.TryEnqueue(async () =>
        {
            LibraryBusyText.Text = string.Empty;
            if (LibraryView.Visibility == Visibility.Visible) await RefreshLibraryAsync();
        });
        _libraryInitialization = InitializeLibraryAsync(localRoot);
        _processing = new PageProcessingService(
            new PageOcrCache(Path.Combine(_cacheRoot, "ocr")),
            new PageTranslationCache(Path.Combine(_cacheRoot, "translation")),
            _ocrCoordinator,
            translator,
            _localTranslator,
            _localModels,
            _translationUsage);
        _readerAssistant = new ReaderAssistantService(
            new ReaderAssistantHistoryStore(Path.Combine(_cacheRoot, "assistant")), translator);
        _ = _ocrEngineProvider.WarmupAsync();
        _ = SweepCachesAsync();

        ThumbnailList.ItemsSource = _thumbnailItems;
        _pageNumberDebounceTimer.Tick += PageNumberDebounceTimer_Tick;
        _librarySearchDebounceTimer.Tick += LibrarySearchDebounceTimer_Tick;
        _readingProgressTimer.Tick += ReadingProgressTimer_Tick;
        RestoreWindowState();

        Closed += MainWindow_Closed;
        _isInitializing = false;
        ApplyReaderLayout(RootGrid.ActualWidth);
        _ = InitializeSettingsAsync();
        _ = ObserveOcrWarmupAsync();
        _ = CheckForUpdatesOnStartupAsync();
    }

    private void UpdateThumbnailHighlightBrushes()
    {
        var converter = (BoolToBrushConverter)RootGrid.Resources["ThumbnailBorderConverter"];
        converter.TrueBrush = Application.Current.Resources.TryGetValue(
            "TransReaderBrandBrush", out var brush) && brush is Brush themed
            ? themed
            : new SolidColorBrush(Colors.DodgerBlue);
        converter.FalseBrush = new SolidColorBrush(Colors.Transparent);
    }

    private void ConfigureWindowChrome()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x19, 0x80, 0x80, 0x80);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80);

        UpdateTitleBarInsets();
        AppWindow.Changed += (_, _) => UpdateTitleBarInsets();
    }

    private void UpdateTitleBarInsets()
    {
        // Keep the command bar clear of the system caption buttons (min/max/close).
        var titleBar = AppWindow.TitleBar;
        var padding = new Thickness(
            titleBar.LeftInset > 0 ? titleBar.LeftInset : 12,
            0,
            titleBar.RightInset,
            0);
        // Avoid redundant layout passes; AppWindow.Changed fires often.
        if (AppTitleBar.Padding != padding)
        {
            AppTitleBar.Padding = padding;
        }
    }

    private void RestoreWindowState()
    {
        var state = _windowStateStore.Load();
        if (state is null)
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
            return;
        }

        AppWindow.Resize(new Windows.Graphics.SizeInt32(state.Width, state.Height));
        if (state.X >= 0 && state.Y >= 0 && IsOnAnyDisplay(state.X, state.Y, state.Width, state.Height))
        {
            AppWindow.Move(new Windows.Graphics.PointInt32(state.X, state.Y));
        }
    }

    /// <summary>拔掉显示器后保存的窗口位置可能落在不可见区域：仅当与某个显示区有实际重叠时才恢复位置。</summary>
    private static bool IsOnAnyDisplay(int x, int y, int width, int height)
    {
        try
        {
            foreach (var display in DisplayArea.FindAll())
            {
                var area = display.WorkArea;
                var overlapX = Math.Max(0, Math.Min(x + width, area.X + area.Width) - Math.Max(x, area.X));
                var overlapY = Math.Max(0, Math.Min(y + height, area.Y + area.Height) - Math.Max(y, area.Y));
                if (overlapX >= 64 && overlapY >= 32) return true;
            }
        }
        catch
        {
            return true; // 查询失败时保持旧行为（恢复位置），避免误伤。
        }
        return false;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        AppLog.Info("窗口关闭，应用正常退出");
        _pageWorkCancellation?.Cancel();
        _pageWorkCancellation?.Dispose();
        _thumbnailWork?.Cancel();
        _thumbnailWork?.Dispose();
        _updateCheckCancellation.Cancel();
        _updateCheckCancellation.Dispose();
        CancelThumbnailRequests();
        _pageNumberDebounceTimer.Stop();
        _librarySearchDebounceTimer.Stop();
        // 关闭前尽力冲刷一次合流中的阅读进度（进程退出阶段任务不保证完成，SQLite 写入通常毫秒级）。
        _readingProgressTimer.Stop();
        _ = FlushPendingReadingProgressAsync();

        var position = AppWindow.Position;
        var size = AppWindow.Size;
        _windowStateStore.Save(position.X, position.Y, size.Width, size.Height);

        _processing.Dispose();
        _libraryAnalysisQueue.Dispose();
        _localModels.Dispose();
        _ocrCoordinator.Dispose();
        _assistantWork?.Cancel();
        _assistantWork?.Dispose();
        _readerAssistant.Dispose();
        _markdownReader.Dispose();
        _ = AppLog.ShutdownAsync();
        _ocrEngineProvider.Dispose();
    }

    private async Task InitializeMarkdownReaderAsync()
    {
        try
        {
            await _markdownReader.InitializeAsync();
            await _markdownReader.SetThemeAsync(RootGrid.ActualTheme);
        }
        catch (Exception ex)
        {
            AppLog.Error("Markdown 阅读器初始化", ex);
            ShowPageError("译文阅读器初始化失败", ex.Message);
        }
    }

    internal void ShowUnhandledError(Exception exception)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowPageError("发生意外错误", exception.Message);
            StatusText.Text = "发生意外错误";
        });
    }

    public async Task OpenPdfFromCommandLineAsync(string path)
    {
        await OpenPdfFileAsync(path);
        // Optional second argument: 1-based page to open (used by automated tests).
        if (Environment.GetCommandLineArgs() is { Length: > 2 } commandLine &&
            uint.TryParse(commandLine[2], out var pageNumber) &&
            pageNumber >= 1 &&
            pageNumber <= _session.PageCount)
        {
            await NavigateToPageAsync(pageNumber - 1);
        }
    }

    private async Task ObserveOcrWarmupAsync()
    {
        try
        {
            EngineText.Text = "PaddleOCR · 预热中";
            await _ocrEngineProvider.EnsureAsync();
            EngineText.Text = "PaddleOCR · oneDNN";
        }
        catch (Exception ex)
        {
            EngineText.Text = "OCR 初始化失败";
            StatusText.Text = $"OCR 预热失败：{ex.Message}";
        }
    }

    private async Task InitializeSettingsAsync()
    {
        try
        {
            await ReloadSettingsAsync();
            // Diagnostic launches can exercise long-PDF navigation without making
            // a real API request or altering the user's saved settings.
            if (Environment.GetEnvironmentVariable("TRANSREADER_DISABLE_TRANSLATION") == "1")
            {
                _onlineProfile = _onlineProfile with { ApiKey = string.Empty };
                UpdateTranslationConfigurationUi();
            }
            // 启动时若有待分析文献且整理来源可用（如在线 follow），自动入队继续分析。
            await EnqueuePendingLibraryAnalysesAsync();
        }
        catch (Exception ex)
        {
            // 火忘初始化：异常若未捕获会成为未观察任务，覆盖崩溃日志且 UI 状态不一致。
            AppLog.Error("加载翻译设置", ex);
            StatusText.Text = $"设置加载失败：{ex.Message}（已回退默认配置）";
        }
    }

    private async Task SweepCachesAsync()
    {
        // 后台低优先级清扫：让首屏与库初始化先跑；之后每 30 分钟一轮，长会话不再只扫启动一次。
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            while (true)
            {
                await Task.WhenAll(
                    CacheSweeper.SweepAsync(Path.Combine(_cacheRoot, "ocr"), CacheSweeper.DefaultMaxBytes),
                    CacheSweeper.SweepAsync(Path.Combine(_cacheRoot, "translation"), CacheSweeper.DefaultMaxBytes)).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(30)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("缓存清扫", ex);
        }
    }

    private async Task InitializeLibraryAsync(string localRoot)
    {
        try
        {
            await _libraryRepository.InitializeAsync();
            await _libraryRepository.ResetInterruptedAnalysesAsync();
            await _libraryRepository.NormalizeLegacyUnclassifiedFolderAsync();
            await _libraryRepository.PurgeExpiredTrashAsync(TimeSpan.FromDays(30));
            _ = Task.Run(async () =>
            {
                try
                {
                    await _libraryMigration.MigrateAsync(
                        Path.Combine(localRoot, "library.json"),
                        _legacyRecentFilesPath);
                    await _libraryRepository.NormalizeLegacyUnclassifiedFolderAsync();
                    await EnqueuePendingLibraryAnalysesAsync();
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (LibraryView.Visibility == Visibility.Visible) await RefreshLibraryAsync();
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Error("迁移旧文献库", ex);
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化文献库", ex);
            DispatcherQueue.TryEnqueue(() => StatusText.Text = $"文献库初始化失败：{ex.Message}");
        }
    }

    // ---------- Document open ----------

    private async void OpenButton_Click(object sender, RoutedEventArgs e) => await ShowOpenPickerAsync();

    private async void OpenSplitButton_Click(SplitButton sender, SplitButtonClickEventArgs args) =>
        await ShowOpenPickerAsync();

    private async void OpenAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldIgnorePageAccelerator())
        {
            return;
        }
        args.Handled = true;
        await ShowOpenPickerAsync();
    }

    private async Task ShowOpenPickerAsync()
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await OpenPdfFileAsync(file.Path);
        }
    }

    private async Task OpenPdfFileAsync(string path)
    {
        try
        {
            await _libraryInitialization;
            AppLog.Info("打开 PDF");
            PageErrorBar.IsOpen = false;
            StatusText.Text = "正在归档并打开 PDF…";
            var sourceFile = await StorageFile.GetFileFromPathAsync(path);
            var sourceDocument = await PdfDocument.LoadFromFileAsync(sourceFile);
            if (sourceDocument.PageCount == 0) throw new InvalidDataException("PDF 没有可读取的页面。");
            var imported = await _libraryIngestion.EnsureImportedAsync(sourceFile.Path, sourceDocument.PageCount);
            var resumePage = Math.Min(imported.Document.LastPageIndex, sourceDocument.PageCount - 1);
            var file = await StorageFile.GetFileFromPathAsync(imported.Document.ManagedPath);
            var document = await PdfDocument.LoadFromFileAsync(file);
            _session.Open(file.Path, document.PageCount);
            if (resumePage > 0) _session.MoveTo(resumePage);
            var documentKey = imported.Document.ContentHash;
            _currentLibraryDocumentId = imported.Document.Id;
            // 类别感知翻译：记录当前文档的 AI 分析领域（未分析为 ""，手动偏好可覆盖）。
            _documentDomain = imported.Document.Domain;
            UpdateDomainHint();
            await _libraryRepository.RecordOpenedAsync(imported.Document.Id, document.PageCount);
            _readerAssistant.CloseDocument();
            _processing.OpenDocument(document, documentKey);
            _documentKey = documentKey;
            _ = _processing.PruneDocumentCacheAsync(documentKey, ActiveTranslationProfile.Settings);
            await _readerAssistant.OpenDocumentAsync(documentKey);
            await _markdownReader.SetTopicsAsync(_readerAssistant.Topics);
            _processing.PrepareForNavigation(resumePage);
            DocumentNameText.Text = imported.Document.Title;
            WelcomeOverlay.Visibility = Visibility.Collapsed;
            ShowLibraryView(false);
            ThumbnailToggle.IsEnabled = true;
            ApplyReaderLayout(RootGrid.ActualWidth);
            if (RootGrid.ActualWidth >= 960)
            {
                SetThumbnailPaneOpen(_wideThumbnailPaneOpen, updateWidePreference: false);
            }
            RetryTranslationButton.IsEnabled = true;
            FitHeightButton.IsEnabled = true;
            ReaderAssistantButton.IsEnabled = true;
            LoadThumbnails();
            AppLog.Info($"PDF 已打开，共 {_session.PageCount} 页");
            await RenderCurrentPageAsync();
            if (_libraryAutoAnalysisEnabled)
            {
                _libraryAnalysisQueue.Enqueue(imported.Document.Id, manual: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("打开 PDF", ex);
            StatusText.Text = $"无法打开 PDF：{ex.Message}";
            ShowPageError("无法打开 PDF", ex.Message);
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "打开 PDF";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsGlyphVisible = false; // 隐藏跟随光标的 drop-action 箭头徽标
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items
            .OfType<StorageFile>()
            .FirstOrDefault(item => item.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (file is not null)
        {
            await OpenPdfFileAsync(file.Path);
        }
    }

    private async void RecentFilesFlyout_Opening(object sender, object e)
    {
        RecentFilesFlyout.Items.Clear();
        await _libraryInitialization;
        var recents = (await _libraryQuery.SearchAsync(new LibraryQuery(
            Navigation: LibraryNavigationKind.All, Sort: LibrarySortOrder.LastOpened)))
            .Where(document => document.LastOpenedAt is not null).Take(10).ToList();
        if (recents.Count == 0)
        {
            RecentFilesFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "暂无最近文件",
                IsEnabled = false
            });
            return;
        }

        foreach (var recent in recents)
        {
            var openItem = new MenuFlyoutItem
            {
                Text = "打开",
                IsEnabled = File.Exists(recent.ManagedPath)
            };
            ToolTipService.SetToolTip(openItem, recent.ManagedPath);
            openItem.Click += async (_, _) => await OpenPdfFileAsync(recent.ManagedPath);

            var removeItem = new MenuFlyoutItem { Text = "清除此条打开历史" };
            removeItem.Click += async (_, _) =>
            {
                await _libraryRepository.ClearHistoryAsync(recent.Id);
                StatusText.Text = $"已清除“{recent.Title}”的打开历史";
            };

            RecentFilesFlyout.Items.Add(new MenuFlyoutSubItem
            {
                Text = recent.Title,
                Items = { openItem, removeItem }
            });
        }

        RecentFilesFlyout.Items.Add(new MenuFlyoutSeparator());
        var clearAllItem = new MenuFlyoutItem { Text = "清空全部打开历史" };
        clearAllItem.Click += async (_, _) => await ClearAllRecentEntriesAsync();
        RecentFilesFlyout.Items.Add(clearAllItem);
    }

    private async Task ClearAllRecentEntriesAsync()
    {
        var result = await ShowCleanupConfirmationAsync(
            "清空全部打开历史？",
            "这会重置所有文献的打开时间、次数和阅读位置。文献、托管 PDF、OCR 与翻译缓存都不会删除。");
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _libraryRepository.ClearHistoryAsync();
            StatusText.Text = "已清空全部 PDF 打开历史";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("清空全部 PDF 打开历史", ex);
            ShowPageError("清理失败", ex.Message);
        }
    }

    private async Task<ContentDialogResult> ShowCleanupConfirmationAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync();
    }

    // ---------- Thumbnails ----------

    private void LoadThumbnails()
    {
        _thumbnailWork?.Cancel();
        _thumbnailWork?.Dispose();
        CancelThumbnailRequests();
        _thumbnailLru.Clear();
        _visibleThumbnailPages.Clear();
        _currentThumbnailIndex = -1;
        // 千页文档逐个 Add 会触发千次 UI 线程 CollectionChanged；整体替换只通知一次。
        _thumbnailItems = _processing.HasDocument
            ? new ObservableCollection<ThumbnailItem>(
                Enumerable.Range(0, (int)_session.PageCount).Select(index => new ThumbnailItem((uint)index)))
            : [];
        ThumbnailList.ItemsSource = _thumbnailItems;
        if (!_processing.HasDocument)
        {
            ThumbnailCountText.Text = string.Empty;
            return;
        }

        ThumbnailCountText.Text = $"共 {_session.PageCount} 页";
        _thumbnailWork = new CancellationTokenSource();
    }

    private async void ThumbnailList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ThumbnailItem item)
        {
            return;
        }
        if (args.InRecycleQueue)
        {
            _visibleThumbnailPages.Remove(item.PageIndex);
            item.NextGeneration();
            CancelThumbnailRequest(item.PageIndex);
            if (item.Image is null)
            {
                item.State = ThumbnailLoadState.Pending;
            }
            return;
        }
        _visibleThumbnailPages.Add(item.PageIndex);
        if (item.Image is null)
        {
            await LoadThumbnailAsync(item);
        }
    }

    private async Task LoadThumbnailAsync(ThumbnailItem item, int retryRound = 0)
    {
        const int maxRetryRounds = 3;
        if (_thumbnailWork is null || _thumbnailWork.IsCancellationRequested)
        {
            return;
        }

        if (_thumbnailRequests.TryGetValue(item.PageIndex, out var existing))
        {
            if (!existing.IsCancellationRequested)
            {
                return;
            }
            _thumbnailRequests.Remove(item.PageIndex);
        }

        var work = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailWork.Token);
        _thumbnailRequests[item.PageIndex] = work;
        var generation = item.NextGeneration();
        item.State = ThumbnailLoadState.Loading;
        byte[]? encodedImage = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    encodedImage = await _processing.RenderThumbnailAsync(item.PageIndex, 280, work.Token);
                    break;
                }
                catch when (attempt == 0 && !work.IsCancellationRequested)
                {
                    await Task.Delay(80, work.Token);
                }
            }
            if (encodedImage is null || work.IsCancellationRequested ||
                item.PageIndex >= _thumbnailItems.Count || !item.IsGenerationCurrent(generation))
            {
                return;
            }
            item.Image = await CreateBitmapImageAsync(encodedImage);
            item.State = ThumbnailLoadState.Loaded;
            TouchThumbnailCache(item.PageIndex);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error($"缩略图失败: 第 {item.PageIndex + 1} 页", ex);
            if (item.IsGenerationCurrent(generation))
            {
                item.State = ThumbnailLoadState.Failed;
            }
        }
        finally
        {
            if (_thumbnailRequests.TryGetValue(item.PageIndex, out var current) &&
                ReferenceEquals(current, work))
            {
                _thumbnailRequests.Remove(item.PageIndex);
            }
            work.Dispose();
            if (item.Image is null && item.State != ThumbnailLoadState.Failed &&
                _visibleThumbnailPages.Contains(item.PageIndex) &&
                item.IsGenerationCurrent(generation) && _thumbnailWork?.IsCancellationRequested == false)
            {
                // Bounded retries only; an endlessly re-queued load leaves a
                // permanently blank slot and burns CPU in the background.
                if (retryRound + 1 >= maxRetryRounds)
                {
                    item.State = ThumbnailLoadState.Failed;
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() => _ = LoadThumbnailAsync(item, retryRound + 1));
                }
            }
        }
    }

    private void ThumbnailRetry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThumbnailItem item)
        {
            return;
        }
        item.State = ThumbnailLoadState.Pending;
        _ = LoadThumbnailAsync(item);
    }

    private void TouchThumbnailCache(uint pageIndex)
    {
        _thumbnailLru.Remove(pageIndex);
        _thumbnailLru.AddLast(pageIndex);
        var inspected = 0;
        while (_thumbnailLru.Count > 24 && inspected < _thumbnailLru.Count)
        {
            var oldest = _thumbnailLru.First!.Value;
            _thumbnailLru.RemoveFirst();
            if (oldest == _session.CurrentPageIndex || _visibleThumbnailPages.Contains(oldest))
            {
                _thumbnailLru.AddLast(oldest);
                inspected++;
                continue;
            }
            if (oldest < _thumbnailItems.Count)
            {
                _thumbnailItems[(int)oldest].Image = null;
                _thumbnailItems[(int)oldest].State = ThumbnailLoadState.Pending;
            }
            inspected = 0;
        }
    }

    private void CancelThumbnailRequest(uint pageIndex)
    {
        if (_thumbnailRequests.TryGetValue(pageIndex, out var work))
        {
            work.Cancel();
        }
    }

    private void CancelThumbnailRequests()
    {
        foreach (var work in _thumbnailRequests.Values)
        {
            work.Cancel();
        }
    }

    private void SyncThumbnailSelection(uint pageIndex)
    {
        using (_thumbnailSyncLatch.Enter())
        {
            if (_currentThumbnailIndex >= 0 && _currentThumbnailIndex < _thumbnailItems.Count)
            {
                _thumbnailItems[_currentThumbnailIndex].IsCurrent = false;
            }
            var index = (int)pageIndex;
            if (index >= 0 && index < _thumbnailItems.Count)
            {
                _thumbnailItems[index].IsCurrent = true;
                ThumbnailList.SelectedIndex = index;
                ThumbnailList.ScrollIntoView(_thumbnailItems[index]);
                _currentThumbnailIndex = index;
            }
        }
    }

    private async void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_thumbnailSyncLatch.IsHeld || ThumbnailList.SelectedItem is not ThumbnailItem item)
        {
            return;
        }
        await NavigateToPageAsync(item.PageIndex);
        if (RootGrid.ActualWidth < 960)
        {
            SetThumbnailPaneOpen(false, updateWidePreference: false);
        }
    }

    private void ThumbnailToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // IsChecked is applied during XAML load, before the reader services exist.
        if (_thumbnailToggleLatch.IsHeld || ReaderSplitView is null || LibraryView is null ||
            LibraryView.Visibility == Visibility.Visible || _processing is null || !_processing.HasDocument)
        {
            return;
        }
        SetThumbnailPaneOpen(ThumbnailToggle.IsChecked == true, RootGrid.ActualWidth >= 960);
    }

    private void ReaderSplitView_PaneClosed(SplitView sender, object args)
    {
        if (RootGrid.ActualWidth < 960)
        {
            SetThumbnailToggle(false);
        }
    }

    private void SetThumbnailPaneOpen(bool isOpen, bool updateWidePreference)
    {
        if (updateWidePreference)
        {
            _wideThumbnailPaneOpen = isOpen;
        }
        ReaderSplitView.IsPaneOpen = isOpen;
        SetThumbnailToggle(isOpen);
    }

    private void SetThumbnailToggle(bool isChecked)
    {
        using (_thumbnailToggleLatch.Enter())
        {
            ThumbnailToggle.IsChecked = isChecked;
            ThumbnailToggleIcon.Glyph = isChecked ? "\uE89F" : "\uE8A0";
        }
    }

    private void ApplyReaderLayout(double width)
    {
        var isWide = width >= 960;
        ReaderSplitView.DisplayMode = isWide ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        ReaderSplitView.OpenPaneLength = 172;

        if (_isWideReaderLayout != isWide)
        {
            _isWideReaderLayout = isWide;
            var canShowPane = LibraryView.Visibility != Visibility.Visible &&
                              _processing is not null && _processing.HasDocument;
            SetThumbnailPaneOpen(canShowPane && isWide && _wideThumbnailPaneOpen,
                updateWidePreference: false);
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInitializing || ReaderSplitView is null)
        {
            return;
        }
        ApplyReaderLayout(e.NewSize.Width);
        var compact = e.NewSize.Width < 720;
        PreviousButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SourceTextActionLabel.Visibility = e.NewSize.Width < 960 ? Visibility.Collapsed : Visibility.Visible;
        StatusDetailsPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ApplyLibraryResponsiveLayout(e.NewSize.Width);
    }

    private void LibraryCompactNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        _compactLibraryNavigationOpen = !_compactLibraryNavigationOpen;
        if (_compactLibraryNavigationOpen) _compactLibraryDetailsOpen = false;
        ApplyLibraryResponsiveLayout(RootGrid.ActualWidth);
    }

    private void LibraryCompactDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        _compactLibraryDetailsOpen = !_compactLibraryDetailsOpen;
        if (_compactLibraryDetailsOpen) _compactLibraryNavigationOpen = false;
        ApplyLibraryResponsiveLayout(RootGrid.ActualWidth);
    }

    private void ApplyLibraryResponsiveLayout(double width)
    {
        if (LibraryDetailsColumn is null) return;
        var libraryCompact = width < 1100;
        var navigationCompact = width < 820;
        LibraryDetailsColumn.Width = libraryCompact
            ? (_compactLibraryDetailsOpen ? new GridLength(Math.Min(320, width * .78)) : new GridLength(0))
            : new GridLength(320);
        LibraryDetailsPane.Visibility = !libraryCompact || _compactLibraryDetailsOpen
            ? Visibility.Visible : Visibility.Collapsed;
        LibraryNavigationColumn.Width = navigationCompact
            ? (_compactLibraryNavigationOpen ? new GridLength(Math.Min(220, width * .65)) : new GridLength(0))
            : new GridLength(220);
        LibraryNavigationPane.Visibility = !navigationCompact || _compactLibraryNavigationOpen
            ? Visibility.Visible : Visibility.Collapsed;
        LibraryCompactDetailsButton.Visibility = libraryCompact ? Visibility.Visible : Visibility.Collapsed;
        LibraryCompactNavigationButton.Visibility = navigationCompact ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void FitHeightButton_Click(object sender, RoutedEventArgs e)
    {
        _fitToHeight = true;
        await FitCurrentPageToHeightAsync();
    }

    private void SourceTextToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (SourceTextDrawer is null || SourceTextChevron is null)
        {
            return;
        }
        var expanded = SourceTextToggle.IsChecked == true;
        SourceTextDrawer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SourceTextChevron.Glyph = expanded ? "\uE70E" : "\uE70D";
    }

    private async void OriginalPageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isInitializing && _fitToHeight)
        {
            await FitCurrentPageToHeightAsync();
        }
    }

    private void OriginalPageScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_isInitializing && !_fitLatch.IsHeld && Math.Abs(OriginalPageScrollViewer.ZoomFactor - 1f) > 0.01f)
        {
            _fitToHeight = false;
        }
    }

    private void ReaderSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var availableWidth = OriginalColumn.ActualWidth + TranslationColumn.ActualWidth;
        if (availableWidth <= 0)
        {
            return;
        }

        const double minimumOriginalWidth = 280;
        const double minimumTranslationWidth = 380;
        var maximumOriginalWidth = Math.Max(minimumOriginalWidth, availableWidth - minimumTranslationWidth);
        var originalWidth = Math.Clamp(
            OriginalColumn.ActualWidth + e.HorizontalChange,
            minimumOriginalWidth,
            maximumOriginalWidth);
        OriginalColumn.Width = new GridLength(originalWidth, GridUnitType.Pixel);
        TranslationColumn.Width = new GridLength(
            Math.Max(minimumTranslationWidth, availableWidth - originalWidth),
            GridUnitType.Pixel);
    }

    private async Task FitCurrentPageToHeightAsync()
    {
        if (!_processing.HasDocument || OriginalPageImage.Source is null ||
            _currentPageAspectRatio <= 0)
        {
            return;
        }
        await Task.Yield();
        var viewportHeight = OriginalPageScrollViewer.ViewportHeight > 0
            ? OriginalPageScrollViewer.ViewportHeight
            : OriginalPageScrollViewer.ActualHeight;
        if (viewportHeight <= 48)
        {
            return;
        }
        var pageHeight = Math.Max(1, viewportHeight - 48);
        var pageWidth = pageHeight * _currentPageAspectRatio;
        var viewportWidth = OriginalPageScrollViewer.ViewportWidth > 0
            ? OriginalPageScrollViewer.ViewportWidth
            : OriginalPageScrollViewer.ActualWidth;
        OriginalPageImage.Height = pageHeight;
        OriginalPageImage.Width = pageWidth;
        OriginalPageBorder.Height = pageHeight + 16;
        OriginalPageBorder.Width = pageWidth + 16;
        OriginalPageHost.Height = viewportHeight;
        // 宿主只包裹内容宽度（配合 XAML 的 HorizontalAlignment=Center 居中）：
        // 不得按 viewport 撑满，否则原页栏会无限吃宽、把译文栏挤出窗口。
        OriginalPageHost.Width = pageWidth + 48;
        using (_fitLatch.Enter())
        {
            OriginalPageScrollViewer.ChangeView(0, 0, 1, true);
        }
    }

    private static async Task<BitmapImage> CreateBitmapImageAsync(byte[] encodedImage)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(encodedImage);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        stream.Seek(0);
        var source = new BitmapImage();
        await source.SetSourceAsync(stream);
        return source;
    }

    // ---------- Navigation ----------

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.CurrentPageIndex > 0)
        {
            await NavigateToPageAsync(_session.CurrentPageIndex - 1);
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.CurrentPageIndex + 1 < _session.PageCount)
        {
            await NavigateToPageAsync(_session.CurrentPageIndex + 1);
        }
    }

    private async Task NavigateToPageAsync(uint pageIndex)
    {
        if (!_processing.HasDocument || pageIndex >= _session.PageCount)
        {
            return;
        }
        if (pageIndex == _session.CurrentPageIndex || _session.MoveTo(pageIndex))
        {
            CancelThumbnailRequests();
            _processing.PrepareForNavigation(pageIndex);
            // 乐观 UI：页码与翻页按钮立即响应，页面渲染异步跟上（渲染完成后会再次幂等更新）。
            UpdateNavigationUi(pageIndex);
            await RenderCurrentPageAsync();
            QueueReadingProgressUpdate(pageIndex);
        }
    }

    /// <summary>阅读进度落盘合流：连续翻页只在停顿 1.5 秒后写一次 SQLite，不再每页一写。</summary>
    private void QueueReadingProgressUpdate(uint pageIndex)
    {
        if (_currentLibraryDocumentId is null) return;
        _pendingProgressPageIndex = pageIndex;
        _readingProgressTimer.Stop();
        _readingProgressTimer.Start();
    }

    private async void ReadingProgressTimer_Tick(object? sender, object e)
    {
        _readingProgressTimer.Stop();
        await FlushPendingReadingProgressAsync();
    }

    private async Task FlushPendingReadingProgressAsync()
    {
        if (_currentLibraryDocumentId is null || _pendingProgressPageIndex is not uint pageIndex) return;
        try
        {
            await _libraryRepository.UpdateReadingProgressAsync(_currentLibraryDocumentId, pageIndex, _session.PageCount);
        }
        catch (Exception ex)
        {
            AppLog.Error("保存阅读进度", ex);
        }
    }

    private void PageNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // ValueChanged can fire during XAML load, before _processing is assigned.
        if (_processing is null || _pageNumberLatch.IsHeld || !_processing.HasDocument || double.IsNaN(args.NewValue))
        {
            return;
        }
        var pageNumber = (uint)Math.Clamp(
            Math.Round(args.NewValue),
            1,
            _session.PageCount);
        _pendingPageIndex = pageNumber - 1;
        _pageNumberDebounceTimer.Stop();
        _pageNumberDebounceTimer.Start();
    }

    private async void PageNumberDebounceTimer_Tick(object? sender, object e)
    {
        _pageNumberDebounceTimer.Stop();
        await CommitPendingPageNumberAsync();
    }

    private async void PageNumberBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _pageNumberDebounceTimer.Stop();
            await CommitPendingPageNumberAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _pageNumberDebounceTimer.Stop();
            _pendingPageIndex = null;
            UpdateNavigationUi(_session.CurrentPageIndex);
        }
    }

    private async void PageNumberBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _pageNumberDebounceTimer.Stop();
        await CommitPendingPageNumberAsync();
    }

    private async Task CommitPendingPageNumberAsync()
    {
        if (_pendingPageIndex is not uint pageIndex)
        {
            return;
        }
        _pendingPageIndex = null;
        await NavigateToPageAsync(pageIndex);
    }

    private async void PreviousPageAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldIgnorePageAccelerator())
        {
            return;
        }
        args.Handled = true;
        if (_session.CurrentPageIndex > 0)
        {
            await NavigateToPageAsync(_session.CurrentPageIndex - 1);
        }
    }

    private async void NextPageAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldIgnorePageAccelerator())
        {
            return;
        }
        args.Handled = true;
        if (_session.CurrentPageIndex + 1 < _session.PageCount)
        {
            await NavigateToPageAsync(_session.CurrentPageIndex + 1);
        }
    }

    private async void FirstPageAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldIgnorePageAccelerator())
        {
            return;
        }
        args.Handled = true;
        await NavigateToPageAsync(0);
    }

    private async void LastPageAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldIgnorePageAccelerator())
        {
            return;
        }
        args.Handled = true;
        if (_session.PageCount > 0)
        {
            await NavigateToPageAsync(_session.PageCount - 1);
        }
    }

    private async void RetryAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldIgnorePageAccelerator() || !_processing.HasDocument)
        {
            return;
        }
        args.Handled = true;
        await RenderCurrentPageAsync(forceTranslation: true);
    }

    private async void SettingsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldIgnorePageAccelerator())
        {
            return;
        }
        args.Handled = true;
        ShowSettingsView(true);
    }

    private bool ShouldIgnorePageAccelerator()
    {
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        return focused is TextBox or PasswordBox or NumberBox or ComboBox;
    }

    /// <summary>
    /// WebView2 聚焦时 XAML 键盘加速器收不到按键；译文/助手视图把阅读快捷键经
    /// reader.js 转发到这里（编辑控件内的按键已被 JS 侧过滤）。行为与加速器处理保持一致。
    /// </summary>
    private async Task HandleReaderKeyDownAsync(ReaderWebMessage message)
    {
        if (message.Modifiers.Length == 0)
        {
            if (!_processing.HasDocument) return;
            switch (message.Key)
            {
                case "ArrowLeft":
                case "PageUp":
                    if (_session.CurrentPageIndex > 0) await NavigateToPageAsync(_session.CurrentPageIndex - 1);
                    break;
                case "ArrowRight":
                case "PageDown":
                    if (_session.CurrentPageIndex + 1 < _session.PageCount) await NavigateToPageAsync(_session.CurrentPageIndex + 1);
                    break;
                case "Home":
                    await NavigateToPageAsync(0);
                    break;
                case "End":
                    if (_session.PageCount > 0) await NavigateToPageAsync(_session.PageCount - 1);
                    break;
            }
            return;
        }
        if (message.Modifiers != "Ctrl") return;
        switch (message.Key.ToLowerInvariant())
        {
            case "o":
                await ShowOpenPickerAsync();
                break;
            case "r":
                if (_processing.HasDocument) await RenderCurrentPageAsync(forceTranslation: true);
                break;
            case ",":
                ShowSettingsView(true);
                break;
        }
    }

    private async void RetryTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_translationMode == TranslationExecutionMode.Local)
        {
            // 显式"重新翻译"＝干净重翻本页，不继承上次失败留下的内存断点。
            _processing.ClearLocalResumePoints();
        }
        await RenderCurrentPageAsync(forceTranslation: true);
    }

    // ---------- Settings（AI 中心视图） ----------

    private void ApiSettingsButton_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsView(true);

    private void ShowSettingsView(bool show)
    {
        if (show)
        {
            // 每次打开新建视图：数据总是最新，避免缓存状态过期；关闭即释放。
            var view = new Views.SettingsView(new Views.SettingsViewContext(
                _settingsStore,
                _localModels,
                _translationUsage,
                GetPendingAnalysisCount,
                EnqueuePendingLibraryAnalysesAsync,
                _updateService,
                Close));
            view.CloseRequested += (_, _) => ShowSettingsView(false);
            view.SettingsChanged += async (_, _) => await OnSettingsChangedAsync();
            SettingsHostContent.Content = view;
            if (LibraryView.Visibility == Visibility.Visible) ShowLibraryView(false);
            SettingsHost.Visibility = Visibility.Visible;
            ReaderSplitView.Visibility = Visibility.Collapsed;
        }
        else
        {
            SettingsHost.Visibility = Visibility.Collapsed;
            SettingsHostContent.Content = null;
            ReaderSplitView.Visibility = Visibility.Visible;
        }
    }

    private int GetPendingAnalysisCount() =>
        _libraryEntries.Count(document => !document.IsTrashed &&
            document.AnalysisStatus == LibraryAnalysisStatus.Pending);

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_updateService.IsAutomaticCheckDue())
        {
            return;
        }

        _updateService.MarkAutomaticCheckAttempted();
        try
        {
            var release = await _updateService.CheckForUpdateAsync(_updateCheckCancellation.Token);
            if (release is null || _updateCheckCancellation.IsCancellationRequested)
            {
                return;
            }

            UpdateInfoBar.Title = $"发现新版本 {release.Version}";
            UpdateInfoBar.Message = "可在 AI 中心底部下载并安装；安装前会自动校验文件完整性。";
            UpdateInfoBar.IsOpen = true;
        }
        catch (OperationCanceledException) when (_updateCheckCancellation.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception ex)
        {
            AppLog.Error("自动检查更新", ex);
        }
    }

    private void UpdateInfoBar_Click(object sender, RoutedEventArgs e)
    {
        UpdateInfoBar.IsOpen = false;
        ShowSettingsView(true);
    }

    private async Task OnSettingsChangedAsync()
    {
        try
        {
            await ReloadSettingsAsync();
            if (_processing.HasDocument) await RenderCurrentPageAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("设置变更重载", ex);
        }
    }

    private async Task ReloadSettingsAsync()
    {
        _onlineProfile = await _settingsStore.LoadAsync();
        _translationMode = await _settingsStore.LoadExecutionModeAsync();
        _processing.PrefetchTranslationEnabled = await _settingsStore.LoadPrefetchTranslationAsync();
        _processing.LocalFallbackEnabled = await _settingsStore.LoadLocalFallbackEnabledAsync();
        _assistantModelSource = await _settingsStore.LoadAssistantModelSourceAsync();
        _libraryAutoAnalysisEnabled = await _settingsStore.LoadLibraryAutoAnalysisEnabledAsync();
        _libraryAnalysisSource = await _settingsStore.LoadLibraryAnalysisSourceAsync();
        _translationDomainPreference = await _settingsStore.LoadTranslationDomainPreferenceAsync();
        TranslationDomainProfiles.SetOverrides(await _settingsStore.LoadDomainPromptHintsAsync());
        _libraryClassification.AnalysisSource = _libraryAnalysisSource;
        UpdateDomainHint();
        await RefreshQuickModelPickerAsync();
        UpdateTranslationConfigurationUi();
        SelectQuickPicker(_onlineProfile.Id);
    }

    /// <summary>生效的翻译类型提示：手动偏好优先；"auto" 跟随当前文档的 AI 分析领域；通用/未分析不注入。</summary>
    private void UpdateDomainHint()
    {
        var effective = _translationDomainPreference == "auto"
            ? _documentDomain
            : _translationDomainPreference;
        _processing.TranslationDomainHint = effective is "general" or "auto" or null
            ? string.Empty
            : effective;
    }

    /// <summary>文献库在线整理的 profile 解析："follow"=当前活动在线模型；其他值=钉选的 provider Id。</summary>
    private async Task<TranslationProfile?> ResolveLibraryAnalysisProfileAsync()
    {
        if (_libraryAnalysisSource == "follow")
        {
            return _onlineProfile.IsConfigured ? _onlineProfile : null;
        }
        if (_libraryAnalysisSource == "local")
        {
            return null;
        }
        return await _settingsStore.LoadProfileByIdAsync(_libraryAnalysisSource);
    }

    private async void ShortcutsButton_Click(object sender, RoutedEventArgs e)
    {
        ShortcutsDialog.XamlRoot = Content.XamlRoot;
        await ShortcutsDialog.ShowAsync();
    }


    private void UpdateTranslationConfigurationUi()
    {
        var local = _translationMode == TranslationExecutionMode.Local;
        var credentialStoreDown = !local && !_onlineProfile.IsConfigured && !_onlineProfile.IsCredentialStoreAvailable;
        ApiHintBar.IsOpen = local ? !_localModels.IsInstalled : !_onlineProfile.IsConfigured;
        ApiHintBar.Title = local ? "本地 AI 尚未安装"
            : credentialStoreDown ? "Windows 凭据库不可用"
            : "尚未配置翻译模型";
        ApiHintBar.Message = local
            ? "本地模式不会上传页面。请安装 Qwen3 1.7B 后重新翻译。"
            : credentialStoreDown
                ? "无法读取已保存的 API Key。请重启应用后重试，或在设置中重新输入 API Key。"
                : "配置在线模型后，识别完成会自动翻译当前页。";
        ModelStatusText.Text = local
            ? $"本地 · Qwen3 1.7B · {_localModels.Status.Message}"
            : _onlineProfile.IsConfigured
                ? $"{_onlineProfile.DisplayName} · {(_onlineProfile.IsMultimodal ? "多模态" : "纯文本")}"
                : "未配置在线模型";
        using (_translationModeLatch.Enter())
        {
            OnlineTranslationToggle.IsOn = !local;
        }
        QuickModelPicker.IsEnabled = !local && QuickModelPicker.Items.Count > 0;
    }

    private async void OnlineTranslationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_translationModeLatch.IsHeld || _settingsStore is null || _processing is null) return;
        await SetTranslationModeAsync(OnlineTranslationToggle.IsOn
            ? TranslationExecutionMode.Online
            : TranslationExecutionMode.Local);
    }

    /// <summary>
    /// 唯一的在线/本地切换入口：所有触发路径（开关、恢复按钮等）必须走这里，
    /// 保证取消在途任务、清空本地断点、递增渲染版本三件事永远一起发生。
    /// </summary>
    private async Task SetTranslationModeAsync(TranslationExecutionMode mode)
    {
        _translationMode = mode;
        await _settingsStore.SaveExecutionModeAsync(mode);
        _pageWorkCancellation?.Cancel();
        _processing.CancelActiveTranslations();
        // 模式切换后本地断点不再适用（目标语言/流程可能变化），一并清空。
        _processing.ClearLocalResumePoints();
        if (mode == TranslationExecutionMode.Online)
        {
            // 切回在线：主动卸载本地推理服务，释放约 1.5–2.5GB 常驻内存（活跃会话时由空闲定时器兜底）。
            _localModels.RequestUnload();
        }
        Interlocked.Increment(ref _renderVersion);
        UpdateTranslationConfigurationUi();
        if (_processing.HasDocument) await RenderCurrentPageAsync();
    }

    private void LocalModels_StatusChanged(object? sender, LocalAiStatus status) =>
        DispatcherQueue.TryEnqueue(UpdateTranslationConfigurationUi);


    private async Task EnqueuePendingLibraryAnalysesAsync()
    {
        if (!_libraryAutoAnalysisEnabled) return;
        // 来源不可用（如本地模型未安装）时入队没有意义：编排器会立刻把文献重置回 Pending。
        if (!await _libraryClassification.IsReadyAsync()) return;
        await _libraryInitialization;
        foreach (var document in await _libraryRepository.GetAllDocumentsAsync())
            if (!document.IsTrashed && document.AnalysisStatus == LibraryAnalysisStatus.Pending)
                _libraryAnalysisQueue.Enqueue(document.Id, manual: false);
    }

    private void SelectQuickPicker(string id)
    {
        using (_quickPickerLatch.Enter())
        {
            for (var index = 0; index < QuickModelPicker.Items.Count; index++)
            {
                if (QuickModelPicker.Items[index] is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), id, StringComparison.Ordinal))
                {
                    QuickModelPicker.SelectedIndex = index;
                    return;
                }
            }
            QuickModelPicker.SelectedIndex = -1;
        }
    }

    private async Task RefreshQuickModelPickerAsync()
    {
        var presets = await _settingsStore.LoadAllAsync();
        var customs = await _settingsStore.LoadCustomProvidersAsync();
        var configured = presets.Concat(customs).Where(profile => profile.IsConfigured).ToList();
        using (_quickPickerLatch.Enter())
        {
            QuickModelPicker.Items.Clear();
            foreach (var profile in configured)
            {
                QuickModelPicker.Items.Add(new ComboBoxItem
                {
                    Content = profile.DisplayName,
                    Tag = profile.Id
                });
            }
        }
    }

    private async void QuickModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_quickPickerLatch.IsHeld)
        {
            return;
        }
        var id = (QuickModelPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(id) || id == _onlineProfile.Id)
        {
            return;
        }
        try
        {
            var match = await _settingsStore.LoadProfileByIdAsync(id);
            if (match is null || !match.IsConfigured)
            {
                return;
            }
            await _settingsStore.SetActiveModelAsync(match.Id);
            _onlineProfile = match;
            UpdateTranslationConfigurationUi();
            if (_processing.HasDocument)
            {
                await RenderCurrentPageAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("切换翻译模型", ex);
            StatusText.Text = $"切换模型失败：{ex.Message}";
        }
    }

    // ---------- Library ----------

    private async void LibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryView.Visibility == Visibility.Visible)
        {
            ShowLibraryView(false);
            return;
        }
        await RefreshLibraryAsync();
        ShowLibraryView(true);
    }

    private void ShowLibraryView(bool show)
    {
        LibraryView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ReaderSplitView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ThumbnailToggle.IsEnabled = !show && _processing.HasDocument;
        if (show)
        {
            WelcomeOverlay.Visibility = Visibility.Collapsed;
            SetThumbnailPaneOpen(false, updateWidePreference: false);
        }
        else
        {
            WelcomeOverlay.Visibility = _processing.HasDocument ? Visibility.Collapsed : Visibility.Visible;
            ApplyReaderLayout(RootGrid.ActualWidth);
            if (RootGrid.ActualWidth >= 960 && _processing.HasDocument)
            {
                SetThumbnailPaneOpen(_wideThumbnailPaneOpen, updateWidePreference: false);
            }
        }
    }

    private readonly SyncLatch _libraryNavigationLatch = new();

    private async Task RefreshLibraryAsync(string? filter = null)
    {
        await _libraryInitialization;
        _libraryFolders = (await _libraryRepository.GetFoldersAsync()).ToList();
        var allDocuments = (await _libraryRepository.GetAllDocumentsAsync()).ToList();
        UpdateLibraryAnalysisHint(allDocuments);
        RebuildLibraryNavigation(allDocuments);
        await ApplyLibraryQueryAsync(filter ?? LibrarySearchBox.Text.Trim());
        LibraryEditFolder.ItemsSource = new[]
        {
            new LibraryFolder("", null, "未分类", 0, 0, "System", DateTime.MinValue, "未分类")
        }.Concat(_libraryFolders.OrderBy(folder => folder.Path)).ToList();
    }

    /// <summary>文献分析状态提示：自动分析被关闭、本地来源未装模型、或待分析时给出明确说明与一键操作。</summary>
    private void UpdateLibraryAnalysisHint(IReadOnlyList<LibraryDocument> documents)
    {
        var pending = documents.Count(document => !document.IsTrashed &&
            document.AnalysisStatus == LibraryAnalysisStatus.Pending);
        LibraryAnalysisActionLink.Visibility = Visibility.Collapsed;
        if (pending == 0)
        {
            LibraryBusyText.Text = string.Empty;
            return;
        }
        if (!_libraryAutoAnalysisEnabled)
        {
            LibraryBusyText.Text = $"{pending} 篇文献待分析（自动分析已关闭，可在 AI 中心开启）";
            return;
        }
        if (_libraryAnalysisSource == "local" && !_localModels.IsInstalled)
        {
            if (_onlineProfile.IsConfigured)
            {
                LibraryBusyText.Text = $"{pending} 篇文献待分析（来源为本地但模型未安装）";
                LibraryAnalysisActionLink.Content = $"改用 {_onlineProfile.DisplayName} 在线分析";
                LibraryAnalysisActionLink.Visibility = Visibility.Visible;
            }
            else
            {
                LibraryBusyText.Text = $"{pending} 篇文献等待本地 AI 安装后自动分类（Ctrl+, 打开 AI 中心安装）";
            }
        }
        // 来源可用：BusyChanged 事件正常驱动状态文案，此处不覆盖。
    }

    private async void LibraryAnalysisActionLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _settingsStore.SaveLibraryAnalysisSourceAsync("follow");
            await ReloadSettingsAsync();
            await EnqueuePendingLibraryAnalysesAsync();
            await RefreshLibraryAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("切换在线分析", ex);
            StatusText.Text = $"切换失败：{ex.Message}";
        }
    }

    private void RebuildLibraryNavigation(IReadOnlyList<LibraryDocument> documents)
    {
        var previousId = _selectedLibraryNavigation?.Id ?? "all";
        var active = documents.Where(document => !document.IsTrashed).ToList();
        var items = new List<LibraryNavigationItem>();
        if (LibraryModeToggle.IsChecked == true)
        {
            var now = DateTime.UtcNow;
            var todayUtc = DateTime.Now.Date.ToUniversalTime();
            items.AddRange([
                new("history-today", "今天", active.Count(document => document.LastOpenedAt >= todayUtc), LibraryNavigationKind.HistoryToday),
                new("history-week", "最近 7 天", active.Count(document => document.LastOpenedAt >= now.AddDays(-7)), LibraryNavigationKind.HistoryWeek),
                new("history-month", "最近 30 天", active.Count(document => document.LastOpenedAt >= now.AddDays(-30)), LibraryNavigationKind.HistoryMonth),
                new("history-older", "更早", active.Count(document => document.LastOpenedAt < now.AddDays(-30)), LibraryNavigationKind.HistoryOlder),
                new("history-never", "从未打开", active.Count(document => document.LastOpenedAt is null), LibraryNavigationKind.HistoryNever)
            ]);
        }
        else
        {
            items.AddRange([
                new("all", "全部文献", active.Count, LibraryNavigationKind.All),
                new("favorite", "收藏", active.Count(document => document.IsFavorite), LibraryNavigationKind.Favorite),
                new("to-read", "待读", active.Count(document => document.ReadingStatus == LibraryReadingStatus.ToRead), LibraryNavigationKind.ToRead),
                new("reading", "阅读中", active.Count(document => document.ReadingStatus == LibraryReadingStatus.Reading), LibraryNavigationKind.Reading),
                new("read", "已读", active.Count(document => document.ReadingStatus == LibraryReadingStatus.Read), LibraryNavigationKind.Read),
                new("review", "待确认", active.Count(document => document.AnalysisStatus == LibraryAnalysisStatus.NeedsReview), LibraryNavigationKind.NeedsReview),
                new("unclassified", "未分类", active.Count(document => document.FolderId is null), LibraryNavigationKind.Unclassified),
                new("issues", "文件异常", active.Count(document => !document.ManagedFileExists), LibraryNavigationKind.FileIssue),
                new("trash", "回收站", documents.Count(document => document.IsTrashed), LibraryNavigationKind.Trash)
            ]);
            foreach (var folder in _libraryFolders.OrderBy(folder => folder.Path))
            {
                var ids = new HashSet<string>(LibraryRepository.GetDescendantFolderIds(folder.Id, _libraryFolders)) { folder.Id };
                items.Add(new LibraryNavigationItem($"folder:{folder.Id}", folder.Name,
                    active.Count(document => document.FolderId is not null && ids.Contains(document.FolderId)),
                    LibraryNavigationKind.Folder, folder.Id, folder.Depth - 1));
            }
        }

        using (_libraryNavigationLatch.Enter())
        {
            LibraryNavigationList.ItemsSource = items;
            var index = items.FindIndex(item => item.Id == previousId);
            if (index < 0) index = 0;
            LibraryNavigationList.SelectedIndex = index;
            _selectedLibraryNavigation = index >= 0 && index < items.Count ? items[index] : null;
        }
    }

    private async Task ApplyLibraryQueryAsync(string filter)
    {
        if (LibraryListView is null || _selectedLibraryNavigation is null) return;
        var sort = LibrarySortBox.SelectedIndex switch
        {
            1 => LibrarySortOrder.Added,
            2 => LibrarySortOrder.Title,
            3 => LibrarySortOrder.Progress,
            _ => LibrarySortOrder.LastOpened
        };
        var libraryFilter = LibraryFilterBox.SelectedIndex switch
        {
            1 => LibraryFilterKind.Favorite,
            2 => LibraryFilterKind.ToRead,
            3 => LibraryFilterKind.Reading,
            4 => LibraryFilterKind.Read,
            5 => LibraryFilterKind.NeedsReview,
            6 => LibraryFilterKind.Unclassified,
            7 => LibraryFilterKind.FileIssue,
            8 => LibraryFilterKind.PendingAnalysis,
            _ => LibraryFilterKind.All
        };
        _libraryEntries = (await _libraryQuery.SearchAsync(new LibraryQuery(filter,
            _selectedLibraryNavigation.Kind, _selectedLibraryNavigation.FolderId, sort, true, libraryFilter))).ToList();
        LibraryListView.ItemsSource = _libraryEntries;
        LibraryResultTitle.Text = _selectedLibraryNavigation.Label;
        LibraryResultCount.Text = $"{_libraryEntries.Count} 篇";
        _ = EnsureVisibleLibraryThumbnailsAsync(_libraryEntries.Take(80).ToList());
    }

    private async void LibraryModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (LibraryNavigationList is null) return;
        LibraryModeToggle.Content = LibraryModeToggle.IsChecked == true ? "历史  /  分类" : "分类  /  历史";
        _selectedLibraryNavigation = null;
        await RefreshLibraryAsync();
    }

    private async void LibraryNavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_libraryNavigationLatch.IsHeld || LibraryNavigationList.SelectedItem is not LibraryNavigationItem item) return;
        _selectedLibraryNavigation = item;
        await ApplyLibraryQueryAsync(LibrarySearchBox.Text.Trim());
    }

    private async void LibrarySortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibrarySearchBox is not null) await ApplyLibraryQueryAsync(LibrarySearchBox.Text.Trim());
    }

    private async void LibraryFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibrarySearchBox is not null) await ApplyLibraryQueryAsync(LibrarySearchBox.Text.Trim());
    }

    private async Task EnsureVisibleLibraryThumbnailsAsync(IReadOnlyList<LibraryDocument> documents)
    {
        // 并行发起，但 LibraryThumbnailService 内部 _gate(2,2) 把实际渲染限流到 2 并发，
        // 既比串行快，又不与大页 OCR/翻译抢内存。
        var results = await Task.WhenAll(documents.Select(document => _libraryThumbnails.EnsureAsync(document)));
        if (results.Any(c => c))
        {
            DispatcherQueue.TryEnqueue(RefreshLibraryListPreservingView);
        }
    }

    /// <summary>重设 ItemsSource 会丢失滚动位置与选中项：先记录再恢复（缩略图刷新等局部更新用）。</summary>
    private void RefreshLibraryListPreservingView()
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(LibraryListView);
        var offset = scrollViewer?.VerticalOffset ?? 0;
        var selectedId = (LibraryListView.SelectedItem as LibraryDocument)?.Id;
        LibraryListView.ItemsSource = _libraryEntries.ToList();
        if (selectedId is not null)
        {
            LibraryListView.SelectedItem = _libraryEntries.FirstOrDefault(document => document.Id == selectedId);
        }
        if (offset > 0 && scrollViewer is not null)
        {
            LibraryListView.UpdateLayout();
            scrollViewer.ChangeView(null, offset, null, disableAnimation: true);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private async void LibraryBatchStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryEntries.Count == 0 || sender is not MenuFlyoutItem item ||
            !Enum.TryParse<LibraryReadingStatus>(item.Tag?.ToString(), out var status)) return;
        await _libraryRepository.SetReadingStatusAsync(_libraryEntries.Select(document => document.Id).ToList(), status);
        await RefreshLibraryAsync();
        StatusText.Text = $"已批量更新 {_libraryEntries.Count} 篇文献的阅读状态";
    }

    private async void LibraryBatchMove_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryEntries.Count == 0) return;
        var folders = new[]
        {
            new LibraryFolder("", null, "未分类", 0, 0, "System", DateTime.MinValue, "未分类")
        }.Concat(_libraryFolders.OrderBy(folder => folder.Path)).ToList();
        var target = new ComboBox
        {
            Header = $"移动 {_libraryEntries.Count} 篇文献到",
            DisplayMemberPath = "Path",
            ItemsSource = folders,
            SelectedIndex = 0,
            MinWidth = 320
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "批量移动",
            Content = target,
            PrimaryButtonText = "移动",
            CloseButtonText = "取消"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var folder = target.SelectedItem as LibraryFolder;
        await _libraryRepository.MoveDocumentsAsync(
            _libraryEntries.Select(document => document.Id).ToList(), string.IsNullOrEmpty(folder?.Id) ? null : folder.Id);
        await RefreshLibraryAsync();
    }

    private async void LibraryBatchTrash_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryEntries.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"将 {_libraryEntries.Count} 篇文献移入回收站？",
            Content = "托管 PDF 会保留 30 天，期间可以恢复；原始来源文件不会受到影响。",
            PrimaryButtonText = "移入回收站",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _libraryRepository.SetDocumentsTrashedAsync(_libraryEntries.Select(document => document.Id).ToList(), true);
        await RefreshLibraryAsync();
    }

    private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 与页码输入同款防抖：每个键击直接查库会让列表滚动位置/选中频繁丢失。
        _librarySearchDebounceTimer.Stop();
        _librarySearchDebounceTimer.Start();
    }

    private async void LibrarySearchDebounceTimer_Tick(object? sender, object e)
    {
        _librarySearchDebounceTimer.Stop();
        if (_selectedLibraryNavigation is not null) await ApplyLibraryQueryAsync(LibrarySearchBox.Text.Trim());
    }

    private async void LibraryAddButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await ImportToLibraryAsync(file.Path, analyze: true);
    }

    private async void LibraryAddBatchButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await ImportFilesAsync(files.Select(file => file.Path).ToList());
    }

    private async void LibraryAddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var paths = await CollectPdfPathsAsync(folder);
        await ImportFilesAsync(paths);
    }

    private void InitializePicker(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private static async Task<List<string>> CollectPdfPathsAsync(StorageFolder root)
    {
        var result = new List<string>();
        var queue = new Queue<StorageFolder>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var folder = queue.Dequeue();
            try
            {
                result.AddRange((await folder.GetFilesAsync()).Where(file =>
                    file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase)).Select(file => file.Path));
                foreach (var child in await folder.GetFoldersAsync()) queue.Enqueue(child);
            }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private async Task ImportFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        LibraryBusyRing.IsActive = true;
        var imported = 0;
        var duplicates = 0;
        var failed = 0;
        try
        {
            for (var index = 0; index < paths.Count; index++)
            {
                LibraryBusyText.Text = $"正在归档 {index + 1}/{paths.Count}：{Path.GetFileName(paths[index])}";
                try
                {
                    var result = await ImportToLibraryAsync(paths[index], analyze: false);
                    if (result is null) failed++;
                    else if (result.WasDuplicate) duplicates++;
                    else imported++;
                    if (result?.WasCreated == true) _libraryAnalysisQueue.Enqueue(result.Document.Id, manual: false);
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Error("批量导入失败", ex);
                }
            }
            await RefreshLibraryAsync();
            StatusText.Text = $"导入完成：新增 {imported}，重复 {duplicates}，失败 {failed}";
        }
        finally
        {
            LibraryBusyRing.IsActive = false;
            LibraryBusyText.Text = string.Empty;
        }
    }

    private async Task<LibraryImportResult?> ImportToLibraryAsync(string filePath, bool analyze)
    {
        await _libraryInitialization;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var pdf = await PdfDocument.LoadFromFileAsync(file);
            if (pdf.PageCount == 0) throw new InvalidDataException("PDF 没有可读取的页面。");
            var result = await _libraryIngestion.EnsureImportedAsync(file.Path, pdf.PageCount);
            if (analyze && result.WasCreated) _libraryAnalysisQueue.Enqueue(result.Document.Id, manual: false);
            await RefreshLibraryAsync();
            StatusText.Text = result.WasDuplicate ? $"已存在，已合并来源：{result.Document.Title}" : $"已加入文献库：{result.Document.Title}";
            return result;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"文献归档失败：{ex.Message}";
            return null;
        }
    }

    private async void LibraryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryListView.SelectedItem is not LibraryDocument document) return;
        LibraryEditTitle.Text = document.Title;
        LibraryEditAuthors.Text = document.Authors;
        LibraryEditYear.Value = document.PublicationYear ?? double.NaN;
        LibraryEditTags.Text = string.Join(", ", document.Tags);
        LibraryEditSummary.Text = document.AiSummary;
        LibraryReadingStatusBox.SelectedIndex = (int)document.ReadingStatus;
        LibraryFavoriteCheck.IsChecked = document.IsFavorite;
        LibraryEditFolder.SelectedItem = ((IEnumerable<LibraryFolder>)LibraryEditFolder.ItemsSource)
            .FirstOrDefault(folder => folder.Id == (document.FolderId ?? ""));
        LibraryInfoText.Text = $"{document.PageCount} 页 · {document.FileSizeLabel}\n添加：{document.AddedAtLabel}\n最后打开：{document.LastOpenedAtLabel}\n打开 {document.OpenCount} 次 · 进度 {document.ProgressLabel}";
        LibrarySourcesText.Text = document.SourcePathsLabel;
        LibraryTrashButton.Content = document.IsTrashed ? "从回收站恢复" : "移入回收站";
        LibraryDeletePermanentlyButton.Visibility = document.IsTrashed ? Visibility.Visible : Visibility.Collapsed;
        var proposal = await _libraryRepository.GetProposalAsync(document.Id);
        LibraryProposalBar.IsOpen = document.AnalysisStatus == LibraryAnalysisStatus.NeedsReview && proposal is not null;
        if (proposal is not null)
            LibraryProposalBar.Message = $"{string.Join(" / ", proposal.SuggestedPath)} · 置信度 {proposal.Confidence:P0}\n{proposal.Reason}";
    }

    private async void LibrarySaveEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryListView.SelectedItem is not LibraryDocument document) return;
        var year = double.IsNaN(LibraryEditYear.Value) ? null : (int?)Math.Round(LibraryEditYear.Value);
        var tags = LibraryEditTags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await _libraryRepository.UpdateDocumentAsync(document.Id, LibraryEditTitle.Text.Trim(), LibraryEditAuthors.Text.Trim(),
            year, LibraryEditSummary.Text.Trim(), tags, (LibraryReadingStatus)Math.Max(0, LibraryReadingStatusBox.SelectedIndex),
            LibraryFavoriteCheck.IsChecked == true);
        var folder = LibraryEditFolder.SelectedItem as LibraryFolder;
        await _libraryRepository.MoveDocumentAsync(document.Id, string.IsNullOrEmpty(folder?.Id) ? null : folder.Id);
        await RefreshLibraryAsync();
        StatusText.Text = $"已保存：{LibraryEditTitle.Text.Trim()}";
    }

    private async void LibraryOpenButton_Click(object sender, RoutedEventArgs e) => await OpenSelectedLibraryDocumentAsync(fromStart: false);
    private async void LibraryOpenFromStartButton_Click(object sender, RoutedEventArgs e) => await OpenSelectedLibraryDocumentAsync(fromStart: true);
    private async void LibraryListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await OpenSelectedLibraryDocumentAsync(false);
    private async void LibraryListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await OpenSelectedLibraryDocumentAsync(false);
        }
    }

    private async Task OpenSelectedLibraryDocumentAsync(bool fromStart)
    {
        if (LibraryListView.SelectedItem is not LibraryDocument document || !File.Exists(document.ManagedPath)) return;
        if (fromStart) await _libraryRepository.UpdateReadingProgressAsync(document.Id, 0, document.PageCount);
        ShowLibraryView(false);
        await OpenPdfFileAsync(document.ManagedPath);
        if (fromStart && _session.CurrentPageIndex != 0) await NavigateToPageAsync(0);
    }

    private void LibraryLocateButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryListView.SelectedItem is not LibraryDocument document) return;
        try { Process.Start("explorer.exe", $"/select,\"{document.ManagedPath}\""); }
        catch (Exception ex) { StatusText.Text = $"无法定位：{ex.Message}"; }
    }

    private async void LibraryExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryListView.SelectedItem is not LibraryDocument document) return;
        var format = await PickExportFormatAsync();
        if (format is null) return;
        var picker = new FileSavePicker { SuggestedFileName = document.Title };
        InitializePicker(picker);
        var isPdf = format == "pdf";
        var isBilingual = format == "bi";
        var label = isPdf ? "PDF 文档" : (isBilingual ? "双语 Markdown" : "译文 Markdown");
        picker.FileTypeChoices.Add(label, [isPdf ? ".pdf" : ".md"]);
        var destination = await picker.PickSaveFileAsync();
        if (destination is null) return;
        try
        {
            if (isPdf)
            {
                File.Copy(document.ManagedPath, destination.Path, overwrite: true);
            }
            else
            {
                await _processing.ExportAsync(document.ContentHash, document.Title, document.PageCount,
                    ActiveTranslationProfile.Settings, destination.Path, isBilingual);
            }
            StatusText.Text = $"已导出：{destination.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private async Task<string?> PickExportFormatAsync()
    {
        var combo = new ComboBox();
        combo.Items.Add(new ComboBoxItem { Content = "原始 PDF", Tag = "pdf" });
        combo.Items.Add(new ComboBoxItem { Content = "译文 Markdown", Tag = "md" });
        combo.Items.Add(new ComboBoxItem { Content = "双语 Markdown（原文 + 译文）", Tag = "bi" });
        combo.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            Title = "导出文献",
            Content = combo,
            PrimaryButtonText = "导出",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "pdf";
    }

    private void LibraryReclassifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryListView.SelectedItem is not LibraryDocument document) return;
        _libraryAnalysisQueue.Enqueue(document.Id, manual: true);
        StatusText.Text = $"已将本地 AI 分析置于队列：{document.Title}";
    }

    private async void LibraryAcceptProposalButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryListView.SelectedItem is not LibraryDocument document) return;
        var proposal = await _libraryRepository.GetProposalAsync(document.Id);
        if (proposal is null) return;
        LibraryFolder? folder = null;
        if (proposal.SuggestedPath.Count > 0)
            folder = await _libraryRepository.FindFolderByPathAsync(proposal.SuggestedPath)
                ?? await _libraryRepository.EnsureFolderPathAsync(proposal.SuggestedPath.Take(3).ToArray(), "UserConfirmedAI");
        await _libraryRepository.AcceptProposalAsync(document.Id, folder?.Id);
        await RefreshLibraryAsync();
        StatusText.Text = "已接受 AI 分类建议";
    }

    private async void LibraryRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryListView.SelectedItem is not LibraryDocument document) return;
        await _libraryRepository.SetTrashedAsync(document.Id, !document.IsTrashed);
        await RefreshLibraryAsync();
        StatusText.Text = document.IsTrashed ? "已恢复到文献库" : "已移入回收站（30 天内可恢复）";
    }

    private async void LibraryDeletePermanentlyButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryListView.SelectedItem is not LibraryDocument document || !document.IsTrashed) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "永久删除这篇文献？",
            Content = $"将删除“{document.Title}”的文献记录和托管 PDF。原始来源文件不会删除，此操作不可恢复。",
            PrimaryButtonText = "永久删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _libraryRepository.DeletePermanentlyAsync(document.Id);
        await RefreshLibraryAsync();
        StatusText.Text = "已永久删除文献及托管副本";
    }

    private async void LibraryNewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var parentId = _selectedLibraryNavigation?.Kind == LibraryNavigationKind.Folder
            ? _selectedLibraryNavigation.FolderId : null;
        var input = new TextBox { Header = "目录名称", MinWidth = 320 };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = parentId is null ? "新建一级目录" : "新建子目录",
            Content = input, PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var folder = await _libraryRepository.CreateFolderAsync(input.Text, parentId);
            _selectedLibraryNavigation = new LibraryNavigationItem($"folder:{folder.Id}", folder.Name, 0, LibraryNavigationKind.Folder, folder.Id);
            await RefreshLibraryAsync();
        }
        catch (Exception ex) { StatusText.Text = $"无法创建目录：{ex.Message}"; }
    }

    private async void LibraryManageFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryNavigation?.Kind != LibraryNavigationKind.Folder || _selectedLibraryNavigation.FolderId is null)
        {
            StatusText.Text = "请先选择一个自定义目录";
            return;
        }
        var folder = _libraryFolders.Single(item => item.Id == _selectedLibraryNavigation.FolderId);
        var input = new TextBox { Header = "目录名称", Text = folder.Name, MinWidth = 320 };
        var target = new ComboBox { Header = "移动或合并到", DisplayMemberPath = "Path", MinWidth = 320,
            ItemsSource = new[] { new LibraryFolder("", null, "顶层 / 不选择", 0, 0, "System", DateTime.MinValue, "顶层 / 不选择") }
                .Concat(_libraryFolders.Where(item => item.Id != folder.Id).OrderBy(item => item.Path)).ToList() };
        target.SelectedIndex = 0;
        var action = new ComboBox { Header = "操作", MinWidth = 320, SelectedIndex = 0,
            Items = { new ComboBoxItem { Content = "仅重命名" }, new ComboBoxItem { Content = "移动到所选目录" },
                new ComboBoxItem { Content = "合并到所选目录" }, new ComboBoxItem { Content = "删除目录（文献转为未分类）" } } };
        var content = new StackPanel { Spacing = 10, Children = { input, action, target } };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = $"管理目录：{folder.Path}", Content = content,
            PrimaryButtonText = "应用", CloseButtonText = "取消" };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        try
        {
            var targetFolder = target.SelectedItem as LibraryFolder;
            switch (action.SelectedIndex)
            {
                case 1:
                    await _libraryRepository.MoveFolderAsync(folder.Id, string.IsNullOrEmpty(targetFolder?.Id) ? null : targetFolder.Id);
                    break;
                case 2 when !string.IsNullOrEmpty(targetFolder?.Id):
                    await _libraryRepository.MergeFolderAsync(folder.Id, targetFolder.Id);
                    break;
                case 3:
                    await _libraryRepository.DeleteFolderAsync(folder.Id);
                    break;
                default:
                    await _libraryRepository.RenameFolderAsync(folder.Id, input.Text);
                    break;
            }
        }
        catch (Exception ex) { StatusText.Text = $"目录操作失败：{ex.Message}"; return; }
        _selectedLibraryNavigation = null;
        await RefreshLibraryAsync();
    }

    private void LibraryView_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "导入到托管文献库";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private async void LibraryView_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var paths = new List<string>();
        foreach (var item in await e.DataView.GetStorageItemsAsync())
        {
            if (item is StorageFile file && file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) paths.Add(file.Path);
            else if (item is StorageFolder folder) paths.AddRange(await CollectPdfPathsAsync(folder));
        }
        await ImportFilesAsync(paths);
    }

    // ---------- Page pipeline ----------

    private async Task RenderCurrentPageAsync(bool forceTranslation = false)
    {
        if (!_processing.HasDocument)
        {
            return;
        }

        _pageWorkCancellation?.Cancel();
        _pageWorkCancellation?.Dispose();
        _pageWorkCancellation = new CancellationTokenSource();
        var cancellationToken = _pageWorkCancellation.Token;
        var renderVersion = Interlocked.Increment(ref _renderVersion);
        var totalTimer = Stopwatch.StartNew();

        var pageIndex = _session.CurrentPageIndex;
        var pageNumber = pageIndex + 1;
        _currentTranslationMarkdown = string.Empty;
        _currentSourceText = string.Empty;
        PageErrorBar.IsOpen = false;
        LocalTranslationHintBar.IsOpen = false;
        LocalTranslationRecoveryActions.Visibility = Visibility.Collapsed;
        StatusText.Text = $"正在渲染第 {pageNumber} 页…";
        SetTranslationState("页面处理中", TranslationVisualState.Working);
        _ = _markdownReader.ClearAsync(); // 翻页即清空译文视图，避免残留上一页内容
        try
        {
            var render = await _processing.GetPageRenderAsync(pageIndex, cancellationToken);
            if (renderVersion != _renderVersion)
            {
                return;
            }
            AppLog.Info($"第 {pageNumber} 页渲染完成 ({render.RenderMilliseconds}ms)");
            OriginalPageImage.Source = await CreateBitmapImageAsync(render.EncodedImage);
            _currentPageAspectRatio = render.Height > 0
                ? render.Width / (double)render.Height
                : 0;
            _fitToHeight = true;
            await FitCurrentPageToHeightAsync();

            UpdateNavigationUi(pageIndex);
            TranslationTitle.Text = $"第 {pageNumber} 页";
            await _markdownReader.SetPageAsync(pageNumber);

            var translationProfile = ActiveTranslationProfile;
            var localTranslation = _translationMode == TranslationExecutionMode.Local;
            var translationReady = localTranslation ? _localModels.IsInstalled : translationProfile.IsConfigured;
            ShowTranslationMessage(translationReady
                ? localTranslation ? "正在进行本地 OCR，随后由本地 Qwen3 翻译…" : "正在同时进行快速初译和本地 OCR…"
                : "正在进行本地文字识别…");
            SourceMetaText.Text = "正在识别…";
            SourceBlocksList.ItemsSource = null;
            SourceEmptyText.Visibility = Visibility.Collapsed;
            StatusText.Text = translationReady
                ? localTranslation ? "正在运行本地 PaddleOCR 与 Qwen3 1.7B…" : $"正在调用 {translationProfile.Settings.Model}，同时运行 PaddleOCR…"
                : "正在调用 PaddleOCR CPU 引擎…";
            SetTranslationState(translationReady ? localTranslation ? "本地 OCR" : "快速初译" : "OCR 识别中",
                TranslationVisualState.Working);

            Task<PageTranslationResult>? translationTask = null;
            if (translationReady)
            {
                var streamingProgress = new Progress<MarkdownRenderUpdate>(update =>
                {
                    if (renderVersion != _renderVersion || string.IsNullOrWhiteSpace(update.Markdown)) return;
                    SetTranslationMarkdown(update);
                    SetTranslationState(localTranslation && update.StepCount > 0
                        ? $"本地翻译 {update.Step}/{update.StepCount}"
                        : update.Stage switch
                    {
                        TranslationPipelineStage.Drafting => "快速初译中",
                        TranslationPipelineStage.OcrRunning => "OCR 校验中",
                        TranslationPipelineStage.Reviewing => "校订中 · 初译可读",
                        _ => update.IsFinal ? "翻译完成" : "快速初译"
                    }, update.IsFinal ? TranslationVisualState.Completed : TranslationVisualState.Working);
                });
                translationTask = _processing.GetTranslationAsync(pageIndex, translationProfile,
                    streamingProgress, forceTranslation, cancellationToken);
            }

            PageData? data = null;
            Exception? ocrError = null;
            try
            {
                data = await _processing.GetPageDataAsync(pageIndex, cancellationToken);
            }
            catch (Exception ex) when (ex is NativeOcrException or DllNotFoundException)
            {
                ocrError = ex;
                SourceMetaText.Text = $"OCR 失败：{ex.Message}";
                SourceBlocksList.ItemsSource = null;
                SourceEmptyText.Visibility = Visibility.Collapsed;
            }
            if (renderVersion != _renderVersion) return;

            if (data is not null)
            {
                EngineText.Text = "PaddleOCR · oneDNN";
                AppLog.Info($"第 {pageNumber} 页 OCR 完成，{data.Ocr.Blocks.Count} 块（{(data.OcrCacheHit ? "缓存" : data.OcrMilliseconds + "ms")}）");
                SourceMetaText.Text = $"PaddleOCR · oneDNN · {data.Ocr.Blocks.Count} 块 · {(data.OcrCacheHit ? "缓存" : data.OcrMilliseconds + "ms")}";
                SourceBlocksList.ItemsSource = data.Ocr.Blocks;
                SourceEmptyText.Visibility = data.Ocr.Blocks.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                _currentSourceText = data.SourceText;
            }

            // 分级预取：后台先渲染、再 OCR 下一页（本地免费）；翻译 API 默认不预取，
            // 仅在设置里开启"空闲时预翻译下一页"后由 PrefetchNextTranslation 负责。
            _processing.PrefetchNextRender(pageIndex);

            if (!translationReady)
            {
                if (ocrError is not null) throw ocrError;
                ShowTranslationMessage(localTranslation
                    ? "OCR 已完成。本地 AI 尚未安装；页面不会上传。请在设置中安装后重试。"
                    : "OCR 已完成。请在设置中配置在线模型，随后会自动翻译这一页。");
                SetTranslationState(localTranslation ? "等待本地模型" : "等待配置", TranslationVisualState.Idle);
                LocalTranslationHintBar.Message = "当前页面没有上传。安装本地模型、重试，或主动切回在线翻译。";
                LocalTranslationHintBar.IsOpen = localTranslation;
                LocalTranslationRecoveryActions.Visibility = localTranslation
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                StatusText.Text = data is null ? "OCR 未识别到内容" :
                    $"识别完成 · {data.Ocr.Blocks.Count} 块 · OCR {FormatStage(data.OcrCacheHit, data.OcrMilliseconds)} · 等待配置翻译模型";
                return;
            }
            var result = await translationTask!;
            if (renderVersion != _renderVersion)
            {
                return;
            }

            // 当前页译文已落盘（下一页上下文指纹已确定），空闲时后台预翻译下一页。
            _processing.PrefetchNextTranslation(pageIndex, translationProfile);

            SetTranslationMarkdown(new MarkdownRenderUpdate(result.Text,
                TranslationPipelineStage.Final, result.IsFinal));
            SetTranslationState(result.CacheHit ? "译文缓存" :
                localTranslation ? result.IsFinal ? "本地翻译完成" : "本地翻译未完成" :
                result.WasReviewed ? "已校订" : result.IsFinal ? "翻译完成" : "快速初译",
                result.CacheHit ? TranslationVisualState.Cached :
                result.IsFinal || result.WasReviewed ? TranslationVisualState.Completed : TranslationVisualState.Idle);
            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                ShowPageError("翻译降级", result.Warning);
            }
            AppLog.Info($"第 {pageNumber} 页翻译完成（{(result.CacheHit ? "缓存" : result.Milliseconds + "ms")}）");
            totalTimer.Stop();
            var usage = _translationUsage.GetSummary();
            var usageSuffix = usage.TodayTotalTokens > 0 ? $" · 今日 {usage.TodayTotalTokens:N0} tokens" : string.Empty;
            StatusText.Text = $"本页 {totalTimer.Elapsed.TotalSeconds:F1}s · 渲染 {render.RenderMilliseconds}ms · OCR {(data is null ? "失败" : FormatStage(data.OcrCacheHit, data.OcrMilliseconds))} · 翻译 {FormatStage(result.CacheHit, result.Milliseconds)}{usageSuffix}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (DllNotFoundException)
        {
            EngineText.Text = "OCR DLL 未找到";
            ShowTranslationMessage("PDF 页面渲染已经完成；本地 OCR DLL 尚未复制到应用目录。");
            SetTranslationState("OCR 不可用", TranslationVisualState.Error);
            StatusText.Text = "页面已就绪 · OCR 引擎待构建";
        }
        catch (NativeOcrException ex)
        {
            AppLog.Error("OCR", ex);
            EngineText.Text = "OCR 初始化失败";
            ShowTranslationMessage("本地 OCR 识别失败，详见上方提示。");
            SetTranslationState("OCR 错误", TranslationVisualState.Error);
            StatusText.Text = $"本地 OCR 错误 ({ex.Status})";
            ShowPageError("本地 OCR 识别失败", ex.Message);
        }
        catch (TranslationException ex)
        {
            AppLog.Error("翻译", ex);
            ShowTranslationMessage("翻译失败，详见上方提示；修复后点击\"重新翻译\"。");
            SetTranslationState("翻译失败", TranslationVisualState.Error);
            StatusText.Text = "OCR 已完成 · 请检查翻译设置";
            ShowPageError("翻译接口调用失败", ex.Message);
            if (_translationMode == TranslationExecutionMode.Local)
            {
                LocalTranslationHintBar.Message = "本地模型生成失败，未调用在线 API。";
                LocalTranslationHintBar.IsOpen = true;
                LocalTranslationRecoveryActions.Visibility = Visibility.Visible;
            }
        }
        catch (LocalAiNotInstalledException ex)
        {
            // 本地模型未安装：明确引导安装，不再落入泛泛的"页面处理失败"。
            AppLog.Info($"本地模型未安装：{ex.Message}");
            ShowTranslationMessage("本地模型尚未安装。请在设置中安装本地模型，或切换在线翻译。");
            SetTranslationState("等待本地模型", TranslationVisualState.Idle);
            StatusText.Text = "OCR 已完成 · 等待安装本地模型";
            LocalTranslationHintBar.Message = ex.Message;
            LocalTranslationHintBar.IsOpen = true;
            LocalTranslationRecoveryActions.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (_translationMode == TranslationExecutionMode.Local &&
            ex is InvalidOperationException or TimeoutException or InvalidDataException)
        {
            // 本地推理服务启动失败（端口冲突、校验失败、超时等），给出安装/修复/切在线入口。
            AppLog.Error("本地模型", ex);
            ShowTranslationMessage("本地模型启动失败，详见上方提示；可修复模型或临时切换在线翻译。");
            SetTranslationState("本地模型失败", TranslationVisualState.Error);
            StatusText.Text = "OCR 已完成 · 本地模型启动失败";
            ShowPageError("本地模型启动失败", ex.Message);
            LocalTranslationHintBar.Message = "本地模型启动失败，未调用在线 API。";
            LocalTranslationHintBar.IsOpen = true;
            LocalTranslationRecoveryActions.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppLog.Error("页面处理", ex);
            ShowTranslationMessage("页面处理失败，详见上方提示。");
            SetTranslationState("处理失败", TranslationVisualState.Error);
            StatusText.Text = "页面处理失败";
            ShowPageError("页面处理失败", ex.Message);
        }
        finally
        {
            if (renderVersion == _renderVersion)
            {
                TranslationProgress.IsActive = false;
            }
        }
    }

    private void UpdateNavigationUi(uint pageIndex)
    {
        var pageNumber = pageIndex + 1;
        using (_pageNumberLatch.Enter())
        {
            PageNumberBox.Maximum = _session.PageCount;
            PageNumberBox.Value = pageNumber;
            PageNumberBox.IsEnabled = true;
            PageCountText.Text = $"/ {_session.PageCount}";
        }
        PreviousButton.IsEnabled = pageIndex > 0;
        NextButton.IsEnabled = pageNumber < _session.PageCount;
        SyncThumbnailSelection(pageIndex);
    }

    private void SetTranslationMarkdown(MarkdownRenderUpdate update)
    {
        _currentTranslationMarkdown = update.Markdown;
        TranslationBody.Visibility = Visibility.Collapsed;
        TranslationWebView.Visibility = Visibility.Visible;
        _markdownReader.Update(update);
    }

    private async void MarkdownReader_ReaderMessageReceived(ReaderWebMessage message)
    {
        try
        {
            if (message.Type == "keyDown")
            {
                await HandleReaderKeyDownAsync(message);
                return;
            }
            if (message.Type == "stopAnswer")
            {
                _readerAssistant.Stop();
                return;
            }
            if (message.Type == "openTopic")
            {
                var existing = _readerAssistant.Topics.FirstOrDefault(topic => topic.Id == message.TopicId);
                if (existing is not null) await _markdownReader.ShowTopicAsync(existing);
                return;
            }
            if (message.Type == "sendFollowUp")
            {
                if (!string.IsNullOrWhiteSpace(message.Question))
                    await RunAssistantQuestionAsync(message.TopicId, ReaderQuestionMode.FollowUp, message.Question);
                return;
            }
            if (message.Type is not ("explainSelection" or "askSelection")) return;
            SetReaderViewMode(ReaderViewMode.Assistant);
            await UpdateAssistantMetaAsync();
            await _markdownReader.ShowAssistantAsync();
            var precheck = await ResolveAssistantRoutingAsync();
            if (precheck.Error is not null)
            {
                await _markdownReader.ShowAssistantErrorAsync(precheck.Error);
                return;
            }
            var selected = message.SelectedText.Trim();
            if (selected.Length == 0) return;
            if (selected.Length > 8000)
            {
                await _markdownReader.ShowAssistantErrorAsync("选中文字超过 8,000 字，请缩小选择范围后重试。");
                return;
            }
            if (message.PageNumber != _session.CurrentPageIndex + 1) return;
            var selection = new ReaderSelectionContext(_documentKey, message.PageNumber, selected,
                message.SurroundingText, message.StructureType);
            var topic = _readerAssistant.CreateTopic(selection);
            await _markdownReader.SetTopicsAsync(_readerAssistant.Topics);
            await _markdownReader.ShowTopicAsync(topic);
            if (message.Type == "explainSelection")
                await RunAssistantQuestionAsync(topic.Id, ReaderQuestionMode.Explain, string.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error("AI 阅读助手", ex);
            await _markdownReader.ShowAssistantErrorAsync(ex.Message);
        }
    }

    /// <summary>
    /// 问答模型路由：跟随翻译模式（follow）/ 固定本地（local）/ 钉选某在线 provider（Id）。
    /// 本地路由时页面内容（选区/OCR/译文）只发给本机 llama-server，绝不附带图像——
    /// 这是"本地模式不上传页面"承诺的一部分。
    /// </summary>
    private async Task<(bool UseLocal, TranslationProfile? OnlineProfile, string? Error)> ResolveAssistantRoutingAsync()
    {
        var source = _assistantModelSource;
        if (source == "local")
        {
            return _localModels.IsInstalled
                ? (true, null, null)
                : (true, null, "问答已固定使用本地模型，但本地模型尚未安装。请在 AI 中心安装，或更换问答来源。");
        }
        if (source == "follow")
        {
            if (_translationMode == TranslationExecutionMode.Local)
            {
                return _localModels.IsInstalled
                    ? (true, null, null)
                    : (true, null, "本地模式不会上传页面内容。请先在 AI 中心安装本地模型，或切换在线翻译模式后提问。");
            }
            return _onlineProfile.IsConfigured
                ? (false, _onlineProfile, null)
                : (false, null, "请先在 AI 中心配置可用模型和 API Key。");
        }
        var pinned = await _settingsStore.LoadProfileByIdAsync(source);
        return pinned is { IsConfigured: true }
            ? (false, pinned, null)
            : (false, null, "钉选的问答模型未配置或已删除，请在 AI 中心检查问答设置。");
    }

    private async Task RunAssistantQuestionAsync(string topicId, ReaderQuestionMode mode, string question)
    {
        var routing = await ResolveAssistantRoutingAsync();
        if (routing.Error is not null)
        {
            await _markdownReader.ShowAssistantErrorAsync(routing.Error);
            return;
        }
        var topic = _readerAssistant.Topics.FirstOrDefault(value => value.Id == topicId);
        if (topic is null) return;
        _assistantWork?.Cancel();
        _assistantWork?.Dispose();
        _assistantWork = new CancellationTokenSource();
        var token = _assistantWork.Token;
        var pageIndex = topic.Selection.PageNumber - 1;
        var data = await _processing.GetPageDataAsync(pageIndex, token);
        var translationProfile = ActiveTranslationProfile;
        var context = await _processing.GetDocumentContextAsync(pageIndex, translationProfile.Settings, token);
        TranslationProfile assistantProfile;
        LocalAiSession? localSession = null;
        if (routing.UseLocal)
        {
            localSession = await _localModels.OpenSessionAsync(LocalAiPriority.ForegroundTranslation, token);
            assistantProfile = _localTranslator.CreateProfile(
                translationProfile.Settings.TargetLanguage, localSession.BaseUri);
        }
        else
        {
            assistantProfile = routing.OnlineProfile!;
        }
        var includeImage = !routing.UseLocal &&
                           ShouldIncludeAssistantImage(topic.Selection.StructureType, question) &&
                           assistantProfile.Settings.IsMultimodal;
        var pageTranslation = pageIndex == _session.CurrentPageIndex ? _currentTranslationMarkdown : string.Empty;
        var progress = new Progress<ReaderAnswerUpdate>(update => _ = _markdownReader.UpdateAnswerAsync(update));
        try
        {
            await _readerAssistant.AskAsync(topicId, mode, question, pageTranslation, data.SourceText,
                context, includeImage ? data.Render.EncodedImage : ReadOnlyMemory<byte>.Empty,
                data.Render.ImageMediaType, assistantProfile, progress, token);
            await _markdownReader.SetTopicsAsync(_readerAssistant.Topics);
        }
        catch (OperationCanceledException)
        {
            await _markdownReader.ShowAssistantErrorAsync("已停止生成，可重新发送问题。");
        }
        catch (Exception ex)
        {
            AppLog.Error("AI 阅读助手请求", ex);
            await _markdownReader.ShowAssistantErrorAsync(ex.Message);
        }
        finally
        {
            localSession?.Dispose();
        }
    }

    private static bool ShouldIncludeAssistantImage(string structureType, string question)
    {
        if (structureType is "table" or "pre" or "code" or "formula") return true;
        var intent = $"{structureType} {question}";
        string[] visualWords = ["公式", "图", "表", "符号", "推导", "版面", "矩阵", "曲线", "坐标", "插图", "figure", "table"];
        return visualWords.Any(word => intent.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private async void ReaderAssistantButton_Click(object sender, RoutedEventArgs e)
    {
        await _markdownReader.SetTopicsAsync(_readerAssistant.Topics);
        SetReaderViewMode(ReaderViewMode.Assistant);
        await UpdateAssistantMetaAsync();
        await _markdownReader.ShowAssistantAsync();
    }

    /// <summary>助手模型徽章：与问答路由一致（本地 = 本机 Qwen3，不上传页面）。</summary>
    private async Task UpdateAssistantMetaAsync()
    {
        var routing = await ResolveAssistantRoutingAsync();
        var model = routing.UseLocal
            ? "Qwen3 1.7B · 本地"
            : $"{routing.OnlineProfile?.DisplayName ?? "未配置"} · 在线";
        await _markdownReader.SetAssistantMetaAsync(model, routing.UseLocal);
    }

    private async void ReturnToTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        SetReaderViewMode(ReaderViewMode.Translation);
        await _markdownReader.ShowTranslationAsync();
    }

    private void SetReaderViewMode(ReaderViewMode mode)
    {
        _readerViewMode = mode;
        var assistant = mode == ReaderViewMode.Assistant;
        TranslationHeaderTitle.Visibility = assistant ? Visibility.Collapsed : Visibility.Visible;
        TranslationHeaderActions.Visibility = assistant ? Visibility.Collapsed : Visibility.Visible;
        AssistantHeaderTitle.Visibility = assistant ? Visibility.Visible : Visibility.Collapsed;
        ReturnToTranslationButton.Visibility = assistant ? Visibility.Visible : Visibility.Collapsed;
        TranslationInfoBars.Visibility = assistant ? Visibility.Collapsed : Visibility.Visible;
        TranslationTitle.Visibility = assistant ? Visibility.Collapsed : Visibility.Visible;
        SourceTextPanel.Visibility = assistant ? Visibility.Collapsed : Visibility.Visible;
        if (assistant)
        {
            // 进入助手时必须让 WebView 可见：页面处理消息（如"正在识别…""等待配置"）会把
            // WebView 收起、改显 XAML 文案；不恢复的话助手视图被文案盖住，看起来"功能没了"。
            TranslationBody.Visibility = Visibility.Collapsed;
            TranslationWebView.Visibility = Visibility.Visible;
        }
    }

    private void SetTranslationState(string text, TranslationVisualState state)
    {
        TranslationStateText.Text = text;
        TranslationProgress.IsActive = state == TranslationVisualState.Working;
        TranslationProgress.Visibility = state == TranslationVisualState.Working
            ? Visibility.Visible : Visibility.Collapsed;
        TranslationStateIcon.Visibility = state == TranslationVisualState.Working
            ? Visibility.Collapsed : Visibility.Visible;
        TranslationStateIcon.Glyph = state switch
        {
            TranslationVisualState.Completed => "\uE73E",
            TranslationVisualState.Cached => "\uE823",
            TranslationVisualState.Error => "\uEA39",
            _ => "\uE8A5"
        };
    }

    private void ShowTranslationMessage(string message)
    {
        if (_readerViewMode == ReaderViewMode.Assistant)
        {
            SetReaderViewMode(ReaderViewMode.Translation);
            _ = _markdownReader.ShowTranslationAsync();
        }
        TranslationWebView.Visibility = Visibility.Collapsed;
        TranslationBody.Visibility = Visibility.Visible;
        TranslationBody.Text = message;
        _ = _markdownReader.ClearAsync();
    }

    private void ShowPageError(string title, string message)
    {
        PageErrorBar.Title = title;
        PageErrorBar.Message = message;
        PageErrorBar.IsOpen = true;
    }

    private async void SwitchToOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        LocalTranslationHintBar.IsOpen = false;
        LocalTranslationRecoveryActions.Visibility = Visibility.Collapsed;
        await SetTranslationModeAsync(TranslationExecutionMode.Online);
    }

    // 翻译页"安装/修复本地模型"恢复按钮：打开 AI 中心（本地 AI 分区就在那里安装）。
    private void LocalAiInstallButton_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsView(true);

    private static string FormatStage(bool cacheHit, long milliseconds) =>
        cacheHit ? "缓存" : $"{milliseconds}ms";
}

internal enum ReaderViewMode
{
    Translation,
    Assistant
}

/// <summary>
/// UI 同步守卫：Enter 时占位、Dispose 时必然释放，替代裸 bool + 手写 try/finally，
/// 防止异常路径下标志永久卡位导致控件静默失响应。仅单线程（UI 线程）使用。
/// </summary>
internal sealed class SyncLatch
{
    private bool _held;

    public bool IsHeld => _held;

    public Scope Enter()
    {
        _held = true;
        return new Scope(this);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly SyncLatch? _latch;

        internal Scope(SyncLatch latch) => _latch = latch;

        public void Dispose()
        {
            if (_latch is not null) _latch._held = false;
        }
    }
}

internal enum TranslationVisualState
{
    Idle,
    Working,
    Completed,
    Cached,
    Error
}
