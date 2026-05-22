using System.Text.Json;
using Usage4Claude.Core.Claude;

namespace Usage4Claude.Core.Tests;

public sealed class ClaudeWireModelsTests
{
    [Fact]
    public void UsageSnapshotKeepsSevenDayPlaceholderAndRoundsResetTime()
    {
        var json = """
            {
              "five_hour": {
                "utilization": 58.2,
                "resets_at": "2026-05-22T10:59:59.645+00:00"
              },
              "seven_day": null,
              "seven_day_opus": { "utilization": 0, "resets_at": null },
              "seven_day_sonnet": { "utilization": 81, "resets_at": "2026-05-26T03:00:00+00:00" }
            }
            """;

        var snapshot = Deserialize<ClaudeUsageResponse>(json).ToSnapshot();

        Assert.Equal(58.2, snapshot.FiveHour.Percentage);
        Assert.Equal(DateTimeOffset.Parse("2026-05-22T11:00:00+00:00"), snapshot.FiveHour.ResetsAt);
        Assert.Equal(0, snapshot.SevenDay.Percentage);
        Assert.Null(snapshot.OpusWeekly);
        Assert.Equal(81, snapshot.SonnetWeekly?.Percentage);
    }

    [Fact]
    public void ExtraUsageConvertsCreditCentsToCurrencyAmount()
    {
        var json = """
            {
              "is_enabled": true,
              "monthly_credit_limit": 2000,
              "used_credits": 215.5,
              "currency": "eur"
            }
            """;

        var extra = Deserialize<ClaudeExtraUsageResponse>(json).ToSnapshot();

        Assert.True(extra.Enabled);
        Assert.Equal(20m, extra.Limit);
        Assert.Equal(2.155m, extra.Used);
        Assert.Equal("EUR", extra.Currency);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("JSON fixture did not deserialize.");
}
