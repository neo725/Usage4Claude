namespace Usage4Claude.WinUI.State;

internal sealed record ProviderAccount(
    Guid Id,
    ProviderKind Provider,
    string DisplayName,
    string? StableId,
    ProviderSessionSource SessionSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);
