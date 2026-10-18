using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MenuWidget.WindowsProbe;

internal static class Program
{
    internal static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MenuWidget.Stage1Probe");
    [STAThread]
    public static int Main(string[] args)
    {
        Directory.CreateDirectory(DataDirectory);
        try { var app = new ProbeApplication(args); return app.Run(); }
        catch (Exception ex) { Record("error", new { error = ex.ToString() }); return 1; }
    }
    internal static void Record(string kind, object data) => File.AppendAllText(Path.Combine(DataDirectory, "events.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.Now, pid = Environment.ProcessId, kind, version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(), data }) + Environment.NewLine);
}

internal sealed class ProbeApplication(string[] args) : Application
{
    private AppNotificationManager? manager;
    private System.Windows.Forms.NotifyIcon? tray;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            Program.Record("registered", new { path = Environment.ProcessPath, setting = manager.Setting.ToString(), uiOpen = false });
            if (args.Contains("--self-check")) { Program.Record("self-check", new { supported = manager.Setting != AppNotificationSetting.Unsupported }); Shutdown(); return; }
            if (args.Contains("--cleanup"))
            {
                await manager.RemoveAllAsync();
                manager.UnregisterAll();
                Program.Record("cleaned", new { }); Shutdown(); return;
            }
            if (args.Contains("--history"))
            {
                if (manager.Setting == AppNotificationSetting.Unsupported) { Program.Record("history-unavailable", new { reason = "Unsupported" }); Shutdown(); return; }
                var items = await manager.GetAllAsync();
                Program.Record("history", new { count = items.Count, items = items.Select(n => new { n.Id, n.Tag, n.Group }).ToArray() });
                Shutdown(); return;
            }
            if (args.Contains("--notify"))
            {
                if (manager.Setting == AppNotificationSetting.Unsupported)
                {
                    ShowTrayFallback();
                    return;
                }
                if (manager.Setting != AppNotificationSetting.Enabled) throw new InvalidOperationException("Windows notifications are disabled: " + manager.Setting);
                // The probe's unique tag replaces its earlier toast rather than stacking duplicates.
                var notification = new AppNotificationBuilder()
                    .AddArgument("action", "open")
                    .AddText("MenuWidget · проверка этапа 1")
                    .AddText("Пора подготовить меню. Нажмите, чтобы открыть тестовое приложение.")
                    .AddButton(new AppNotificationButton("Открыть").AddArgument("action", "open"))
                    .BuildNotification();
                notification.Tag = "stage1"; notification.Group = "technical-probe";
                manager.Show(notification);
                Program.Record("shown", new { notification.Id, notification.Tag, setting = manager.Setting.ToString(), uiOpen = false });
                Shutdown(); return;
            }
            // COM activation may arrive asynchronously immediately after Register().
            if (args.Any(a => a.Contains("AppNotificationActivated", StringComparison.OrdinalIgnoreCase)))
            {
                Program.Record("activation-startup", new { args });
                return;
            }
            ShowProbeWindow();
        }
        catch (Exception ex) { Program.Record("error", new { error = ex.ToString() }); Shutdown(1); }
    }

    private void ShowTrayFallback()
    {
        tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "MenuWidget · стенд этапа 1",
            BalloonTipTitle = "MenuWidget · проверка этапа 1",
            BalloonTipText = "Пора подготовить меню. Нажмите, чтобы открыть тестовое приложение.",
            Visible = true
        };
        tray.BalloonTipClicked += (_, _) => { Program.Record("activated", new { transport = "tray-balloon" }); Dispatcher.BeginInvoke(() => ShowProbeWindow()); };
        tray.DoubleClick += (_, _) => { Program.Record("tray-opened", new { }); Dispatcher.BeginInvoke(() => ShowProbeWindow()); };
        tray.BalloonTipShown += (_, _) => Program.Record("balloon-visible", new { uiOpen = MainWindow is not null });
        tray.ShowBalloonTip(30000);
        Program.Record("fallback-requested", new { transport = "tray-balloon", uiOpen = false });
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timeout.Tick += (_, _) => { timeout.Stop(); if (MainWindow is null) Shutdown(); };
        timeout.Start();
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs e)
    {
        Program.Record("activated", new { e.Argument });
        Dispatcher.BeginInvoke(() => ShowProbeWindow());
    }

    private void ShowProbeWindow()
    {
        if (MainWindow is not null) { MainWindow.Activate(); return; }
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Проверка установленного MenuWidget", FontSize = 22, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Изолированный стенд этапа 1. Настоящие меню и настройки не используются.", Margin = new Thickness(0, 14, 0, 14), TextWrapping = TextWrapping.Wrap });
        var close = new Button { Content = "Закрыть тестовое приложение", Padding = new Thickness(12), Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => Shutdown(); panel.Children.Add(close);
        MainWindow = new Window { Title = "MenuWidget · установленный стенд этапа 1", Width = 480, Height = 280, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        MainWindow.Closed += (_, _) => Shutdown();
        MainWindow.Show(); MainWindow.Activate();
        Program.Record("window-opened", new { MainWindow.Title });
    }
    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        manager?.Unregister();
        Program.Record("exited", new { e.ApplicationExitCode });
        base.OnExit(e);
    }
}
