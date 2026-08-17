using System.Text;
using TransReader.Core.Ocr;
using TransReader.Core.Storage;

namespace TransReader.Core.Translation;

/// <summary>把缓存的译文导出为 Markdown（纯译文或原文+译文双语）。</summary>
public static class TranslationExporter
{
    /// <summary>导出纯译文 Markdown，页间用分隔线隔开；未翻译页插入占位。</summary>
    public static async Task ExportMarkdownAsync(
        string documentKey,
        string title,
        uint pageCount,
        TranslationSettings settings,
        PageTranslationCache translationCache,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var pages = await translationCache.ReadAllTranslationTextAsync(documentKey, pageCount, settings, cancellationToken);
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("# ").Append(title.Trim()).Append("\n\n");
        }
        for (uint i = 0; i < pageCount; i++)
        {
            var text = pages[(int)i];
            builder.Append(text is null || text.Length == 0 ? "（本页尚未翻译）" : text);
            builder.Append("\n\n---\n\n");
        }
        await File.WriteAllTextAsync(destinationPath, builder.ToString(), cancellationToken);
    }

    /// <summary>导出双语 Markdown：每页先原文（来自 OCR 缓存）后译文。</summary>
    public static async Task ExportBilingualAsync(
        string documentKey,
        string title,
        uint pageCount,
        TranslationSettings settings,
        PageTranslationCache translationCache,
        PageOcrCache ocrCache,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var pages = await translationCache.ReadAllTranslationTextAsync(documentKey, pageCount, settings, cancellationToken);
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append("# ").Append(title.Trim()).Append("\n\n");
        }
        for (uint i = 0; i < pageCount; i++)
        {
            builder.Append($"## 第 {i + 1} 页\n\n");
            var ocr = await ocrCache.TryReadAsync(documentKey, i, cancellationToken);
            builder.Append("### 原文\n\n")
                .Append(ocr is null ? "（原文未缓存）" : JoinBlocks(ocr))
                .Append("\n\n");
            var translation = pages[(int)i];
            builder.Append("### 译文\n\n")
                .Append(string.IsNullOrEmpty(translation) ? "（本页尚未翻译）" : translation)
                .Append("\n\n---\n\n");
        }
        await File.WriteAllTextAsync(destinationPath, builder.ToString(), cancellationToken);
    }

    private static string JoinBlocks(OcrPage page) => string.Join(
        "\n\n",
        page.Blocks.OrderBy(block => block.ReadingOrder).Select(block => block.Text));
}
