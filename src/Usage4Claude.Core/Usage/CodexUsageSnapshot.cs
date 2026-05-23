namespace Usage4Claude.Core.Usage;

public sealed record CodexUsageSnapshot(
    UsageLimit? Primary,
    UsageLimit? Secondary,
    CodexCreditsSnapshot? Credits);

public sealed record CodexCreditsSnapshot(
    bool HasCredits,
    bool Unlimited,
    bool OverageLimitReached,
    bool SpendControlReached,
    decimal? Balance,
    IReadOnlyList<int>? ApproxLocalMessages,
    IReadOnlyList<int>? ApproxCloudMessages)
{
    public bool Enabled =>
        HasCredits ||
        Unlimited ||
        OverageLimitReached ||
        SpendControlReached ||
        Balance is > 0;

    public decimal? VisualPercentage =>
        OverageLimitReached || SpendControlReached
            ? 100
            : Enabled
                ? 0
                : null;
}
