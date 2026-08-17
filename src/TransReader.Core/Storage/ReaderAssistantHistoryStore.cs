using System.Text.Json;
using TransReader.Core.Translation;

namespace TransReader.Core.Storage;

public sealed class ReaderAssistantHistoryStore
{
    private const int FormatVersion = 1;
    /// <summary>单篇文献保留的助手话题上限（最新在前，超量裁剪尾部旧的）。</summary>
    public const int MaxTopicsPerDocument = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _root;

    public ReaderAssistantHistoryStore(string root) => _root = root;

    public async Task<IReadOnlyList<ReaderAssistantTopic>> ReadAsync(
        string documentKey,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(documentKey);
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<HistoryFile>(stream, JsonOptions, cancellationToken);
            return value?.Version == FormatVersion ? value.Topics : [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    public async Task WriteAsync(
        string documentKey,
        IReadOnlyList<ReaderAssistantTopic> topics,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(documentKey);
        // 话题按"最新在前"存储（调用方 Insert(0, …)）；超量时只保留最近 N 条，防止无限增长。
        var capped = topics.Count > MaxTopicsPerDocument ? topics.Take(MaxTopicsPerDocument).ToList() : topics;
        await AtomicJsonFile.WriteAsync(path, new HistoryFile(FormatVersion, capped), JsonOptions, cancellationToken);
    }

    public void DeleteDocument(string documentKey)
    {
        var directory = GetDocumentDirectory(documentKey);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    public void Clear()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string GetPath(string documentKey) => Path.Combine(GetDocumentDirectory(documentKey), "topics.json");

    private string GetDocumentDirectory(string documentKey)
    {
        if (string.IsNullOrWhiteSpace(documentKey) || documentKey is "." or ".." ||
            documentKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid document key.", nameof(documentKey));
        return Path.Combine(_root, documentKey);
    }

    private sealed record HistoryFile(int Version, IReadOnlyList<ReaderAssistantTopic> Topics);
}
