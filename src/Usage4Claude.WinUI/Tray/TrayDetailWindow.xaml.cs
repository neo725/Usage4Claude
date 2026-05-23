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
    private const int DetailWindowHeight = 318;
    private const int EdgeMargin = 12;

    private readonly Action _openProbe;
    private readonly Func<Task> _refreshUsage;
    private readonly nint _windowHandle;
    private bool _isTopMost;

    public TrayDetailWindow(string iconPath, Action openProbe, Func<Task> refreshUsage)
    {
        InitializeComponent();
        _openProbe = openProbe;
        _refreshUsage = refreshUsage;
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

    private void TopMostToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _isTopMost = TopMostToggle.IsChecked == true;
        SetWindowTopMost(_isTopMost, showWindow: true);
    }

    private void DragSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(DragSurface).Properties.IsLeftButtonPressed &&
            !IsInteractiveControl(e.OriginalSource as DependencyObject))
        {
            ReleaseCapture();
            SendMessage(_windowHandle, WindowMessage.SysCommand, new nint(SystemCommandMove), nint.Zero);
            e.Handled = true;
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
        UpdatedAtText.Text = usageState.UpdatedAt is null
            ? "Sign in and refresh usage"
            : $"Updated {usageState.UpdatedAt.Value.ToLocalTime():t}";

        UpdateClaude(usageState.Claude, sessionState.Claude);
        UpdateCodex(usageState.Codex, sessionState.HasCodex);
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

        ClaudePrimaryText.Text = FormatPercentage(usage.FiveHour);
        ClaudePrimaryResetText.Text = FormatReset(usage.FiveHour);
        ClaudeSecondaryText.Text = FormatPercentage(usage.SevenDay);
        ClaudeSecondaryResetText.Text = FormatReset(usage.SevenDay);
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

        CodexPrimaryText.Text = FormatPercentage(usage.Primary);
        CodexPrimaryResetText.Text = FormatReset(usage.Primary);
        CodexSecondaryText.Text = FormatPercentage(usage.Secondary);
        CodexSecondaryResetText.Text = FormatReset(usage.Secondary);
    }

    private static string FormatPercentage(UsageLimit? limit) =>
        limit is null ? "--" : $"{limit.Percentage:0.#}%";

    private static string FormatReset(UsageLimit? limit) =>
        limit?.ResetsAt is null ? "Reset unavailable" : $"Resets {limit.ResetsAt.Value.ToLocalTime():g}";

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int SystemCommandMove = 0xF012;
    private static readonly nint TopMostWindow = new(-1);
    private static readonly nint NotTopMostWindow = new(-2);

    private enum WindowMessage : uint
    {
        SysCommand = 0x0112,
    }

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
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessage(nint windowHandle, WindowMessage message, nint wParam, nint lParam);
}
