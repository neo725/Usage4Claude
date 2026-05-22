using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Usage4Claude.Core.Usage;

namespace Usage4Claude.WinUI.Tray;

public sealed partial class TrayDetailWindow : Window
{
    private readonly Action _openProbe;
    private readonly Func<Task> _refreshUsage;

    public TrayDetailWindow(string iconPath, Action openProbe, Func<Task> refreshUsage)
    {
        InitializeComponent();
        _openProbe = openProbe;
        _refreshUsage = refreshUsage;
        AppWindow.SetIcon(iconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(348, 318));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
    }

    private void OpenProbe_Click(object sender, RoutedEventArgs e)
    {
        HideDetail();
        _openProbe();
    }

    private void HideDetail_Click(object sender, RoutedEventArgs e)
    {
        HideDetail();
    }

    private async void RefreshUsage_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await _refreshUsage();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    public void HideDetail()
    {
        AppWindow.Hide();
    }

    public void UpdateUsage(UsageState state)
    {
        UpdatedAtText.Text = state.UpdatedAt is null
            ? "Sign in and refresh usage"
            : $"Updated {state.UpdatedAt.Value.ToLocalTime():t}";

        UpdateClaude(state.Claude);
        UpdateCodex(state.Codex);
    }

    private void UpdateClaude(ClaudeUsageSnapshot? usage)
    {
        ClaudeEmptyText.Visibility = usage is null ? Visibility.Visible : Visibility.Collapsed;
        ClaudeUsagePanel.Visibility = usage is null ? Visibility.Collapsed : Visibility.Visible;
        if (usage is null)
        {
            return;
        }

        ClaudePrimaryText.Text = FormatPercentage(usage.FiveHour);
        ClaudePrimaryResetText.Text = FormatReset(usage.FiveHour);
        ClaudeSecondaryText.Text = FormatPercentage(usage.SevenDay);
        ClaudeSecondaryResetText.Text = FormatReset(usage.SevenDay);
    }

    private void UpdateCodex(CodexUsageSnapshot? usage)
    {
        CodexEmptyText.Visibility = usage is null ? Visibility.Visible : Visibility.Collapsed;
        CodexUsagePanel.Visibility = usage is null ? Visibility.Collapsed : Visibility.Visible;
        if (usage is null)
        {
            return;
        }

        CodexPrimaryText.Text = FormatPercentage(usage.Primary);
        CodexPrimaryResetText.Text = FormatReset(usage.Primary);
        CodexSecondaryText.Text = FormatPercentage(usage.Secondary);
        CodexSecondaryResetText.Text = FormatReset(usage.Secondary);
    }

    private static string FormatPercentage(UsageLimit? limit) =>
        limit is null ? "--" : $"{limit.Percentage:0.#}%";

    private static string FormatReset(UsageLimit? limit) =>
        limit?.ResetsAt is null ? "Reset unavailable" : $"Resets {limit.ResetsAt.Value.ToLocalTime():g}";
}
