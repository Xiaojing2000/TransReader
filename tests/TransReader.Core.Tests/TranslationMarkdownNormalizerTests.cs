using TransReader.Core.Translation;

namespace TransReader.Core.Tests;

public sealed class TranslationMarkdownNormalizerTests
{
    [Fact]
    public void RemovesGeneratedPageHeadingAndWholeDocumentFence()
    {
        var markdown = """
            ```markdown
            # 第 37 页

            ## 定理

            设 \(x^2=1\)。
            ```
            """;

        var normalized = TranslationMarkdownNormalizer.Normalize(markdown);

        Assert.Equal("## 定理\n\n设 \\(x^2=1\\)。".Replace("\\\\", "\\"), normalized);
    }

    [Fact]
    public void PreservesNumberingAndMathWhileNormalizingWhitespace()
    {
        var markdown = "(2.) 一般而言  \r\n\r\n\r\n\r\n1. 第一项\r\n2. 第二项\r\n\r\n\\[a=b \\tag{2.1}\\]";

        var normalized = TranslationMarkdownNormalizer.Normalize(markdown);

        Assert.StartsWith("(2.) 一般而言", normalized);
        Assert.Contains("1. 第一项\n2. 第二项", normalized);
        Assert.Contains("\\tag{2.1}", normalized);
        Assert.DoesNotContain("\n\n\n", normalized);
    }

    [Theory]
    [InlineData("# Page 12\n\n正文", "正文")]
    [InlineData("## 第12页\n正文", "正文")]
    [InlineData("## 原文标题\n正文", "## 原文标题\n正文")]
    public void OnlyRemovesPageHeadings(string input, string expected)
    {
        Assert.Equal(expected, TranslationMarkdownNormalizer.Normalize(input));
    }

    [Fact]
    public void ConvertsGeneratedMarkdownImageToPlainCaption()
    {
        var normalized = TranslationMarkdownNormalizer.Normalize(
            "正文\n\n![图 2.2](https://invalid.example/figure.png)\n");

        Assert.Equal("正文\n\n图 2.2", normalized);
    }

    [Fact]
    public void MergesImageAltTextWithImmediatelyRepeatedCaption()
    {
        var normalized = TranslationMarkdownNormalizer.Normalize(
            "正文\n\n![图 2.2](image_placeholder)\n图 2.2。\n");

        Assert.Equal("正文\n\n图 2.2", normalized);
    }

    [Theory]
    [InlineData("若有 条线具有", "若有条线具有")]
    [InlineData("顶点 按 顺序 重新 标号", "顶点按顺序重新标号")]
    [InlineData("设 x 为 顶点", "设 x 为顶点")] // x 是英文，两侧空格保留边界
    [InlineData("hello world 世界", "hello world 世界")] // 纯英文段不动
    [InlineData("中文 test 中文", "中文 test 中文")] // 中英边界空格保留
    public void CollapsesSpacesBetweenCjkCharacters(string input, string expected)
    {
        Assert.Equal(expected, TranslationMarkdownNormalizer.Normalize(input));
    }
}
