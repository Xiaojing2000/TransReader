using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using TransReader.Core.Translation;

namespace TransReader.App.Services;

internal sealed class MarkdownReaderController : IDisposable
{
    private const string ReaderHost = "transreader.local";
    private readonly WebView2 _view;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _answerTimer;
    private readonly string _assetDirectory;
    private readonly string _userDataDirectory;
    private Task? _initializeTask;
    private MarkdownRenderUpdate? _pending;
    private ReaderAnswerUpdate? _pendingAnswer;
    private bool _ready;
    private int _contentVersion;
    private string _taskId = string.Empty;
    private long _taskContentVersion;
    private ReaderViewMode _viewMode = ReaderViewMode.Translation;

    public event Action<ReaderWebMessage>? ReaderMessageReceived;

    public MarkdownReaderController(WebView2 view, DispatcherQueue dispatcherQueue, string userDataDirectory)
    {
        _view = view;
        _assetDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Reader");
        _userDataDirectory = Path.GetFullPath(userDataDirectory);
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(120);
        _timer.IsRepeating = false;
        _timer.Tick += FlushPending;
        _answerTimer = dispatcherQueue.CreateTimer();
        _answerTimer.Interval = TimeSpan.FromMilliseconds(120);
        _answerTimer.IsRepeating = false;
        _answerTimer.Tick += FlushAnswerPending;
    }

    public Task InitializeAsync()
    {
        return _initializeTask ??= InitializeCoreAsync();
    }

    public void Update(MarkdownRenderUpdate update)
    {
        if (!string.IsNullOrEmpty(update.TaskId))
        {
            if (_taskId == update.TaskId && update.ContentVersion <= _taskContentVersion) return;
            if (_taskId != update.TaskId)
            {
                _taskId = update.TaskId;
                _taskContentVersion = 0;
            }
            _taskContentVersion = update.ContentVersion;
        }
        _contentVersion++;
        _pending = update;
        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    public async Task ClearAsync()
    {
        var version = ++_contentVersion;
        _taskId = string.Empty;
        _taskContentVersion = 0;
        _pending = null;
        _timer.Stop();
        await InitializeAsync();
        if (_ready && version == _contentVersion)
        {
            await _view.ExecuteScriptAsync("window.transReader.clear()");
        }
    }

    public async Task SetThemeAsync(ElementTheme theme)
    {
        await InitializeAsync();
        if (_ready)
        {
            var value = theme == ElementTheme.Dark ? "dark" : "light";
            await _view.ExecuteScriptAsync($"window.transReader.theme('{value}')");
        }
    }

    private async Task InitializeCoreAsync()
    {
        if (!Directory.Exists(_assetDirectory))
        {
            throw new DirectoryNotFoundException($"Markdown reader assets were not found: {_assetDirectory}");
        }
        Directory.CreateDirectory(_userDataDirectory);
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _userDataDirectory, null);
        await _view.EnsureCoreWebView2Async(environment);
        _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        _view.CoreWebView2.Settings.AreDevToolsEnabled = false;
        // 浏览器级加速器（如 Ctrl+F）在 WebView2 里没有对应 UI，关掉以免吞掉要转发给宿主的按键。
        _view.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _view.CoreWebView2.Settings.IsScriptEnabled = true;
        _view.CoreWebView2.SetVirtualHostNameToFolderMapping(
            ReaderHost, _assetDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
        _view.CoreWebView2.NavigationStarting += NavigationStarting;
        _view.CoreWebView2.WebMessageReceived += WebMessageReceived;
        _view.Source = new Uri($"https://{ReaderHost}/reader.html");
    }

    private void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.Uri.StartsWith($"https://{ReaderHost}/", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
        }
    }

