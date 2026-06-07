using System.Text.Json.Serialization;
using Usage4Claude.Core.Usage;

namespace Usage4Claude.Core.Claude;

public sealed class ClaudeOrganizationResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string>? Capabilities { get; init; }

    public ClaudeOrganization ToOrganization() => new(Id, Uuid, Name, Capabilities);
}

public sealed class ClaudeUsageResponse
{
    [JsonPropertyName("five_hour")]
    public required ClaudeLimitResponse FiveHour { get; init; }

    [JsonPropertyName("seven_day")]
    public ClaudeLimitResponse? SevenDay { get; init; }

    [JsonPropertyName("seven_day_opus")]
    public ClaudeLimitResponse? OpusWeekly { get; init; }

    [JsonPropertyName("seven_day_sonnet")]
    public ClaudeLimitResponse? SonnetWeekly { get; init; }

    public ClaudeUsageSnapshot ToSnapshot(ClaudeExtraUsageSnapshot? extraUsage = null) =>
        new(
            FiveHour.ToUsageLimit(TimeSpan.FromHours(5)),
            SevenDay?.ToUsageLimit(TimeSpan.FromDays(7)) ?? new UsageLimit(0, null, null),
            ToOptionalModelLimit(OpusWeekly),
            ToOptionalModelLimit(SonnetWeekly),
            extraUsage);

    private static UsageLimit? ToOptionalModelLimit(ClaudeLimitResponse? value) =>
        value is null || value.Utilization == 0 && value.ResetsAt is null
            ? null
            : value.ToUsageLimit(TimeSpan.FromDays(7));
}

public sealed class ClaudeLimitResponse
{
    [JsonPropertyName("utilization")]
    public double Utilization { get; init; }

    [JsonPropertyName("resets_at")]
    public DateTimeOffset? ResetsAt { get; init; }

    public UsageLimit ToUsageLimit(TimeSpan? windowDuration = null) =>
        new(Utilization, RoundToNearestSecond(ResetsAt), windowDuration);

    private static DateTimeOffset? RoundToNearestSecond(DateTimeOffset? value)
    {
        if (value is null)
        {
            return null;
        }

        var roundedMilliseconds = Math.Round(value.Value.ToUnixTimeMilliseconds() / 1000d) * 1000;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)roundedMilliseconds);
    }
}

public sealed class ClaudeExtraUsageResponse
{
    [JsonPropertyName("is_enabled")]
    public bool? IsEnabled { get; init; }

    [JsonPropertyName("monthly_limit")]
    public int? MonthlyLimit { get; init; }

    [JsonPropertyName("monthly_credit_limit")]
    public int? MonthlyCreditLimit { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("used_credits")]
    public decimal? UsedCredits { get; init; }

    [JsonPropertyName("spend_limit_currency")]
    public string? LegacyCurrency { get; init; }

    [JsonPropertyName("spend_limit_amount_cents")]
    public int? LegacyLimitCents { get; init; }

    [JsonPropertyName("balance_cents")]
    public int? LegacyUsedCents { get; init; }

    public ClaudeExtraUsageSnapshot ToSnapshot()
    {
        var currency = (Currency ?? LegacyCurrency ?? "USD").ToUpperInvariant();
        var limitCents = MonthlyLimit ?? MonthlyCreditLimit ?? LegacyLimitCents;
        var enabled = IsEnabled ?? limitCents is > 0;

        if (!enabled || limitCents is not > 0)
        {
            return new ClaudeExtraUsageSnapshot(false, null, null, currency);
        }

        var usedCents = UsedCredits ?? LegacyUsedCents ?? 0;
        return new ClaudeExtraUsageSnapshot(
            true,
            usedCents / 100,
            limitCents.Value / 100m,
            currency);
    }
}
