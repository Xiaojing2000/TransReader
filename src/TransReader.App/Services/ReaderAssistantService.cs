using TransReader.Core.Storage;
using TransReader.Core.Translation;

namespace TransReader.App.Services;

internal sealed class ReaderAssistantService : IDisposable
{
    private readonly ReaderAssistantHistoryStore _store;
    private readonly OpenAiCompatibleTranslator _translator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ReaderAssistantTopic> _topics = [];
    private string _documentKey = string.Empty;
    private CancellationTokenSource? _activeRequest;

    public ReaderAssistantService(ReaderAssistantHistoryStore store, OpenAiCompatibleTranslator translator)
    {
        _store = store;
        _translator = translator;
    }

    public IReadOnlyList<ReaderAssistantTopic> Topics => _topics;

    public async Task OpenDocumentAsync(string documentKey, CancellationToken cancellationToken = default)
    {
        Stop();
        _documentKey = documentKey;
        _topics.Clear();
        _topics.AddRange((await _store.ReadAsync(documentKey, cancellationToken))
            .OrderByDescending(topic => topic.UpdatedAt));
    }

    public void CloseDocument()
    {
        Stop();
        _documentKey = string.Empty;
        _topics.Clear();
    }

    public ReaderAssistantTopic CreateTopic(ReaderSelectionContext selection)
    {
        var now = DateTimeOffset.Now;
        var title = selection.SelectedText.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (title.Length > 46) title = title[..46] + "…";
        var topic = new ReaderAssistantTopic(Guid.NewGuid().ToString("N"), selection, title, now, now, []);
        _topics.Insert(0, topic);
        return topic;
    }

    public async Task AskAsync(
        string topicId,
        ReaderQuestionMode mode,
        string question,
        string pageTranslation,
        string pageSourceText,
        DocumentTranslationContext documentContext,
        ReadOnlyMemory<byte> pageImage,
        string imageMediaType,
        TranslationProfile profile,
        IProgress<ReaderAnswerUpdate>? progress,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Stop();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeRequest = linked;
            var topic = _topics.FirstOrDefault(value => value.Id == topicId)
                ?? throw new InvalidOperationException("阅读助手话题不存在。");
            var userText = string.IsNullOrWhiteSpace(question) ? "解释所选内容" : question.Trim();
            var now = DateTimeOffset.Now;
            var user = new ReaderAssistantMessage(Guid.NewGuid().ToString("N"), "user", userText, now,
                Model: profile.Settings.Model);
            var history = topic.Messages.Concat([user]).ToList();
            ReplaceTopic(topic with { Messages = history, UpdatedAt = now });

            long version = 0;
            var streamProgress = new InlineProgress<string>(markdown =>
                progress?.Report(new ReaderAnswerUpdate(topicId, markdown, false, ++version)));
            var request = new ReaderQuestionRequest(mode, question, topic.Selection, pageTranslation,
                pageSourceText, documentContext, topic.Messages.TakeLast(16).ToList(), pageImage, imageMediaType);
            var answer = await _translator.AnswerReaderQuestionStreamingAsync(profile.Settings, profile.ApiKey,
                request, streamProgress, linked.Token);
            linked.Token.ThrowIfCancellationRequested();

            var assistant = new ReaderAssistantMessage(Guid.NewGuid().ToString("N"), "assistant", answer,
                DateTimeOffset.Now, Model: profile.Settings.Model);
            var completed = _topics.First(value => value.Id == topicId) with
            {
                Messages = history.Concat([assistant]).ToList(),
                UpdatedAt = DateTimeOffset.Now
            };
            ReplaceTopic(completed);
            await _store.WriteAsync(_documentKey, _topics, linked.Token);
            progress?.Report(new ReaderAnswerUpdate(topicId, answer, true, ++version));
        }
        finally
        {
            _activeRequest?.Dispose();
            _activeRequest = null;
            _gate.Release();
        }
    }

    public void Stop() => _activeRequest?.Cancel();

    public void DeleteDocument(string documentKey) => _store.DeleteDocument(documentKey);
    public void Clear() => _store.Clear();

    private void ReplaceTopic(ReaderAssistantTopic topic)
    {
        var index = _topics.FindIndex(value => value.Id == topic.Id);
        if (index >= 0) _topics.RemoveAt(index);
        _topics.Insert(0, topic);
    }

    public void Dispose()
    {
        Stop();
        _activeRequest?.Dispose();
        _gate.Dispose();
    }
}
