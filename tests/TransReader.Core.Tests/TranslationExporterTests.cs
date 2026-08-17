using System.IO;
using TransReader.Core.Ocr;
using TransReader.Core.Storage;
using TransReader.Core.Translation;

namespace TransReader.Core.Tests;

public sealed class TranslationExporterTests
{
    [Fact]
    public async Task ExportMarkdownJoinsPagesAndMarksMissingTranslations()
    {
        var root = Path.Combine(Path.GetTempPath(), "tr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var translationCache = new PageTranslationCache(Path.Combine(root, "t"));
            var settings = TranslationSettings.MiMoDefault;
            const string key = "docKey";
            await translationCache.WriteAsync(key, 0, settings, MakeResult("第一页译文"), "", default);
            await translationCache.WriteAsync(key, 2, settings, MakeResult("第三页译文"), "", default);

            var dest = Path.Combine(root, "out.md");
            await TranslationExporter.ExportMarkdownAsync(key, "书名", 3, settings, translationCache, dest, default);

            var text = await File.ReadAllTextAsync(dest);
            Assert.Contains("# 书名", text);
            Assert.Contains("第一页译文", text);
            Assert.Contains("第三页译文", text);
            Assert.Contains("（本页尚未翻译）", text); // 第 2 页（索引 1）缺失
            Assert.Contains("---", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportBilingualIncludesOcrSourceAndTranslationPerPage()
    {
        var root = Path.Combine(Path.GetTempPath(), "tr-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var translationCache = new PageTranslationCache(Path.Combine(root, "t"));
            var ocrCache = new PageOcrCache(Path.Combine(root, "o"));
            var settings = TranslationSettings.MiMoDefault;
            const string key = "docKey";
            await ocrCache.WriteAsync(key, 0,
                new OcrPage(100, 200, [new OcrBlock([[0, 0], [1, 0], [1, 1], [0, 1]], "OCR 原文内容", 0.99, 0)]), default);
            await translationCache.WriteAsync(key, 0, settings, MakeResult("中文译文"), "", default);

            var dest = Path.Combine(root, "bilingual.md");
            await TranslationExporter.ExportBilingualAsync(key, "论文", 2, settings, translationCache, ocrCache, dest, default);

            var text = await File.ReadAllTextAsync(dest);
            Assert.Contains("第 1 页", text);
            Assert.Contains("OCR 原文内容", text);
            Assert.Contains("中文译文", text);
            Assert.Contains("（本页尚未翻译）", text); // 第 2 页未缓存
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MultimodalTranslationResult MakeResult(string text) => new(text, "摘要", [], "fingerprint");
}
