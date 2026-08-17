using MimamoriTai.Infrastructure.OpenData;

namespace MimamoriTai.Tests;

public class JmaDisasterAdvisoryProviderTests
{
    /// <summary>
    /// 気象庁 writes 震度 as 1..4, 5-, 5+, 6-, 6+, 7. Compared as strings "5-" sorts above
    /// "6+", which would silently drop the worst earthquakes -- exactly the ones the
    /// notice exists for.
    /// </summary>
    [Theory]
    [InlineData("1", "2")]
    [InlineData("4", "5-")]
    [InlineData("5-", "5+")]
    [InlineData("5+", "6-")]
    [InlineData("6-", "6+")]
    [InlineData("6+", "7")]
    public void Intensity_Ranks_In_The_Order_Jma_Publishes(string weaker, string stronger)
    {
        Assert.True(
            JmaDisasterAdvisoryProvider.Rank(weaker) < JmaDisasterAdvisoryProvider.Rank(stronger));
    }

    /// <summary>
    /// An unrecognised or missing 震度 ranks below every real one, so an entry we cannot
    /// read is skipped rather than pushed as if it had cleared the threshold.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不明")]
    public void Unreadable_Intensity_Never_Clears_The_Threshold(string? value)
    {
        Assert.True(JmaDisasterAdvisoryProvider.Rank(value) < JmaDisasterAdvisoryProvider.Rank("5-"));
    }
}
