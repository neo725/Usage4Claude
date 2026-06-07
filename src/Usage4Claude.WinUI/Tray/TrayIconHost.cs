using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Usage4Claude.Core.Usage;
using WinRT.Interop;

namespace Usage4Claude.WinUI.Tray;

internal sealed class TrayIconHost : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint CallbackMessage = 0x8001;
    private const uint LeftButtonUp = 0x0202;
    private const uint LeftButtonDoubleClick = 0x0203;
    private const uint RightButtonUp = 0x0205;

    private readonly Window _window;
    private readonly string _iconPath;
    private readonly nint _windowHandle;
    private readonly WndProc _wndProc;
    private readonly nint _previousWndProc;
    private nint _iconHandle;
    private bool _ownsIconHandle;
    private readonly Action _toggleWindow;
    private readonly Action _showSettings;
    private readonly Action _quit;

    private bool _disposed;
    private int _quotaIconIndex;
    private string _tip = "Usage4Claude";

    public TrayIconHost(Window window, string iconPath, Action toggleWindow, Action showSettings, Action quit)
    {
        _window = window;
        _iconPath = iconPath;
        _toggleWindow = toggleWindow;
        _showSettings = showSettings;
        _quit = quit;
        _windowHandle = WindowNative.GetWindowHandle(window);
        _wndProc = WindowProcedure;
        _previousWndProc = SetWindowLongPtr(_windowHandle, WindowLongIndex.WndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
        _iconHandle = LoadImage(
            nint.Zero,
            iconPath,
            ImageType.Icon,
            32,
            32,
            LoadImageFlags.LoadFromFile);
        _ownsIconHandle = _iconHandle != nint.Zero;
        if (_iconHandle == nint.Zero)
        {
            _iconHandle = LoadIcon(nint.Zero, new nint(32512));
        }

        var icon = CreateIconData();
        if (!Shell_NotifyIcon(NotifyIconMessage.Add, ref icon))
        {
            throw new InvalidOperationException("Windows did not add the notification-area icon.");
        }

        icon.TimeoutOrVersion = 4;
        Shell_NotifyIcon(NotifyIconMessage.SetVersion, ref icon);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var icon = CreateIconData();
        Shell_NotifyIcon(NotifyIconMessage.Delete, ref icon);
        SetWindowLongPtr(_windowHandle, WindowLongIndex.WndProc, _previousWndProc);
        if (_ownsIconHandle)
        {
            DestroyIcon(_iconHandle);
        }
        _disposed = true;
    }

    private NotifyIconData CreateIconData() =>
        CreateIconData(NotifyIconFlags.Message | NotifyIconFlags.Icon | NotifyIconFlags.Tip | NotifyIconFlags.ShowTip);

    private NotifyIconData CreateIconData(NotifyIconFlags flags) =>
        new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = TrayIconId,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            IconHandle = _iconHandle,
            Tip = _tip,
        };

    private nint WindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4 packs the notification code into LOWORD(lParam).
            var notificationCode = LowWord(lParam);
            switch (notificationCode)
            {
                case LeftButtonUp:
                    _window.DispatcherQueue.TryEnqueue(() => _toggleWindow());
                    return nint.Zero;
                case LeftButtonDoubleClick:
                    _window.DispatcherQueue.TryEnqueue(() => _showSettings());
                    return nint.Zero;
                case RightButtonUp:
                    _window.DispatcherQueue.TryEnqueue(ShowContextMenu);
                    return nint.Zero;
            }
        }

        return CallWindowProc(_previousWndProc, windowHandle, message, wParam, lParam);
    }

    private static uint LowWord(nint value) => (uint)(value.ToInt64() & 0xFFFF);

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MenuFlags.String, 1001, "Show detail");
            AppendMenu(menu, MenuFlags.String, 1002, "Open Usage4Claude");
            AppendMenu(menu, MenuFlags.Separator, 0, string.Empty);
            AppendMenu(menu, MenuFlags.String, 1003, "Quit");
            SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenu(
                menu,
                TrackPopupMenuFlags.ReturnCommand | TrackPopupMenuFlags.RightButton,
                cursor.X,
                cursor.Y,
                0,
                _windowHandle,
                nint.Zero);

            switch (command)
            {
                case 1001:
                    _toggleWindow();
                    break;
                case 1002:
                    _showSettings();
                    break;
                case 1003:
                    _quit();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void CycleQuotaIcon()
    {
        var cycleStep = _quotaIconIndex++ % 4;
        var quotaIcon = cycleStep switch
        {
            0 => TrayQuotaIconRenderer.CreateIcon(27),
            1 => TrayQuotaIconRenderer.CreateIcon(73),
            2 => TrayQuotaIconRenderer.CreateIcon(96),
            _ => LoadIconFromFile(),
        };

        if (quotaIcon == nint.Zero)
        {
            return;
        }

        _tip = cycleStep switch
        {
            0 => "Usage4Claude 27% used",
            1 => "Usage4Claude 73% used",
            2 => "Usage4Claude 96% used",
            _ => "Usage4Claude",
        };
        ReplaceIcon(quotaIcon, ownsIconHandle: true);
    }

    public void UpdateUsage(UsageState state)
    {
        var primaryLimit = state.PrimaryLimit;
        if (primaryLimit is null)
        {
            return;
        }

        var quotaIcon = TrayQuotaIconRenderer.CreateIcon(primaryLimit.Percentage);
        if (quotaIcon == nint.Zero)
        {
            return;
        }

        _tip = $"Usage4Claude {primaryLimit.Percentage:0.#}% used";
        ReplaceIcon(quotaIcon, ownsIconHandle: true);
    }

    private nint LoadIconFromFile()
    {
        return LoadImage(
            nint.Zero,
            _iconPath,
            ImageType.Icon,
            32,
            32,
            LoadImageFlags.LoadFromFile);
    }

    private void ReplaceIcon(nint iconHandle, bool ownsIconHandle)
    {
        var oldIconHandle = _iconHandle;
        var oldIconOwned = _ownsIconHandle;
        _iconHandle = iconHandle;
        _ownsIconHandle = ownsIconHandle;

        if (!TryReplaceTrayIcon())
        {
            _iconHandle = oldIconHandle;
            _ownsIconHandle = oldIconOwned;
            if (ownsIconHandle)
            {
                DestroyIcon(iconHandle);
            }

            return;
        }

        if (oldIconOwned)
        {
            DestroyIcon(oldIconHandle);
        }
    }

    private bool TryReplaceTrayIcon()
    {
        var icon = CreateIconData();
        Shell_NotifyIcon(NotifyIconMessage.Delete, ref icon);
        if (!Shell_NotifyIcon(NotifyIconMessage.Add, ref icon))
        {
            return false;
        }

        icon.TimeoutOrVersion = 4;
        Shell_NotifyIcon(NotifyIconMessage.SetVersion, ref icon);
        return true;
    }

    private delegate nint WndProc(nint windowHandle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public NotifyIconFlags Flags;
        public uint CallbackMessage;
        public nint IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [Flags]
    private enum NotifyIconFlags : uint
    {
        Message = 0x1,
        Icon = 0x2,
        Tip = 0x4,
        ShowTip = 0x80,
    }

    private enum NotifyIconMessage : uint
    {
        Add = 0,
        Modify = 1,
        Delete = 2,
        SetVersion = 4,
    }

    private enum WindowLongIndex
    {
        WndProc = -4,
    }

    private enum ImageType : uint
    {
        Icon = 1,
    }

    [Flags]
    private enum LoadImageFlags : uint
    {
        LoadFromFile = 0x10,
    }

    [Flags]
    private enum MenuFlags : uint
    {
        String = 0x0,
        Separator = 0x800,
    }

    [Flags]
    private enum TrackPopupMenuFlags : uint
    {
        RightButton = 0x2,
        ReturnCommand = 0x100,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(NotifyIconMessage message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, WindowLongIndex index, nint newLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CallWindowProc(nint previousWndProc, nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        ImageType type,
        int desiredWidth,
        int desiredHeight,
        LoadImageFlags flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(nint menu, MenuFlags flags, nuint id, string text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(
        nint menu,
        TrackPopupMenuFlags flags,
        int x,
        int y,
        int reserved,
        nint windowHandle,
        nint rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
