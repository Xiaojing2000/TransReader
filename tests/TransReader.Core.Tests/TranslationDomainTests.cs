using TransReader.Core.Translation;

namespace TransReader.Core.Tests;

public sealed class TranslationDomainTests
{
    [Fact]
    public void ContextFingerprint_ChangesWithDomain()
    {
        var math = new DocumentTranslationContext("摘要", [], "source", "译文", Domain: "math");
        var physics = math with { Domain = "physics" };
        var alsoMath = math with { };

        Assert.NotEqual(math.Fingerprint(), physics.Fingerprint());
        Assert.Equal(math.Fingerprint(), alsoMath.Fingerprint());
    }

    [Fact]
    public void DomainProfiles_AllContainsTenClassificationKeys()
    {
        Assert.Equal(
            new[]
            {
                "math", "computer_science", "physics", "engineering", "medicine",
                "literature", "history", "social_science", "business", "general"
            },
            TranslationDomainProfiles.All.Select(profile => profile.Key).ToArray());
    }

    [Theory]
    [InlineData("math")]
    [InlineData("computer_science")]
    public void DomainProfiles_FindReturnsNonEmptyHintForSpecificDomains(string domain)
    {
        var profile = TranslationDomainProfiles.Find(domain);

        Assert.NotNull(profile);
        Assert.Equal(domain, profile.Key);
        Assert.False(string.IsNullOrEmpty(profile.PromptHint));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("general")]
    [InlineData("astrology")]
    public void DomainProfiles_FindFallsBackToGeneralWithEmptyHint(string? domain)
    {
        var profile = TranslationDomainProfiles.Find(domain);

        Assert.NotNull(profile);
        Assert.Equal("general", profile.Key);
        Assert.Equal(string.Empty, profile.PromptHint);
    }

    [Fact]
    public void EffectiveHint_PrefersUserOverride()
    {
        try
        {
            TranslationDomainProfiles.SetOverrides(new Dictionary<string, string> { ["math"] = "自定义数学提示" });

            Assert.Equal("自定义数学提示", TranslationDomainProfiles.EffectiveHint("math"));
            Assert.Equal("自定义数学提示", TranslationDomainProfiles.OverrideFor("math"));
            // 未覆盖的领域仍用内置默认
            Assert.Equal(TranslationDomainProfiles.Find("physics")!.PromptHint,
                TranslationDomainProfiles.EffectiveHint("physics"));
            Assert.Null(TranslationDomainProfiles.OverrideFor("physics"));
        }
        finally
        {
            TranslationDomainProfiles.SetOverrides(null);
        }
    }

    [Fact]
    public void EffectiveHint_GeneralOverrideAppliesToUnknownDomain()
    {
        try
        {
            TranslationDomainProfiles.SetOverrides(new Dictionary<string, string> { ["general"] = "全局追加提示" });

            Assert.Equal("全局追加提示", TranslationDomainProfiles.EffectiveHint(null));
            Assert.Equal("全局追加提示", TranslationDomainProfiles.EffectiveHint("astrology"));
        }
        finally
        {
            TranslationDomainProfiles.SetOverrides(null);
        }
    }

    [Fact]
    public void ContextFingerprint_ChangesOnlyWhenOverrideActive()
    {
        var context = new DocumentTranslationContext("摘要", [], "source", "译文", Domain: "math");
        var baseline = context.Fingerprint();
        try
        {
            TranslationDomainProfiles.SetOverrides(new Dictionary<string, string> { ["math"] = "自定义数学提示" });
            Assert.NotEqual(baseline, context.Fingerprint());

            // 覆盖的是别的领域：math 指纹回到基线（不误伤其它领域的缓存）
            TranslationDomainProfiles.SetOverrides(new Dictionary<string, string> { ["physics"] = "物理提示" });
            Assert.Equal(baseline, context.Fingerprint());
        }
        finally
        {
            TranslationDomainProfiles.SetOverrides(null);
        }
        Assert.Equal(baseline, context.Fingerprint());
    }
}
