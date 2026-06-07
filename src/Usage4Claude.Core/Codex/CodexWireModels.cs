using System.Globalization;
using System.Text.Json.Serialization;
using Usage4Claude.Core.Usage;

namespace Usage4Claude.Core.Codex;

public sealed class CodexSessionResponse
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("user")]
    public CodexUserResponse? User { get; init; }
}

public sealed class CodexUserResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }
}

public sealed class CodexUsageResponse
{
    [JsonPropertyName("rate_limit")]
    public CodexRateLimitResponse? RateLimit { get; init; }

    [JsonPropertyName("credits")]
    public CodexCreditsResponse? Credits { get; init; }

    [JsonPropertyName("spend_control")]
    public CodexSpendControlResponse? SpendControl { get; init; }

    public CodexUsageSnapshot ToSnapshot(DateTimeOffset now) =>
        new(
            RateLimit?.PrimaryWindow?.ToUsageLimit(now),
            ToSecondaryUsageLimit(RateLimit?.SecondaryWindow, now),
            Credits?.ToSnapshot(SpendControl?.Reached ?? false));

    private static UsageLimit? ToSecondaryUsageLimit(CodexWindowResponse? value, DateTimeOffset now) =>
        value is null || value.UsedPercent == 0 && value.ResetAt is null && value.ResetAfterSeconds is null
            ? null
            : value.ToUsageLimit(now);
}

public sealed class CodexRateLimitResponse
{
    [JsonPropertyName("primary_window")]
    public CodexWindowResponse? PrimaryWindow { get; init; }

    [JsonPropertyName("secondary_window")]
    public CodexWindowResponse? SecondaryWindow { get; init; }
}

public sealed class CodexWindowResponse
{
    [JsonPropertyName("used_percent")]
    public double UsedPercent { get; init; }

    [JsonPropertyName("reset_after_seconds")]
    public int? ResetAfterSeconds { get; init; }

    [JsonPropertyName("reset_at")]
    public long? ResetAt { get; init; }

    public UsageLimit ToUsageLimit(DateTimeOffset now)
    {
        DateTimeOffset? resetsAt = ResetAt is not null
            ? DateTimeOffset.FromUnixTimeSeconds(ResetAt.Value)
            : ResetAfterSeconds is not null
                ? now.AddSeconds(ResetAfterSeconds.Value)
                : null;

        var windowDuration = ResetAfterSeconds is not null
            ? TimeSpan.FromSeconds(ResetAfterSeconds.Value)
            : (TimeSpan?)null;

        return new UsageLimit(UsedPercent, resetsAt, windowDuration);
    }
}

public sealed class CodexCreditsResponse
{
    [JsonPropertyName("has_credits")]
    public bool? HasCredits { get; init; }

    [JsonPropertyName("unlimited")]
    public bool? Unlimited { get; init; }

    [JsonPropertyName("overage_limit_reached")]
    public bool? OverageLimitReached { get; init; }

    [JsonPropertyName("balance")]
    public string? Balance { get; init; }

    [JsonPropertyName("approx_local_messages")]
    public IReadOnlyList<int>? ApproxLocalMessages { get; init; }

    [JsonPropertyName("approx_cloud_messages")]
    public IReadOnlyList<int>? ApproxCloudMessages { get; init; }

    public CodexCreditsSnapshot ToSnapshot(bool spendControlReached) =>
        new(
            HasCredits ?? false,
            Unlimited ?? false,
            OverageLimitReached ?? false,
            spendControlReached,
            ParseBalance(Balance),
            ApproxLocalMessages,
            ApproxCloudMessages);

    private static decimal? ParseBalance(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance)
            ? balance
            : null;
}

public sealed class CodexSpendControlResponse
{
    [JsonPropertyName("reached")]
    public bool? Reached { get; init; }
}
