#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
using Valour.Sdk.Models;
using Valour.Sdk.Services;

namespace Valour.Client.Maui.Notifications;

/// <summary>
/// Listens for in-app notifications via SignalR and shows native Windows toast notifications.
/// Works while the app is running (foreground or minimized to tray).
/// </summary>
public class WindowsToastService : IDisposable
{
    private readonly NotificationService _notificationService;
    private bool _enabled;

    public WindowsToastService(NotificationService notificationService)
    {
        _notificationService = notificationService;

        // Auto-enable if user previously opted in
        if (Preferences.Get("push_subscribed", false))
        {
            Enable();
        }
    }

    public void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        _notificationService.NotificationReceived += OnNotificationReceived;
    }

    public void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        _notificationService.NotificationReceived -= OnNotificationReceived;
    }

    private void OnNotificationReceived(Notification notification)
    {
        if (notification.TimeRead is not null)
            return;

        try
        {
            var builder = new ToastContentBuilder()
                .AddText(notification.Title ?? "Valour")
                .AddText(notification.Body ?? string.Empty);

            if (!string.IsNullOrEmpty(notification.ImageUrl))
            {
                builder.AddAppLogoOverride(new Uri(notification.ImageUrl));
            }

            builder.Show();
        }
        catch
        {
            // Best-effort — don't crash the app over a failed toast
        }
    }

    public void Dispose()
    {
        Disable();
    }
}
#endif
