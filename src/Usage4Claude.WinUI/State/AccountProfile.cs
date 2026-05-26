namespace Usage4Claude.WinUI.State;

internal sealed record AccountProfile(
    Guid Id,
    string Name,
    Guid? ClaudeAccountId,
    Guid? CodexAccountId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
