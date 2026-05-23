namespace Usage4Claude.Core.Usage;

public sealed record ClaudeUsageSnapshot(
    UsageLimit FiveHour,
    UsageLimit SevenDay,
    UsageLimit? OpusWeekly,
    UsageLimit? SonnetWeekly,
    ClaudeExtraUsageSnapshot? ExtraUsage);

public sealed record ClaudeExtraUsageSnapshot(
    bool Enabled,
    decimal? Used,
    decimal? Limit,
    string Currency)
{
    public decimal? Percentage => Limit is > 0 && Used is not null
        ? Used.Value / Limit.Value * 100
        : null;
}
