using TransReader.Core.Ocr;

namespace TransReader.Core.Translation;

public static class LocalTranslationChunker
{
    // 与 llama-server --ctx-size 12288 对齐的预算：2400 中文字 ≈ ≤3000 tokens + 上下文 ~1600
    // + 输出 3072 + 系统提示 ~300，稳在窗口内（旧值 4000 在上下文联发时可能超窗被静默滑窗）。
    internal const int MaximumChunkCharacters = 2400;

    public static IReadOnlyList<IReadOnlyList<OcrBlock>> Split(IReadOnlyList<OcrBlock> source)
    {
        var normalized = source
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .OrderBy(block => block.ReadingOrder)
            .SelectMany(SplitOversizedBlock)
            .ToList();
        var chunks = new List<IReadOnlyList<OcrBlock>>();
        var current = new List<OcrBlock>();
        var length = 0;
        foreach (var block in normalized)
        {
            var required = block.Text.Length + (current.Count == 0 ? 0 : 2);
            if (current.Count > 0 && length + required > MaximumChunkCharacters)
            {
                chunks.Add(current);
                current = [];
                length = 0;
                required = block.Text.Length;
            }
            current.Add(block);
            length += required;
        }
        if (current.Count > 0) chunks.Add(current);
        return chunks;
    }

    private static IEnumerable<OcrBlock> SplitOversizedBlock(OcrBlock block)
    {
        if (block.Text.Length <= MaximumChunkCharacters)
        {
            yield return block;
            yield break;
        }
        var remaining = block.Text;
        var part = 0;
        while (remaining.Length > 0)
        {
            var take = Math.Min(MaximumChunkCharacters, remaining.Length);
            if (take < remaining.Length)
            {
                var boundary = remaining.LastIndexOfAny(['\n', '.', '。', '!', '！', '?', '？'], take - 1, take);
                if (boundary >= MaximumChunkCharacters / 2) take = boundary + 1;
            }
            yield return block with { Text = remaining[..take], ReadingOrder = block.ReadingOrder * 100 + part++ };
            remaining = remaining[take..];
        }
    }
}
