using System.Text.Json;
using TransReader.Core.Storage;

namespace TransReader.Core.Translation;

public sealed record TranslationUsageEntry(string ProviderId, string Model, DateOnly Date, int PromptTokens, int CompletionTokens);

public sealed record ModelUsageSummary(string Model, int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

public sealed record TranslationUsageSummary(int TodayTotalTokens, int TotalTokens, IReadOnlyList<ModelUsageSummary> Models);

/// <summary>翻译 token 用量统计（仅在线）。按 {provider, model, 日期} 累加，原子落盘。</summary>
public sealed class TranslationUsageStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<TranslationUsageEntry> _entries;

    public TranslationUsageStore(string storeRoot)
    {
        _path = Path.Combine(storeRoot, "usage.json");
        _entries = Load();
    }

    private List<TranslationUsageEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new List<TranslationUsageEntry>();
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<List<TranslationUsageEntry>>(stream, Options) ?? new List<TranslationUsageEntry>();
        }
        catch (IOException) { return new List<TranslationUsageEntry>(); }
        catch (JsonException) { return new List<TranslationUsageEntry>(); }
    }

    public async Task RecordAsync(
        string providerId, string model, TranslationUsage usage, CancellationToken cancellationToken = default)
    {
        if (usage is null || string.IsNullOrEmpty(model)) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var index = _entries.FindIndex(e => e.ProviderId == providerId && e.Model == model && e.Date == today);
            if (index < 0)
            {
                _entries.Add(new TranslationUsageEntry(providerId, model, today, usage.PromptTokens, usage.CompletionTokens));
            }
            else
            {
                _entries[index] = _entries[index] with
                {
                    PromptTokens = _entries[index].PromptTokens + usage.PromptTokens,
                    CompletionTokens = _entries[index].CompletionTokens + usage.CompletionTokens
                };
            }
            await AtomicJsonFile.WriteAsync(_path, _entries, Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public TranslationUsageSummary GetSummary()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var todayTotal = _entries.Where(e => e.Date == today).Sum(e => e.PromptTokens + e.CompletionTokens);
        var total = _entries.Sum(e => e.PromptTokens + e.CompletionTokens);
        var models = _entries
            .GroupBy(e => e.Model)
            .Select(g => new ModelUsageSummary(g.Key, g.Sum(e => e.PromptTokens), g.Sum(e => e.CompletionTokens)))
            .OrderByDescending(m => m.TotalTokens)
            .ToList();
        return new TranslationUsageSummary(todayTotal, total, models);
    }
}
