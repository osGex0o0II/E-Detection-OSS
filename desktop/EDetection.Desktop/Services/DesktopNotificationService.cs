using EDetection.Desktop.Models;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace EDetection.Desktop.Services;

public sealed class DesktopNotificationService
{
    private const string ActionKey = "action";
    private const string ReportPathKey = "reportPath";
    private const string ActionUrlKey = "actionUrl";

    private bool _registered;

    public event EventHandler<DesktopNotificationActivation>? Activated;

    public bool Register()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return false;
            }

            AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
            AppNotificationManager.Default.NotificationInvoked += NotificationInvoked;

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "app.ico");
            var iconUri = File.Exists(iconPath) ? new Uri(iconPath) : null;
            if (iconUri is null)
            {
                AppNotificationManager.Default.Register();
            }
            else
            {
                AppNotificationManager.Default.Register("E-Detection Desktop", iconUri);
            }

            _registered = true;
            return true;
        }
        catch
        {
            AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
            _registered = false;
            return false;
        }
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch
        {
        }
        finally
        {
            _registered = false;
        }
    }

    public bool Show(DesktopNotificationRequest request)
    {
        if (!_registered && !Register())
        {
            return false;
        }

        if (!_registered)
        {
            return false;
        }

        try
        {
            if (AppNotificationManager.Default.Setting is not AppNotificationSetting.Enabled)
            {
                return false;
            }

            var defaultAction = string.IsNullOrWhiteSpace(request.ActionUrl)
                ? DesktopNotificationActivation.OpenWorkbenchAction
                : DesktopNotificationActivation.OpenUpdateAction;
            var builder = new AppNotificationBuilder()
                .AddArgument(ActionKey, defaultAction)
                .AddText(request.Title)
                .AddText(request.Message)
                .SetAttributionText("E-Detection");

            if (!string.IsNullOrWhiteSpace(request.ActionUrl))
            {
                builder.AddArgument(ActionUrlKey, request.ActionUrl)
                    .AddButton(new AppNotificationButton("打开更新页面")
                        .AddArgument(ActionKey, DesktopNotificationActivation.OpenUpdateAction)
                        .AddArgument(ActionUrlKey, request.ActionUrl));
            }
            else
            {
                builder.AddButton(new AppNotificationButton("打开工作台")
                    .AddArgument(ActionKey, DesktopNotificationActivation.OpenWorkbenchAction));
            }

            if (!string.IsNullOrWhiteSpace(request.ReportPath))
            {
                builder.AddButton(new AppNotificationButton("打开报告")
                    .AddArgument(ActionKey, DesktopNotificationActivation.OpenReportAction)
                    .AddArgument(ReportPathKey, request.ReportPath));
            }

            if (request.Kind is DesktopNotificationKind.Error)
            {
                builder.SetScenario(AppNotificationScenario.Urgent);
            }

            var notification = builder.BuildNotification();
            notification.Tag = request.Kind is DesktopNotificationKind.Update ? "update" : "detection";
            notification.Group = request.Kind is DesktopNotificationKind.Update ? "updates" : "runs";
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            var action = args.Arguments.TryGetValue(ActionKey, out var actionValue)
                ? actionValue
                : DesktopNotificationActivation.OpenWorkbenchAction;
            var reportPath = args.Arguments.TryGetValue(ReportPathKey, out var reportPathValue)
                ? reportPathValue
                : null;
            var actionUrl = args.Arguments.TryGetValue(ActionUrlKey, out var actionUrlValue)
                ? actionUrlValue
                : null;

            Activated?.Invoke(
                this,
                new DesktopNotificationActivation(action, reportPath, actionUrl));
        }
        catch
        {
        }
    }
}
