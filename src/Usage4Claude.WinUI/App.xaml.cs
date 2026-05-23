using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;

namespace Usage4Claude.WinUI;

public partial class App : Application
{
    internal MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppNotificationManager.Default.NotificationInvoked += AppNotificationManager_NotificationInvoked;
        AppNotificationManager.Default.Register();
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private void AppNotificationManager_NotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        MainWindow?.DispatcherQueue.TryEnqueue(() => MainWindow.Activate());
    }
}
