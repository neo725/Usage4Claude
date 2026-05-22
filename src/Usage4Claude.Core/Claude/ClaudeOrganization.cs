namespace Usage4Claude.Core.Claude;

public sealed record ClaudeOrganization(
    int Id,
    string Uuid,
    string Name,
    IReadOnlyList<string>? Capabilities);
