using Jellyfin.Plugin.Projectionist.Services;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class MaturityRankerTests
{
    [Theory]
    [InlineData("G", 20)]
    [InlineData("PG", 40)]
    [InlineData("PG-13", 60)]
    [InlineData("R", 80)]
    [InlineData("NC-17", 100)]
    [InlineData("TV-Y", 20)]
    [InlineData("TV-PG", 40)]
    [InlineData("TV-14", 60)]
    [InlineData("TV-MA", 80)]
    public void Score_MpaaRatings(string input, int expected)
    {
        Assert.Equal(expected, MaturityRanker.Score(input));
    }

    [Theory]
    [InlineData("U", 20)]
    [InlineData("12", 60)]
    [InlineData("15", 80)]
    [InlineData("18", 100)]
    public void Score_BbfcRatings(string input, int expected)
    {
        Assert.Equal(expected, MaturityRanker.Score(input));
    }

    [Theory]
    [InlineData("FSK 0", 20)]
    [InlineData("FSK 6", 30)]
    [InlineData("FSK 12", 60)]
    [InlineData("FSK 16", 80)]
    [InlineData("FSK 18", 100)]
    public void Score_GermanFskRatings(string input, int expected)
    {
        Assert.Equal(expected, MaturityRanker.Score(input));
    }

    [Theory]
    [InlineData("TP", 20)]
    [InlineData("-10", 40)]
    [InlineData("-12", 60)]
    [InlineData("-16", 80)]
    [InlineData("-18", 100)]
    public void Score_FrenchCsaRatings(string input, int expected)
    {
        Assert.Equal(expected, MaturityRanker.Score(input));
    }

    [Theory]
    [InlineData("MA15+", 80)]
    [InlineData("R18+", 100)]
    public void Score_AustralianAcbRatings(string input, int expected)
    {
        Assert.Equal(expected, MaturityRanker.Score(input));
    }

    [Theory]
    [InlineData("PG12", 40)]
    [InlineData("R15+", 80)]
    [InlineData("R18+", 100)]
    public void Score_JapaneseEirinRatings(string input, int expected)
    {
        Assert.Equal(expected, MaturityRanker.Score(input));
    }

    [Fact]
    public void Score_UnknownRating_DefaultsHighForFeature()
    {
        Assert.Equal(100, MaturityRanker.Score(""));
        Assert.Equal(100, MaturityRanker.Score(null));
    }

    [Fact]
    public void ScorePreroll_UnknownRating_DefaultsLowForPreroll()
    {
        Assert.Equal(0, MaturityRanker.ScorePreroll(""));
        Assert.Equal(0, MaturityRanker.ScorePreroll(null));
    }

    [Fact]
    public void Score_IsCaseInsensitive()
    {
        Assert.Equal(MaturityRanker.Score("pg-13"), MaturityRanker.Score("PG-13"));
    }

    [Fact]
    public void Score_TrimsWhitespace()
    {
        Assert.Equal(80, MaturityRanker.Score("  R  "));
    }

    [Fact]
    public void Score_UnrecognizedRating_ReturnsMidpoint()
    {
        Assert.Equal(50, MaturityRanker.Score("blarg"));
    }
}
