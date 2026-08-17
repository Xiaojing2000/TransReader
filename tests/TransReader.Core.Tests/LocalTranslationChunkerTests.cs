using TransReader.Core.Ocr;
using TransReader.Core.Translation;

namespace TransReader.Core.Tests;

public sealed class LocalTranslationChunkerTests
{
    [Fact]
    public void KeepsNormalOcrBlocksWholeAndInReadingOrder()
    {
        var blocks = new[]
        {
            Block(2, new string('b', 2000)),
            Block(0, new string('a', 2000)),
            Block(1, "标题")
        };

        var chunks = LocalTranslationChunker.Split(blocks);

        Assert.Equal(2, chunks.Count);
        Assert.Equal([0, 1], chunks[0].Select(block => block.ReadingOrder));
        Assert.Equal(2, chunks[1].Single().ReadingOrder);
        Assert.All(chunks, chunk => Assert.True(string.Join("\n\n", chunk.Select(block => block.Text)).Length <= 4000));
    }

    [Fact]
    public void SplitsAnOversizedSingleBlockWithoutDroppingText()
    {
        var source = string.Concat(Enumerable.Repeat("长句内容。", 1200));

        var chunks = LocalTranslationChunker.Split([Block(0, source)]);

        Assert.True(chunks.Count > 1);
        Assert.Equal(source, string.Concat(chunks.SelectMany(chunk => chunk).Select(block => block.Text)));
        Assert.All(chunks.SelectMany(chunk => chunk), block => Assert.InRange(block.Text.Length, 1, 4000));
    }

    private static OcrBlock Block(int order, string text) => new([], text, 0.99, order);
}