    private async void WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var root = json.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "ready")
            {
                _ready = true;
                if (_pending is not null) await FlushAsync();
                await ExecuteViewModeAsync();
            }
            else if (type == "openLink" && root.TryGetProperty("url", out var urlValue) &&
                     Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var url) &&
                     url.Scheme is "http" or "https" &&
                     !url.Host.Equals(ReaderHost, StringComparison.OrdinalIgnoreCase))
            {
                await Windows.System.Launcher.LaunchUriAsync(url);
            }
            else if (type is "selectionChanged" or "explainSelection" or "askSelection")
            {
                ReaderMessageReceived?.Invoke(new ReaderWebMessage(type,
                    SelectedText: ReadString(root, "selectedText"),
                    SurroundingText: ReadString(root, "surroundingText"),
                    StructureType: ReadString(root, "structureType"),
                    PageNumber: ReadUInt(root, "pageNumber")));
            }
            else if (type is "sendFollowUp" or "openTopic")
            {
                ReaderMessageReceived?.Invoke(new ReaderWebMessage(type,
                    TopicId: ReadString(root, "topicId"),
                    Question: ReadString(root, "question")));
            }
            else if (type == "keyDown")
            {
                ReaderMessageReceived?.Invoke(new ReaderWebMessage(type,
                    Key: ReadString(root, "key"),
                    Modifiers: ReadString(root, "modifiers")));
            }
            else if (type == "stopAnswer")
            {
                ReaderMessageReceived?.Invoke(new ReaderWebMessage(type));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Markdown 阅读器消息", ex);
        }
    }

    public Task SetPageAsync(uint pageNumber) => ExecuteAsync($"window.transReader.setPage({pageNumber})");

    public Task ShowTranslationAsync()
    {
        _viewMode = ReaderViewMode.Translation;
        return ExecuteAsync("window.transReader.showTranslation()");
    }

    public Task ShowAssistantAsync()
    {
        _viewMode = ReaderViewMode.Assistant;
        return ExecuteAsync("window.transReader.showAssistant()");
    }

    public Task SetTopicsAsync(IReadOnlyList<ReaderAssistantTopic> topics)
    {
        var payload = JsonSerializer.Serialize(topics.Select(topic => new
        {
            id = topic.Id,
            pageNumber = topic.Selection.PageNumber,
            title = topic.Title
        }));
        return ExecuteAsync($"window.transReader.setTopics({payload})");
    }

    public Task ShowTopicAsync(ReaderAssistantTopic topic)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = topic.Id,
            pageNumber = topic.Selection.PageNumber,
            selectedText = topic.Selection.SelectedText,
            messages = topic.Messages.Select(message => new { role = message.Role, markdown = message.Markdown })
        });
        return ExecuteAsync($"window.transReader.showTopic({payload})");
    }

    public Task UpdateAnswerAsync(ReaderAnswerUpdate update)
    {
        // 与译文更新一样走 120ms 合并，避免本地模型高频 chunk 触发全量 JS 重渲。
        _pendingAnswer = update;
        if (!_answerTimer.IsRunning)
        {
            _answerTimer.Start();
        }
        return Task.CompletedTask;
    }

    private async void FlushAnswerPending(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (_pendingAnswer is null) return;
            var update = _pendingAnswer;
            _pendingAnswer = null;
            var payload = JsonSerializer.Serialize(new
            {
                topicId = update.TopicId,
                markdown = update.Markdown,
                isFinal = update.IsFinal,
                contentVersion = update.ContentVersion
            });
            await ExecuteAsync($"window.transReader.updateAnswer({payload})");
        }
        catch (Exception ex)
        {
            AppLog.Error("Markdown 阅读助手渲染", ex);
        }
    }

    public Task ShowAssistantErrorAsync(string message)
    {
        var payload = JsonSerializer.Serialize(message);
        return ExecuteAsync($"window.transReader.showAssistantError({payload})");
    }

    /// <summary>助手头部的模型徽章：模型名与是否本地（本地显示"本地运行·不上传"）。</summary>
    public Task SetAssistantMetaAsync(string model, bool local)
    {
        var payload = JsonSerializer.Serialize(new { model, local });
        return ExecuteAsync($"window.transReader.setAssistantMeta({payload})");
    }

    private async Task ExecuteAsync(string script)
    {
        await InitializeAsync();
        if (_ready) await _view.ExecuteScriptAsync(script);
    }

    private async Task ExecuteViewModeAsync()
    {
        var script = _viewMode == ReaderViewMode.Assistant
            ? "window.transReader.showAssistant()"
            : "window.transReader.showTranslation()";
        await _view.ExecuteScriptAsync(script);
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static uint ReadUInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetUInt32(out var number) ? number : 0;

    private async void FlushPending(DispatcherQueueTimer sender, object args)
    {
        try { await FlushAsync(); }
        catch (Exception ex) { AppLog.Error("Markdown 渲染", ex); }
    }

    private async Task FlushAsync()
    {
        await InitializeAsync();
        if (!_ready || _pending is null) return;
        var update = _pending;
        _pending = null;
        var payload = JsonSerializer.Serialize(new
        {
            markdown = update.Markdown,
            isFinal = update.IsFinal,
            autoFollow = update.AutoFollow
        });
        await _view.ExecuteScriptAsync($"window.transReader.update({payload})");
    }

    public void Dispose()
    {
        _timer.Stop();
        _answerTimer.Stop();
        if (_view.CoreWebView2 is not null)
        {
            _view.CoreWebView2.NavigationStarting -= NavigationStarting;
            _view.CoreWebView2.WebMessageReceived -= WebMessageReceived;
        }
    }
}

internal sealed record ReaderWebMessage(
    string Type,
    string SelectedText = "",
    string SurroundingText = "",
    string StructureType = "",
    uint PageNumber = 0,
    string TopicId = "",
    string Question = "",
    string Key = "",
    string Modifiers = "");
