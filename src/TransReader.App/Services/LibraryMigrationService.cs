using System.Text.Json;
using TransReader.Core.Library;
using Windows.Data.Pdf;
using Windows.Storage;

namespace TransReader.App.Services;

internal sealed class LibraryMigrationService
{
    private readonly LibraryRepository _repository;
    private readonly LibraryIngestionService _ingestion;

    public LibraryMigrationService(LibraryRepository repository, LibraryIngestionService ingestion)
    {
        _repository = repository;
        _ingestion = ingestion;
    }

    public async Task MigrateAsync(string legacyLibraryPath, string legacyRecentPath, CancellationToken cancellationToken = default)
    {
        if (await _repository.GetMetadataFlagAsync("legacy-migration-v2", cancellationToken)) return;
        var candidates = new Dictionary<string, LegacyItem>(StringComparer.OrdinalIgnoreCase);
        await ReadLegacyLibraryAsync(legacyLibraryPath, candidates, cancellationToken);
        await ReadRecentFilesAsync(legacyRecentPath, candidates, cancellationToken);

        foreach (var item in candidates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(item.FilePath))
            {
                await _repository.AddLegacyIssueAsync(item.FilePath, item.Title, "原文件已移动或删除，需要重新定位", cancellationToken);
                continue;
            }
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                var pdf = await PdfDocument.LoadFromFileAsync(file);
                if (pdf.PageCount == 0) continue;
                var imported = await _ingestion.EnsureImportedAsync(item.FilePath, pdf.PageCount, cancellationToken);
                if (!string.IsNullOrWhiteSpace(item.Category) &&
                    !string.Equals(item.Category.Trim(), "未分类", StringComparison.OrdinalIgnoreCase))
                {
                    var folder = await _repository.EnsureFolderPathAsync([item.Category], "Migration", cancellationToken);
                    await _repository.MoveDocumentAsync(imported.Document.Id, folder.Id, manual: false, cancellationToken);
                }
                if (item.Tags.Count > 0 || !string.IsNullOrWhiteSpace(item.Annotation))
                {
                    await _repository.UpdateDocumentAsync(imported.Document.Id,
                        string.IsNullOrWhiteSpace(item.Title) ? imported.Document.Title : item.Title, "", null,
                        item.Annotation, item.Tags, LibraryReadingStatus.ToRead, false, markManualMetadata: false, cancellationToken);
                }
                if (item.LastOpenedAt is DateTime lastOpenedAt)
                    await _repository.ImportLegacyHistoryAsync(imported.Document.Id, lastOpenedAt, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _repository.AddLegacyIssueAsync(item.FilePath, item.Title, ex.Message, cancellationToken);
            }
        }
        await _repository.SetMetadataFlagAsync("legacy-migration-v2", true, cancellationToken);
    }

    private static async Task ReadLegacyLibraryAsync(string path, Dictionary<string, LegacyItem> result, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        try
        {
            await using var stream = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var filePath = GetString(item, "filePath");
                if (filePath.Length == 0) continue;
                var tags = item.TryGetProperty("tags", out var tagsValue) && tagsValue.ValueKind == JsonValueKind.Array
                    ? tagsValue.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String).Select(tag => tag.GetString() ?? "").ToArray() : [];
                result[filePath] = new LegacyItem(filePath, GetString(item, "title"), GetString(item, "category"),
                    GetString(item, "aiAnnotation"), tags, GetDateTime(item, "lastOpenedAt"));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    private static async Task ReadRecentFilesAsync(string path, Dictionary<string, LegacyItem> result, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        try
        {
            await using var stream = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var filePath = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString() ?? "",
                    JsonValueKind.Object => GetString(item, "filePath"),
                    _ => ""
                };
                if (filePath.Length > 0 && !result.ContainsKey(filePath))
                    result[filePath] = new LegacyItem(filePath, Path.GetFileNameWithoutExtension(filePath), "", "", [],
                        item.ValueKind == JsonValueKind.Object ? GetDateTime(item, "lastOpenedAt") : null);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static DateTime? GetDateTime(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(value.GetString(), out var parsed) ? parsed : null;

    private sealed record LegacyItem(
        string FilePath,
        string Title,
        string Category,
        string Annotation,
        IReadOnlyList<string> Tags,
        DateTime? LastOpenedAt);
}
