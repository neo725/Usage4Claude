namespace Usage4Claude.WinUI.State;

internal enum DisplayMode
{
    Smart,
    Custom,
}

internal enum DetailTimeMode
{
    ResetTime,
    TimeRemaining,
}

internal enum AppAppearance
{
    System,
    Light,
    Dark,
}

internal enum ProgressBarMode
{
    Usage,
    TimeElapsed,
}

internal sealed record DisplaySettings(
    DisplayMode DisplayMode,
    bool ShowFiveHour,
    bool ShowSevenDay,
    bool ShowExtraUsage,
    bool ShowOpus,
    bool ShowSonnet,
    bool ShowCodexPrimary,
    bool ShowCodexSecondary,
    bool ShowCodexCredits,
    bool UseColoredTheme,
    DetailTimeMode DetailTimeMode,
    AppAppearance Appearance,
    ProgressBarMode ProgressBarMode = ProgressBarMode.Usage)
{
    public static DisplaySettings Default { get; } = new(
        DisplayMode.Smart,
        ShowFiveHour: true,
        ShowSevenDay: true,
        ShowExtraUsage: true,
        ShowOpus: true,
        ShowSonnet: true,
        ShowCodexPrimary: true,
        ShowCodexSecondary: true,
        ShowCodexCredits: true,
        UseColoredTheme: false,
        DetailTimeMode: DetailTimeMode.ResetTime,
        Appearance: AppAppearance.System,
        ProgressBarMode: ProgressBarMode.Usage);
}
