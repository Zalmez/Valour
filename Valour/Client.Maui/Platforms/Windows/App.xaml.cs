using Microsoft.UI.Xaml;
using WinUiControls = Microsoft.UI.Xaml.Controls;
using H.NotifyIcon;

namespace Valour.Client.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    private TaskbarIcon? _trayIcon;
    private Microsoft.UI.Xaml.Window? _mauiWindow;
    private Microsoft.UI.Windowing.AppWindow? _appWindow;

    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        var window = Microsoft.Maui.MauiWinUIApplication.Current.Application.Windows[0];
        var mauiWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (mauiWindow is null) return;
        _mauiWindow = mauiWindow;

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(mauiWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        // Intercept the close button to minimize to tray instead
        if (_appWindow is not null)
        {
            _appWindow.Closing += (_, e) =>
            {
                e.Cancel = true;
                mauiWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    _mauiWindow?.Hide();
                });
            };
        }

        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Valour",
            DoubleClickCommand = new RelayCommand(ShowWindow),
            Visibility =  Microsoft.UI.Xaml.Visibility.Visible,
        };

        // Try to load the app icon
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Platforms", "Windows", "trayicon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                _trayIcon.Icon = new System.Drawing.Icon(iconPath);
            }
        }
        catch
        {
            // Fall back to default if icon file not found
        }

        // If the custom icon is unavailable, fall back to the executable icon.
        if (_trayIcon.Icon is null)
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                }
            }
            catch
            {
                // Best-effort fallback icon
            }
        }

        // Right-click context menu
        var menu = new WinUiControls.MenuFlyout();
        var showItem = new WinUiControls.MenuFlyoutItem { Text = "Show Valour" };
        showItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(showItem);

        menu.Items.Add(new WinUiControls.MenuFlyoutSeparator());

        var exitItem = new WinUiControls.MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        _trayIcon.ContextFlyout = menu;
        _trayIcon.ForceCreate();
    }

    private void ShowWindow()
    {
        _mauiWindow?.Show();
        _appWindow?.Show();
        if (_appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = true;
            presenter.Restore();
        }
    }

    private void ExitApplication()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        try
        {
            Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.Uninstall();
        }
        catch
        {
            // Best-effort cleanup
        }

        _appWindow?.Destroy();
    }

    private class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
