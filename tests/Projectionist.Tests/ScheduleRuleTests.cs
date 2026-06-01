using System;
using Jellyfin.Plugin.Projectionist.Models;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class ScheduleRuleTests
{
    [Fact]
    public void EmptyRule_AlwaysMatches()
    {
        var r = new ScheduleRule();
        Assert.True(r.Matches(new DateTime(2026, 1, 15, 12, 0, 0)));
        Assert.True(r.Matches(new DateTime(2026, 12, 31, 23, 59, 0)));
    }

    [Fact]
    public void Months_MatchesIncluded()
    {
        var r = new ScheduleRule { Months = new() { 10 } };
        Assert.True(r.Matches(new DateTime(2026, 10, 15, 12, 0, 0)));
        Assert.False(r.Matches(new DateTime(2026, 9, 30, 23, 59, 0)));
        Assert.False(r.Matches(new DateTime(2026, 11, 1, 0, 0, 0)));
    }

    [Fact]
    public void DaysOfWeek_MatchesOnly()
    {
        var r = new ScheduleRule { DaysOfWeek = new() { 0, 6 } };
        Assert.True(r.Matches(new DateTime(2026, 1, 4, 12, 0, 0)));
        Assert.True(r.Matches(new DateTime(2026, 1, 3, 12, 0, 0)));
        Assert.False(r.Matches(new DateTime(2026, 1, 5, 12, 0, 0)));
    }

    [Fact]
    public void HourWindow_InclusiveStart_ExclusiveEnd()
    {
        var r = new ScheduleRule { StartHour = 18, EndHour = 22 };
        Assert.False(r.Matches(new DateTime(2026, 1, 1, 17, 59, 59)));
        Assert.True(r.Matches(new DateTime(2026, 1, 1, 18, 0, 0)));
        Assert.True(r.Matches(new DateTime(2026, 1, 1, 21, 59, 59)));
        Assert.False(r.Matches(new DateTime(2026, 1, 1, 22, 0, 0)));
    }

    [Fact]
    public void DateRange_StandardForward()
    {
        var r = new ScheduleRule { DateRange = "10-15..10-31" };
        Assert.True(r.Matches(new DateTime(2026, 10, 15, 12, 0, 0)));
        Assert.True(r.Matches(new DateTime(2026, 10, 31, 23, 59, 0)));
        Assert.False(r.Matches(new DateTime(2026, 10, 14, 12, 0, 0)));
    }

    [Fact]
    public void DateRange_WrapsYearBoundary()
    {
        var r = new ScheduleRule { DateRange = "12-20..01-05" };
        Assert.True(r.Matches(new DateTime(2026, 12, 25, 12, 0, 0)));
        Assert.True(r.Matches(new DateTime(2026, 1, 1, 0, 0, 0)));
        Assert.False(r.Matches(new DateTime(2026, 6, 15, 12, 0, 0)));
    }

    [Fact]
    public void InvalidMonth_NeverMatches()
    {
        var r = new ScheduleRule { Months = new() { 13 } };
        Assert.False(r.Matches(new DateTime(2026, 1, 15, 12, 0, 0)));
    }

    [Fact]
    public void InvalidDayOfWeek_NeverMatches()
    {
        var r = new ScheduleRule { DaysOfWeek = new() { 9 } };
        Assert.False(r.Matches(new DateTime(2026, 1, 15, 12, 0, 0)));
    }

    [Fact]
    public void CombinedFilters_AllMustMatch()
    {
        var r = new ScheduleRule
        {
            Months = new() { 12 },
            DaysOfWeek = new() { 0 },
            StartHour = 18,
            EndHour = 22,
        };
        Assert.True(r.Matches(new DateTime(2026, 12, 6, 19, 0, 0)));
        Assert.False(r.Matches(new DateTime(2026, 12, 6, 17, 0, 0)));
        Assert.False(r.Matches(new DateTime(2026, 11, 1, 19, 0, 0)));
    }
}
