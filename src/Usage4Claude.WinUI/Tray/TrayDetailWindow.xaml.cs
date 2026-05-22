using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Usage4Claude.WinUI.Tray;

public sealed partial class TrayDetailWindow : Window
{
    private readonly Action _openProbe;

    public TrayDetailWindow(string iconPath, Action openProbe)
    {
        InitializeComponent();
        _openProbe = openProbe;
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

    public void HideDetail()
    {
        AppWindow.Hide();
    }
}
