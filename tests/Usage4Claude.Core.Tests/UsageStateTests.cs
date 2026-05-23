using Usage4Claude.Core.Usage;

namespace Usage4Claude.Core.Tests;

public sealed class UsageStateTests
{
    [Fact]
    public void ClaudePrimaryLimitWinsWhenBothProvidersHaveUsage()
    {
        var state = UsageState.Empty
            .WithClaude(
                new ClaudeUsageSnapshot(
                    new UsageLimit(27, null),
                    new UsageLimit(43, null),
                    null,
                    null,
                    null),
                DateTimeOffset.UnixEpoch)
            .WithCodex(
                new CodexUsageSnapshot(
                    new UsageLimit(68, null),
                    null,
                    null),
                DateTimeOffset.UnixEpoch);

        Assert.Equal(27, state.PrimaryLimit?.Percentage);
    }

    [Fact]
    public void CodexPrimaryLimitDrivesEmptyClaudeState()
    {
        var state = UsageState.Empty.WithCodex(
            new CodexUsageSnapshot(
                new UsageLimit(68, null),
                null,
                null),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(68, state.PrimaryLimit?.Percentage);
    }
}
