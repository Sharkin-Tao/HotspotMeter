using HotspotMeter.Core.Localization;
using Xunit;

namespace HotspotMeter.Tests.Localization;

public sealed class LanguageCatalogTests
{
    [Theory]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("en-US", "en-US")]
    [InlineData("en", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("EN-us", "en-US")]
    public void TryNormalize_AcceptsSupportedLanguageTags(string input, string expected)
    {
        Assert.True(LanguageCatalog.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("fr-FR")]
    [InlineData("zh-TW")]
    [InlineData("english")]
    public void TryNormalize_RejectsUnknownOrAmbiguousValues(string? input)
    {
        Assert.False(LanguageCatalog.TryNormalize(input, out _));
    }

    [Fact]
    public void Normalize_FallsBackToChinese()
    {
        Assert.Equal(LanguageCatalog.ChineseSimplified, LanguageCatalog.Normalize("fr-FR"));
        Assert.Equal(LanguageCatalog.English, LanguageCatalog.Normalize("en-US"));
    }

    [Fact]
    public void BothLanguagesExposeTheSameKeys()
    {
        var chinese = LanguageCatalog.For(LanguageCatalog.ChineseSimplified).Keys.Order().ToArray();
        var english = LanguageCatalog.For(LanguageCatalog.English).Keys.Order().ToArray();

        Assert.Equal(chinese, english);
        Assert.DoesNotContain(chinese, key => string.IsNullOrWhiteSpace(LanguageCatalog.Get(LanguageCatalog.ChineseSimplified, key)));
        Assert.DoesNotContain(english, key => string.IsNullOrWhiteSpace(LanguageCatalog.Get(LanguageCatalog.English, key)));
    }

    [Fact]
    public void PlaceholderIndexesMatchAcrossLanguages()
    {
        foreach (var key in LanguageCatalog.For(LanguageCatalog.ChineseSimplified).Keys)
        {
            var chinese = Placeholders(LanguageCatalog.Get(LanguageCatalog.ChineseSimplified, key));
            var english = Placeholders(LanguageCatalog.Get(LanguageCatalog.English, key));
            Assert.Equal(chinese, english);
        }
    }

    [Fact]
    public void UnknownKeyReturnsKeyInsteadOfThrowing()
    {
        Assert.Equal("Missing.Key", LanguageCatalog.Get(LanguageCatalog.English, "Missing.Key"));
    }

    [Fact]
    public void CoreKeysAreTranslated()
    {
        Assert.Equal("Traffic Statistics", LanguageCatalog.Get(LanguageCatalog.English, "Stats_Title"));
        Assert.Equal("流量统计", LanguageCatalog.Get(LanguageCatalog.ChineseSimplified, "Stats_Title"));
        Assert.Equal("Lock Position", LanguageCatalog.Get(LanguageCatalog.English, "Capsule_MenuPin"));
    }

    private static string[] Placeholders(string value)
    {
        var indexes = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            if (value.Contains("{" + i, StringComparison.Ordinal))
            {
                indexes.Add(i.ToString());
            }
        }

        return indexes.ToArray();
    }
}