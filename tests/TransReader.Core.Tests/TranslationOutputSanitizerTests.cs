using TransReader.Core.Translation;
using Xunit;

namespace TransReader.Core.Tests;

public class TranslationOutputSanitizerTests
{
    [Fact]
    public void Sanitize_CleanText_PassesThrough()
    {
        var result = TranslationOutputSanitizer.Sanitize("这是一段正常的译文。公式 \\(a+b\\) 保留。");
        Assert.Equal("这是一段正常的译文。公式 \\(a+b\\) 保留。", result.Text);
        Assert.False(result.HadArtifacts);
        Assert.False(result.HasDegenerateRepetition);
        Assert.False(result.RequiresReview);
    }

    [Fact]
    public void Sanitize_StripsMathPlaceholders()
    {
        var result = TranslationOutputSanitizer.Sanitize("顶点按 TRMATHPLACEHOLDER0END、TRMATHPLACEHOLDER12END 重新标号。");
        Assert.Equal("顶点按 重新标号。", result.Text);
        Assert.True(result.HadArtifacts);
        Assert.True(result.RequiresReview);
    }

    [Fact]
    public void Sanitize_StripsUnfinishedTokensAndContextMarker()
    {
        var result = TranslationOutputSanitizer.Sanitize("前半 TRUNFINISHEDLPARENEND 后半 <<<TRANSREADER_CONTEXT>>>");
        Assert.Equal("前半  后半", result.Text);
        Assert.True(result.HadArtifacts);
    }

    [Fact]
    public void Sanitize_CollapsesOrphanEnumerationCommas()
    {
        var result = TranslationOutputSanitizer.Sanitize("按 TRMATHPLACEHOLDER0END、TRMATHPLACEHOLDER1END、TRMATHPLACEHOLDER2END 结束");
        Assert.DoesNotContain("、、", result.Text);
        Assert.DoesNotContain("TRMATH", result.Text);
    }

    [Fact]
    public void Sanitize_DetectsDegenerateRepetition()
    {
        var sentence = "一个图也可能与自身同构，也就是说，对其顶点重新标号后可以得到一个与之同构的图";
        var text = string.Join("。", sentence, sentence, sentence);
        var result = TranslationOutputSanitizer.Sanitize(text);
        Assert.True(result.HasDegenerateRepetition);
        Assert.True(result.RequiresReview);
    }

    [Fact]
    public void Sanitize_ShortOrInfrequentRepetition_IsNotDegenerate()
    {
        var result = TranslationOutputSanitizer.Sanitize("同构。同构。同构。一个图与自身同构称为自同构。一个图与自身同构称为自同构。");
        Assert.False(result.HasDegenerateRepetition);
    }

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TranslationOutputSanitizer.Sanitize(null).Text);
        Assert.Equal(string.Empty, TranslationOutputSanitizer.Sanitize("   ").Text);
    }

    [Fact]
    public void Sanitize_StripsPrivateUseWrappedRuns()
    {
        // 渲染层数学占位符（U+E000…U+E001，含索引数字）整段抹除。
        var input = $"设 {(char)0xE000}2{(char)0xE001} 为该集合中的任意顶点。公式 {(char)0xE000}10{(char)0xE001} 保留不了。";
        var result = TranslationOutputSanitizer.Sanitize(input);
        Assert.DoesNotContain(result.Text, character => character is >= (char)0xE000 and <= (char)0xF8FF);
        Assert.DoesNotContain("2", result.Text.Split("任意顶点")[0]);
        Assert.Contains("为该集合中的任意顶点", result.Text);
        Assert.True(result.HadArtifacts);
    }

    [Fact]
    public void Sanitize_StripsModelInventedBoxPlaceholders()
    {
        var result = TranslationOutputSanitizer.Sanitize("按 ▯0▯、▯1▯ 重新标号。");
        Assert.DoesNotContain("▯", result.Text);
        Assert.True(result.HadArtifacts);
    }

    [Fact]
    public void Sanitize_DetectsSubstringDegeneracyWithVaryingVariables()
    {
        // 变量逐次变化的同句重复（句级规则抓不到）：归一化后存在 20 字子串重复 5 次。
        var text = string.Join("", Enumerable.Range(0, 5).Select(index =>
            $"设 \\(x_{index}\\) 为该集合中的任意顶点。可以在集合中选出各个分支，每个分支包含在顶点处相交的直线。"));
        Assert.True(TranslationOutputSanitizer.Sanitize(text).HasDegenerateRepetition);
    }

    [Fact]
    public void Sanitize_LegitShortRepetition_IsNotDegenerate()
    {
        // 列表化的短单元重复属正文常态，不应误判（20 字/5 次阈值以下）。
        var text = string.Join("\n", Enumerable.Repeat("图 3.9 见下。", 6)) +
                   "\n连续性为一的集合在图论中反复出现，但并不以完全相同的长句连续堆叠。";
        Assert.False(TranslationOutputSanitizer.Sanitize(text).HasDegenerateRepetition);
    }
}
