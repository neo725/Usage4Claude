namespace Usage4Claude.WinUI.State;

internal sealed record UserPreferences(
    DisplaySettings DisplaySettings,
    bool KeepInTray,
    bool TrayDetailTopMost,
    IReadOnlyList<ProviderAccount> Accounts,
    IReadOnlyList<AccountProfile> Profiles,
    Guid? CurrentProfileId,
    Guid? CurrentClaudeAccountId,
    Guid? CurrentCodexAccountId)
{
    public static UserPreferences Default { get; } =
        new(
            DisplaySettings.Default,
            KeepInTray: true,
            TrayDetailTopMost: false,
            Accounts: Array.Empty<ProviderAccount>(),
            Profiles: Array.Empty<AccountProfile>(),
            CurrentProfileId: null,
            CurrentClaudeAccountId: null,
            CurrentCodexAccountId: null);
}
