using Gitberth.Credentials;

namespace Gitberth.Tests;

/// <summary>
/// When a token about to expire is told about: the lowest threshold crossed (21, 7 or 1 days; an API token's 7 or 1),
/// once each, never two warnings within a day, the expiry the moment it passes; a token just saved counts the thresholds
/// it's past as told; and how far off it is, as the banners say it.
/// </summary>
public sealed class ExpiryRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<int> External = ExpiryRule.External;

    private static DateTimeOffset In(double days) => Now + TimeSpan.FromDays(days);

    [Fact]
    public void A_token_further_off_than_3_weeks_tells_nothing_and_one_within_them_tells_the_lowest_threshold_once()
    {
        Assert.Equal(((int?)null, (int?)null, (DateTimeOffset?)null), ExpiryRule.Next(In(30), Now, External, null, null));
        Assert.Null(ExpiryRule.Next(null, Now, External, null, null).Tell);

        // 20 days out: the 3 weeks' warning, once.
        var told = ExpiryRule.Next(In(20), Now, External, null, null);
        Assert.Equal(((int?)21, (int?)21, (DateTimeOffset?)Now), told);
        Assert.Null(ExpiryRule.Next(In(20), Now.AddHours(2), External, told.WarnedDays, told.WarnedAt).Tell);

        // 6 days out, never told: the week's only, not 3 weeks' then a week's.
        Assert.Equal(7, ExpiryRule.Next(In(6), Now, External, null, null).Tell);
        // Half a day out: the day's.
        Assert.Equal(1, ExpiryRule.Next(In(0.5), Now, External, null, null).Tell);
    }

    [Fact]
    public void Two_warnings_never_come_within_a_day_but_the_expiry_is_told_at_once()
    {
        // The 3 weeks' warning went out 10 hours ago, and the token is within the week now: told tomorrow, not now.
        var warnedAt = Now.AddHours(-10);
        Assert.Null(ExpiryRule.Next(In(6), Now, External, 21, warnedAt).Tell);
        Assert.Equal(7, ExpiryRule.Next(In(6), warnedAt.AddDays(1), External, 21, warnedAt).Tell);

        // The day's warning went out an hour ago; the expiry passes now: told now, once.
        var expired = ExpiryRule.Next(Now, Now, External, 1, Now.AddHours(-1));
        Assert.Equal(((int?)0, (int?)0, (DateTimeOffset?)Now), expired);
        Assert.Null(ExpiryRule.Next(Now, Now.AddDays(3), External, expired.WarnedDays, expired.WarnedAt).Tell);
    }

    [Fact]
    public void A_token_just_saved_counts_what_its_past_as_told_and_one_found_otherwise_tells_its_threshold_once()
    {
        // Saved 10 days from its expiry: the 3 weeks' warning counts as told, so the week's comes when it's due, the
        // moment it's crossed (no day's wait after a save).
        var marked = ExpiryRule.Saved(In(10), Now, External);
        Assert.Equal(21, marked);
        Assert.Null(ExpiryRule.Next(In(10), Now, External, marked, null).Tell);
        Assert.Equal(7, ExpiryRule.Next(In(10), Now.AddDays(3), External, marked, null).Tell);
        Assert.Null(ExpiryRule.Saved(In(40), Now, External));
        Assert.Null(ExpiryRule.Saved(null, Now, External));

        // Found 5 days from its expiry (the first look after an upgrade): told once, at its threshold.
        Assert.Equal(7, ExpiryRule.Next(In(5), Now, External, null, null).Tell);
    }

    [Fact]
    public void An_api_token_is_told_a_week_and_a_day_before_never_3_weeks()
    {
        Assert.Null(ExpiryRule.Next(In(20), Now, ExpiryRule.ApiToken, null, null).Tell);
        Assert.Equal(7, ExpiryRule.Next(In(6), Now, ExpiryRule.ApiToken, null, null).Tell);
        Assert.Equal(1, ExpiryRule.Next(In(0.9), Now, ExpiryRule.ApiToken, 7, Now.AddDays(-5)).Tell);
        Assert.Equal(0, ExpiryRule.Next(Now.AddMinutes(-1), Now, ExpiryRule.ApiToken, 1, Now.AddHours(-23)).Tell);
    }

    [Fact]
    public void The_next_crossing_is_the_soonest_threshold_or_the_expiry_ahead()
    {
        Assert.Equal(In(30) - TimeSpan.FromDays(21), ExpiryRule.NextCrossing(In(30), Now, External));
        Assert.Equal(In(6) - TimeSpan.FromDays(1), ExpiryRule.NextCrossing(In(6), Now, External));
        Assert.Equal(In(0.5), ExpiryRule.NextCrossing(In(0.5), Now, External));
        // At a threshold's very moment, the next is the one after it.
        Assert.Equal(In(7), ExpiryRule.NextCrossing(In(7), In(7) - TimeSpan.FromDays(1), External));
        Assert.Null(ExpiryRule.NextCrossing(Now, Now, External));
        Assert.Null(ExpiryRule.NextCrossing(null, Now, External));
    }

    [Theory]
    [InlineData(21.0, "in 3 weeks")]
    [InlineData(20.5, "in 3 weeks")]
    [InlineData(15.0, "in 15 days")]
    [InlineData(14.0, "in 2 weeks")]
    [InlineData(7.0, "in 7 days")]
    [InlineData(5.1, "in 6 days")]
    [InlineData(1.5, "in 2 days")]
    public void How_far_off_an_expiry_is_counts_whole_days_rounded_up(double days, string said) =>
        Assert.Equal(said, CredentialText.When(In(days), Now));

    [Fact]
    public void The_last_day_is_today_or_tomorrow_by_the_calendar()
    {
        // Noon now: an expiry at midnight is tomorrow, one this evening is today.
        Assert.Equal("tomorrow", CredentialText.When(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), Now));
        Assert.Equal("today", CredentialText.When(new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero), Now));
        Assert.Equal(6, ExpiryRule.DaysLeft(In(5.1), Now));
        Assert.Equal(0, ExpiryRule.DaysLeft(Now, Now));
    }
}
