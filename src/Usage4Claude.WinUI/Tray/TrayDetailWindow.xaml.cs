using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Usage4Claude.Core.Usage;
using Usage4Claude.WinUI.State;
using WinRT.Interop;

namespace Usage4Claude.WinUI.Tray;

public sealed partial class TrayDetailWindow : Window
{
    private const int DetailWindowWidth = 348;
    private const int DetailWindowHeight = 395;
    private const int EdgeMargin = 12;
    private const int EdgeSnapThreshold = 15;

    private readonly Action _openProbe;
    private readonly Func<Task> _refreshUsage;
    private readonly Action<ProgressBarMode> _onProgressBarModeChanged;
    private readonly nint _windowHandle;
    private DisplaySettings _displaySettings = DisplaySettings.Default;
    private UsageState? _lastUsageState;
    private ProviderSessionState _lastSessionState = ProviderSessionState.Empty;
    private bool _isTopMost;
    private bool _isDragging;
    private bool _syncingProgressMode;
    private Point _dragStartCursor;
    private Rect _dragStartWindow;

    public bool IsTopMost => _isTopMost;

    internal TrayDetailWindow(string iconPath, bool initialTopMost, Action openProbe,
        Func<Task> refreshUsage, Action<ProgressBarMode> onProgressBarModeChanged)
    {
        InitializeComponent();
        _openProbe = openProbe;
        _refreshUsage = refreshUsage;
        _onProgressBarModeChanged = onProgressBarModeChanged;
        _windowHandle = WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(iconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(DetailWindowWidth, DetailWindowHeight));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        TopMostToggle.IsOn = initialTopMost;
        _isTopMost = initialTopMost;
        if (initialTopMost)
        {
            SetWindowTopMost(true, showWindow: false);
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

    public void ShowNearCursor()
    {
        AppWindow.Resize(new Windows.Graphics.SizeInt32(DetailWindowWidth, DetailWindowHeight));
        AppWindow.Move(GetCursorAnchoredPosition());
        AppWindow.Show();
        Activate();
        BringToForeground();
    }

    private void BringToForeground()
    {
        SetWindowPos(
            _windowHandle,
            TopMostWindow,
            0,
            0,
            0,
            0,
            SetWindowPosFlags.NoMove | SetWindowPosFlags.NoSize | SetWindowPosFlags.ShowWindow);
        SetForegroundWindow(_windowHandle);
        if (!_isTopMost)
        {
            SetWindowTopMost(false, showWindow: true);
        }
    }

    private void ProgressMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingProgressMode || TimeModeRadio is null)
        {
            return;
        }

        var mode = TimeModeRadio.IsChecked == true ? ProgressBarMode.TimeElapsed : ProgressBarMode.Usage;
        _onProgressBarModeChanged(mode);
    }

    private double ComputeBarValue(UsageLimit? limit)
    {
        if (_displaySettings.ProgressBarMode == ProgressBarMode.TimeElapsed
            && limit?.WindowDuration is not null
            && limit.ResetsAt is not null)
        {
            var start = limit.ResetsAt.Value - limit.WindowDuration.Value;
            var elapsed = DateTimeOffset.UtcNow - start;
            return Math.Clamp(
                elapsed.TotalSeconds / limit.WindowDuration.Value.TotalSeconds * 100,
                0, 100);
        }

        return limit?.Percentage ?? 0;
    }

