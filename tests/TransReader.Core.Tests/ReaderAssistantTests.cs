using System.Text.Json;
using TransReader.Core.Storage;
using TransReader.Core.Translation;

namespace TransReader.Core.Tests;

public sealed class ReaderAssistantTests
{
    [Fact]
    public void QuestionPromptTreatsDocumentAsUntrustedAndIncludesContext()
    {
        var selection = new ReaderSelectionContext("document", 7, "所选译文", "所在段落", "paragraph");
        var context = new DocumentTranslationContext("前文摘要", [new TranslationTerm("agent", "智能体")],
            "previous source", "上一页译文");
        var request = new ReaderQuestionRequest(ReaderQuestionMode.Explain, string.Empty, selection,
            "本页译文", "page source", context, [], ReadOnlyMemory<byte>.Empty, "image/jpeg");

        var messages = OpenAiCompatibleTranslator.CreateReaderQuestionMessages(TranslationSettings.MiMoDefault, request);
        var json = JsonSerializer.Serialize(messages, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        Assert.Contains("不可信引用材料", json);
        Assert.Contains("所选译文", json);
        Assert.Contains("前文摘要", json);
        Assert.Contains("智能体", json);
        Assert.DoesNotContain("image_url", json);
    }

    [Fact]
    public void MultimodalQuestionIncludesImageOnlyWhenProvided()
    {
        var selection = new ReaderSelectionContext("document", 1, "公式", "公式段落", "table");
        var request = new ReaderQuestionRequest(ReaderQuestionMode.Ask, "如何推导？", selection,
            "译文", "source", DocumentTranslationContext.Empty, [], new byte[] { 1, 2, 3 }, "image/jpeg");

        var json = JsonSerializer.Serialize(
            OpenAiCompatibleTranslator.CreateReaderQuestionMessages(TranslationSettings.MiMoDefault, request),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        Assert.Contains("image_url", json);
        Assert.Contains("data:image/jpeg;base64,AQID", json);
    }

    [Fact]
    public async Task HistoryIsAtomicAndSeparatedByDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), $"transreader-assistant-{Guid.NewGuid():N}");
        try
        {
            var store = new ReaderAssistantHistoryStore(root);
            var selection = new ReaderSelectionContext("doc-a", 3, "选区", "段落", "paragraph");
            var topic = new ReaderAssistantTopic("topic", selection, "选区", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, [new ReaderAssistantMessage("answer", "assistant", "解释", DateTimeOffset.UtcNow)]);

            await store.WriteAsync("doc-a", [topic]);

            var reopened = new ReaderAssistantHistoryStore(root);
            Assert.Single(await reopened.ReadAsync("doc-a"));
            Assert.Empty(await reopened.ReadAsync("doc-b"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task HistoryCapsTopicsToLatestFifty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"transreader-assistant-{Guid.NewGuid():N}");
        try
        {
            var store = new ReaderAssistantHistoryStore(root);
            var selection = new ReaderSelectionContext("doc", 1, "选区", "段落", "paragraph");
            // 最新在前（index 0 最新）；写入 60 条，断言只保留前 50 条。
            var topics = Enumerable.Range(0, 60)
                .Select(i => new ReaderAssistantTopic($"t{i}", selection, $"话题{i}", DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, []))
                .ToList();

            await store.WriteAsync("doc", topics);

            var stored = await store.ReadAsync("doc");
            Assert.Equal(ReaderAssistantHistoryStore.MaxTopicsPerDocument, stored.Count);
            // 保留的是头部（最新）50 条，尾部 t50..t59 被裁掉。
            Assert.Equal("t0", stored[0].Id);
            Assert.Equal("t49", stored[^1].Id);
            Assert.DoesNotContain(stored, t => t.Id is "t50" or "t59");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
