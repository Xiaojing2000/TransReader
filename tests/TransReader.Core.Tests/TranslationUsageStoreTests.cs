using System.IO;
using TransReader.Core.Translation;

namespace TransReader.Core.Tests;

public sealed class TranslationUsageStoreTests
{
    [Fact]
    public async Task RecordsAccumulatePerDayPerModelAndPersistAcrossInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "tr-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new TranslationUsageStore(root);
            await store.RecordAsync("mimo", "mimo-v2.5", new TranslationUsage(10, 20));
            await store.RecordAsync("mimo", "mimo-v2.5", new TranslationUsage(5, 7));

            // 重新加载，验证原子落盘后仍可读取累计值。
            var reloaded = new TranslationUsageStore(root);
            var summary = reloaded.GetSummary();

            Assert.Equal(42, summary.TodayTotalTokens);
            Assert.Equal(42, summary.TotalTokens);
            var model = Assert.Single(summary.Models);
            Assert.Equal("mimo-v2.5", model.Model);
            Assert.Equal(15, model.PromptTokens);
            Assert.Equal(27, model.CompletionTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordsSeparateModelsAndProvidersIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), "tr-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new TranslationUsageStore(root);
            await store.RecordAsync("mimo", "mimo-v2.5", new TranslationUsage(10, 20));
            await store.RecordAsync("deepseek", "deepseek-v4-flash", new TranslationUsage(100, 200));

            var summary = store.GetSummary();
            Assert.Equal(330, summary.TotalTokens);
            Assert.Equal(2, summary.Models.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
