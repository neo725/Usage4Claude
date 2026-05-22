namespace Usage4Claude.Core.Usage;

public sealed record UsageLimit(double Percentage, DateTimeOffset? ResetsAt);
