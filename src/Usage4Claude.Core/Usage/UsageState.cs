namespace Usage4Claude.Core.Usage;

public sealed record UsageState(
    ClaudeUsageSnapshot? Claude,
    CodexUsageSnapshot? Codex,
    DateTimeOffset? UpdatedAt)
{
    public static UsageState Empty { get; } = new(null, null, null);

    public UsageState WithClaude(ClaudeUsageSnapshot usage, DateTimeOffset updatedAt) =>
        this with { Claude = usage, UpdatedAt = updatedAt };

    public UsageState WithCodex(CodexUsageSnapshot usage, DateTimeOffset updatedAt) =>
        this with { Codex = usage, UpdatedAt = updatedAt };

    public UsageLimit? PrimaryLimit =>
        Claude?.FiveHour ?? Codex?.Primary;
}
