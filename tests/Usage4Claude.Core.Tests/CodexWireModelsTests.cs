using System.Text.Json;
using Usage4Claude.Core.Codex;

namespace Usage4Claude.Core.Tests;

public sealed class CodexWireModelsTests
{
    [Fact]
    public void UsageSnapshotResolvesRelativeResetAndSkipsEmptySecondaryWindow()
    {
        var json = """
            {
              "rate_limit": {
                "primary_window": {
                  "used_percent": 34,
                  "reset_after_seconds": 90
                },
                "secondary_window": {
                  "used_percent": 0
                }
              },
              "credits": {
                "has_credits": true,
                "balance": "12.50",
                "approx_local_messages": [1, 2]
              },
              "spend_control": {
                "reached": false
              }
            }
            """;
        var now = DateTimeOffset.Parse("2026-05-22T00:00:00+00:00");

        var snapshot = Deserialize<CodexUsageResponse>(json).ToSnapshot(now);

        Assert.Equal(34, snapshot.Primary?.Percentage);
        Assert.Equal(now.AddSeconds(90), snapshot.Primary?.ResetsAt);
        Assert.Null(snapshot.Secondary);
        Assert.True(snapshot.Credits?.Enabled);
        Assert.Equal(12.50m, snapshot.Credits?.Balance);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("JSON fixture did not deserialize.");
}