    private void TopMostToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _isTopMost = TopMostToggle.IsOn;
        SetWindowTopMost(_isTopMost, showWindow: true);
    }

    private void DragSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(DragSurface).Properties.IsLeftButtonPressed &&
            !IsInteractiveControl(e.OriginalSource as DependencyObject) &&
            GetCursorPos(out _dragStartCursor) &&
            GetWindowRect(_windowHandle, out _dragStartWindow))
        {
            _isDragging = true;
            DragSurface.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void DragSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(DragSurface);
        if (!point.Properties.IsLeftButtonPressed || !GetCursorPos(out var cursor))
        {
            EndDrag(e);
            return;
        }

        var nextX = _dragStartWindow.Left + cursor.X - _dragStartCursor.X;
        var nextY = _dragStartWindow.Top + cursor.Y - _dragStartCursor.Y;
        SnapToEdges(ref nextX, ref nextY);
        AppWindow.Move(new Windows.Graphics.PointInt32(nextX, nextY));
        e.Handled = true;
    }

    private void DragSurface_PointerDragEnded(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(e);
    }

    private void DragSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        DragSurface.ReleasePointerCapture(e.Pointer);
        ApplyEdgeSnap();
        e.Handled = true;
    }

    private void ApplyEdgeSnap()
    {
        if (!GetWindowRect(_windowHandle, out var win))
        {
            return;
        }

        var x = win.Left;
        var y = win.Top;
        SnapToEdges(ref x, ref y, win.Right - win.Left, win.Bottom - win.Top);
        if (x != win.Left || y != win.Top)
        {
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
    }

    private void SnapToEdges(ref int x, ref int y)
    {
        if (!GetWindowRect(_windowHandle, out var win))
        {
            return;
        }

        SnapToEdges(ref x, ref y, win.Right - win.Left, win.Bottom - win.Top);
    }

    private void SnapToEdges(ref int x, ref int y, int windowWidth, int windowHeight)
    {
        var center = new Point
        {
            X = x + windowWidth / 2,
            Y = y + windowHeight / 2,
        };
        var work = GetWorkArea(center);

        if (Math.Abs(x - work.Left) <= EdgeSnapThreshold)
        {
            x = work.Left;
        }
        else if (Math.Abs(x + windowWidth - work.Right) <= EdgeSnapThreshold)
        {
            x = work.Right - windowWidth;
        }

        if (Math.Abs(y - work.Top) <= EdgeSnapThreshold)
        {
            y = work.Top;
        }
        else if (Math.Abs(y + windowHeight - work.Bottom) <= EdgeSnapThreshold)
        {
            y = work.Bottom - windowHeight;
        }
    }

    private static bool IsInteractiveControl(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button or ToggleButton)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void SetWindowTopMost(bool isTopMost, bool showWindow)
    {
        var flags = SetWindowPosFlags.NoMove | SetWindowPosFlags.NoSize;
        if (showWindow)
        {
            flags |= SetWindowPosFlags.ShowWindow;
        }

        SetWindowPos(
            _windowHandle,
            isTopMost ? TopMostWindow : NotTopMostWindow,
            0,
            0,
            0,
            0,
            flags);
    }

    private static Windows.Graphics.PointInt32 GetCursorAnchoredPosition()
    {
        if (!GetCursorPos(out var cursor))
        {
            return new Windows.Graphics.PointInt32(EdgeMargin, EdgeMargin);
        }

        var workArea = GetWorkArea(cursor);
        var x = Math.Clamp(
            cursor.X - DetailWindowWidth + EdgeMargin,
            workArea.Left + EdgeMargin,
            workArea.Right - DetailWindowWidth - EdgeMargin);
        var y = Math.Clamp(
            cursor.Y - DetailWindowHeight - EdgeMargin,
            workArea.Top + EdgeMargin,
            workArea.Bottom - DetailWindowHeight - EdgeMargin);
        return new Windows.Graphics.PointInt32(x, y);
    }

    private static Rect GetWorkArea(Point point)
    {
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
        };
        return GetMonitorInfo(monitor, ref monitorInfo)
            ? monitorInfo.WorkArea
            : new Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    internal void UpdateState(UsageState usageState, ProviderSessionState sessionState)
    {
        _lastUsageState = usageState;
        _lastSessionState = sessionState;
        RenderState();
    }

    private void RenderState()
    {
        if (_lastUsageState is null)
        {
            return;
        }

        UpdatedAtText.Text = _lastUsageState.UpdatedAt is null
            ? "Sign in and refresh usage"
            : $"Updated {_lastUsageState.UpdatedAt.Value.ToLocalTime():t}";

        UpdateClaude(_lastUsageState.Claude, _lastSessionState.Claude);
        UpdateCodex(_lastUsageState.Codex, _lastSessionState.HasCodex);
    }

    internal void UpdateDisplaySettings(DisplaySettings displaySettings)
    {
        _displaySettings = displaySettings;
        _syncingProgressMode = true;
        try
        {
            UsageModeRadio.IsChecked = displaySettings.ProgressBarMode == ProgressBarMode.Usage;
            TimeModeRadio.IsChecked = displaySettings.ProgressBarMode == ProgressBarMode.TimeElapsed;
        }
        finally
        {
            _syncingProgressMode = false;
        }

        RenderState();
    }

    private void UpdateClaude(ClaudeUsageSnapshot? usage, ProviderSessionSource sessionSource)
    {
        ClaudeEmptyText.Visibility = usage is null ? Visibility.Visible : Visibility.Collapsed;
        ClaudeUsagePanel.Visibility = usage is null ? Visibility.Collapsed : Visibility.Visible;
        if (usage is null)
        {
            ClaudeEmptyText.Text = sessionSource switch
            {
                ProviderSessionSource.WebViewCookie => "Waiting for browser refresh",
                ProviderSessionSource.CredentialLocker => "Credential loaded; browser refresh needed",
                _ => "Not signed in",
            };
            return;
        }

        ClaudeRing.PrimaryPercentage = usage.FiveHour?.Percentage ?? 0;
        ClaudeRing.SecondaryPercentage = usage.SevenDay?.Percentage ?? 0;

        var showFiveHour = ShouldShow(_displaySettings.ShowFiveHour, usage.FiveHour is not null);
        ClaudePrimaryRow.Visibility = showFiveHour ? Visibility.Visible : Visibility.Collapsed;
        ClaudePrimaryText.Text = FormatBarText(usage.FiveHour);
        ClaudePrimaryResetText.Text = FormatTime(usage.FiveHour);
        ClaudePrimaryBar.Value = ComputeBarValue(usage.FiveHour);

        var showSevenDay = ShouldShow(_displaySettings.ShowSevenDay, usage.SevenDay is not null);
        ClaudeSecondaryRow.Visibility = showSevenDay ? Visibility.Visible : Visibility.Collapsed;
        ClaudeSecondaryText.Text = FormatBarText(usage.SevenDay);
        ClaudeSecondaryResetText.Text = FormatTime(usage.SevenDay);
        ClaudeSecondaryBar.Value = ComputeBarValue(usage.SevenDay);

        var showExtra = ShouldShow(_displaySettings.ShowExtraUsage, usage.ExtraUsage?.Enabled == true);
        ClaudeExtraRow.Visibility = showExtra ? Visibility.Visible : Visibility.Collapsed;
        ClaudeExtraText.Text = FormatExtraUsage(usage.ExtraUsage);
        ClaudeExtraBar.Value = (double)(usage.ExtraUsage?.Percentage ?? 0);

        var showOpus = ShouldShow(_displaySettings.ShowOpus, usage.OpusWeekly is not null);
        ClaudeOpusRow.Visibility = showOpus ? Visibility.Visible : Visibility.Collapsed;
        ClaudeOpusText.Text = FormatBarText(usage.OpusWeekly);
        ClaudeOpusResetText.Text = FormatTime(usage.OpusWeekly);
        ClaudeOpusBar.Value = ComputeBarValue(usage.OpusWeekly);

        var showSonnet = ShouldShow(_displaySettings.ShowSonnet, usage.SonnetWeekly is not null);
        ClaudeSonnetRow.Visibility = showSonnet ? Visibility.Visible : Visibility.Collapsed;
        ClaudeSonnetText.Text = FormatBarText(usage.SonnetWeekly);
        ClaudeSonnetResetText.Text = FormatTime(usage.SonnetWeekly);
        ClaudeSonnetBar.Value = ComputeBarValue(usage.SonnetWeekly);
        ApplyIndicatorTheme(ClaudePrimaryText, ClaudeSecondaryText, ClaudeExtraText, ClaudeOpusText, ClaudeSonnetText);
    }

    private void UpdateCodex(CodexUsageSnapshot? usage, bool hasSession)
    {
        CodexEmptyText.Visibility = usage is null ? Visibility.Visible : Visibility.Collapsed;
        CodexUsagePanel.Visibility = usage is null ? Visibility.Collapsed : Visibility.Visible;
        if (usage is null)
        {
            CodexEmptyText.Text = hasSession ? "Waiting for refresh" : "Not signed in";
            return;
        }

        var showPrimary = ShouldShow(_displaySettings.ShowCodexPrimary, usage.Primary is not null);
        CodexPrimaryRow.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
        CodexPrimaryText.Text = FormatBarText(usage.Primary);
        CodexPrimaryResetText.Text = FormatTime(usage.Primary);
        CodexPrimaryBar.Value = ComputeBarValue(usage.Primary);

        var showSecondary = ShouldShow(_displaySettings.ShowCodexSecondary, usage.Secondary is not null);
        CodexSecondaryRow.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
        CodexSecondaryText.Text = FormatBarText(usage.Secondary);
        CodexSecondaryResetText.Text = FormatTime(usage.Secondary);
        CodexSecondaryBar.Value = ComputeBarValue(usage.Secondary);

        var showCredits = ShouldShow(_displaySettings.ShowCodexCredits, usage.Credits?.Enabled == true);
        CodexCreditsRow.Visibility = showCredits ? Visibility.Visible : Visibility.Collapsed;
        CodexCreditsText.Text = FormatCodexCredits(usage.Credits);
        ApplyIndicatorTheme(CodexPrimaryText, CodexSecondaryText, CodexCreditsText);
    }

    private bool ShouldShow(bool customEnabled, bool hasData) =>
        hasData && (_displaySettings.DisplayMode == DisplayMode.Smart || customEnabled);

    private static string FormatPercentage(UsageLimit? limit) =>
        limit is null ? "--" : $"{limit.Percentage:0.#}% used";

    private string FormatBarText(UsageLimit? limit)
    {
        if (_displaySettings.ProgressBarMode == ProgressBarMode.TimeElapsed
            && limit?.WindowDuration is not null
            && limit.ResetsAt is not null)
        {
            var value = ComputeBarValue(limit);
            return $"{value:0.#}%";
        }

        return FormatPercentage(limit);
    }

    private string FormatTime(UsageLimit? limit)
    {
        if (limit?.ResetsAt is null)
        {
            return _displaySettings.DetailTimeMode == DetailTimeMode.TimeRemaining
                ? "Remaining unavailable"
                : "Reset unavailable";
        }

        if (_displaySettings.DetailTimeMode == DetailTimeMode.ResetTime)
        {
            return $"Resets {limit.ResetsAt.Value.ToLocalTime():g}";
        }

        var remaining = limit.ResetsAt.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "Ready to reset";
        }

        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m remaining"
            : $"{Math.Max(1, remaining.Minutes)}m remaining";
    }

    private static string FormatExtraUsage(ClaudeExtraUsageSnapshot? extraUsage)
    {
        if (extraUsage is null || !extraUsage.Enabled)
        {
            return "--";
        }

        if (extraUsage.Used is null || extraUsage.Limit is null)
        {
            return "Enabled";
        }

        return $"{extraUsage.Currency}{extraUsage.Used:0.##}/{extraUsage.Limit:0.##}";
    }

    private static string FormatCodexCredits(CodexCreditsSnapshot? credits)
    {
        if (credits is null || !credits.Enabled)
        {
            return "--";
        }

        if (credits.Unlimited)
        {
            return "Unlimited";
        }

        if (credits.OverageLimitReached || credits.SpendControlReached)
        {
            return "Limit reached";
        }

        return credits.Balance is null ? "Available" : $"{credits.Balance:0.##}";
    }

    private void ApplyIndicatorTheme(params TextBlock[] values)
    {
        var brush = _displaySettings.UseColoredTheme
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        foreach (var value in values)
        {
            value.Foreground = brush;
        }
    }

    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly nint TopMostWindow = new(-1);
    private static readonly nint NotTopMostWindow = new(-2);

    [Flags]
    private enum SetWindowPosFlags : uint
    {
        NoSize = 0x0001,
        NoMove = 0x0002,
        ShowWindow = 0x0040,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        SetWindowPosFlags flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint windowHandle, out Rect rect);
}
