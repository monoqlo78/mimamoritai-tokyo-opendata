using MimamoriTai.Core.Application;

namespace MimamoriTai.Tests;

/// <summary>
/// The rule behind "the house has gone dark". It is only ever acted on while 気象庁 has
/// emergency information out over the household's area, which is exactly why it has to be
/// hard to trip: the family is being asked to stop what they are doing and telephone.
/// </summary>
public class QuietSpellTests
{
    /// <summary>
    /// Builds a profile whose window starts at <paramref name="startHour"/> and whose last
    /// bucket is the hour currently running, matching what
    /// <see cref="ActivityService.BuildHourlyProfile"/> produces.
    /// </summary>
    private static HourlyEnergyProfile Profile(
        double todayWhPerHour,
        double usualWhPerHour,
        int startHour = 20,
        int usualDays = 13)
    {
        var today = new double[24];
        var usual = new double[24];
        Array.Fill(today, todayWhPerHour);
        Array.Fill(usual, usualWhPerHour);
        return new HourlyEnergyProfile(today, usual, [], usualDays, startHour);
    }

    [Fact]
    public void Reports_The_Hours_When_The_House_Draws_Almost_Nothing()
    {
        // Window opens at 20:00, so the last whole hour is 18:00 and the three hours
        // examined are 16, 17 and 18 -- none of them a sleeping hour.
        var quiet = RiskAssessmentService.DetectQuiet(Profile(todayWhPerHour: 10, usualWhPerHour: 200));

        Assert.NotNull(quiet);
        Assert.Equal(3, quiet.Hours);
        Assert.Equal(16, quiet.FromHour);
        Assert.Equal(30, quiet.RecentWh);
        Assert.Equal(600, quiet.UsualWh);
        Assert.Equal(5, quiet.PercentOfUsual);
    }

    [Fact]
    public void Silent_On_An_Ordinary_Afternoon()
    {
        Assert.Null(RiskAssessmentService.DetectQuiet(Profile(todayWhPerHour: 180, usualWhPerHour: 200)));
    }

    /// <summary>
    /// A dip is not an absence. The threshold is a quarter of the usual draw, so a
    /// household that merely had a slow afternoon is left alone.
    /// </summary>
    [Fact]
    public void A_Merely_Quiet_Afternoon_Is_Not_An_Empty_House()
    {
        Assert.Null(RiskAssessmentService.DetectQuiet(Profile(todayWhPerHour: 60, usualWhPerHour: 200)));
    }

    /// <summary>
    /// The house is supposed to be dark at three in the morning. Comparing those hours
    /// would fire every single night, and a nightly alert is one the family mutes --
    /// which would cost them the night it mattered.
    /// </summary>
    [Fact]
    public void Sleeping_Hours_Are_Never_Reported()
    {
        // Window opens at 06:00, so the three whole hours examined are 02, 03 and 04.
        Assert.Null(RiskAssessmentService.DetectQuiet(
            Profile(todayWhPerHour: 0, usualWhPerHour: 200, startHour: 6)));
    }

    /// <summary>
    /// Without a fortnight behind it, "usual" is a guess. Reporting a household as empty
    /// on its second day of service would teach the family the alert means nothing.
    /// </summary>
    [Fact]
    public void Needs_Enough_History_Before_Usual_Means_Anything()
    {
        Assert.Null(RiskAssessmentService.DetectQuiet(
            Profile(todayWhPerHour: 0, usualWhPerHour: 200, usualDays: 2)));
    }

    /// <summary>
    /// A household whose hours normally carry a couple of watt-hours has nothing to be
    /// absent from; without a floor the rule would report it over a rounding error.
    /// </summary>
    [Fact]
    public void Ignores_Hours_That_Never_Carried_Any_Electricity()
    {
        Assert.Null(RiskAssessmentService.DetectQuiet(Profile(todayWhPerHour: 0, usualWhPerHour: 2)));
    }

    /// <summary>
    /// The newest bucket is the hour currently running and is therefore always short of
    /// a full hour's electricity. Counting it would report every household as empty,
    /// every hour, forever.
    /// </summary>
    [Fact]
    public void The_Hour_Still_Running_Is_Left_Out_Of_The_Comparison()
    {
        var today = new double[24];
        var usual = new double[24];
        Array.Fill(usual, 200);

        // Hours 16, 17 and 18 (buckets 20-22) were ordinary; only the part-hour that is
        // still being measured looks empty.
        today[20] = 200;
        today[21] = 200;
        today[22] = 200;
        today[23] = 5;

        Assert.Null(RiskAssessmentService.DetectQuiet(
            new HourlyEnergyProfile(today, usual, [], 13, 20)));
    }
}
