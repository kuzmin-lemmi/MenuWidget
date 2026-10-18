using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MenuWidget.Core;
using MenuWidget.Services;
using Forms = System.Windows.Forms;

namespace MenuWidget.Prototype;

internal sealed class DesktopRuntime : IDisposable
{
    private readonly App app;
    private readonly LocalData data = new();
    private readonly WindowsIntegration windows = new();
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer placementTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer idleExit = new() { Interval = TimeSpan.FromMinutes(2) };
    private MainWindow? window;
    private WidgetWindow? widget;
    private Window? reminderWindow;
    private bool transient, restoring, disposed;
    private DateTimeOffset? reminderShownAt;
    private static readonly TimeSpan ReminderRepeat = TimeSpan.FromMinutes(30);
    public bool Exiting { get; private set; }

    public DesktopRuntime(App app, bool transient)
    {
        this.app = app; this.transient = transient;
        tray = new Forms.NotifyIcon { Icon = TrayIcon(), Text = "MenuWidget · меню для сайта", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Показать виджет", null, (_, _) => Dispatch(() => ShowWidget(force: true)));
        menu.Items.Add("Открыть программу", null, (_, _) => Dispatch(ShowMain));
        menu.Items.Add("Напомнить через 30 минут", null, (_, _) => Dispatch(async () => await SnoozeAsync()));
        menu.Items.Add("Выйти", null, (_, _) => Dispatch(() => RequestExit()));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatch(ShowMain);
        tray.BalloonTipClicked += (_, _) => Dispatch(OpenReminder);
        clock.Tick += (_, _) => { window?.RefreshToday(); widget?.Refresh(); CheckReminders(); }; clock.Start();
        placementTimer.Tick += (_, _) => { placementTimer.Stop(); SavePlacement(); };
        idleExit.Tick += (_, _) => { if (this.transient && window?.IsVisible != true && reminderWindow is null && !ReminderPending) RequestExit(); };
        if (transient) idleExit.Start();
    }

    public void ShowMain()
    {
        transient = false; idleExit.Stop();
        if (window is null)
        {
            window = new MainWindow(); app.MainWindow = window;
            RestorePlacement();
            window.Closing += WindowClosing;
            window.LocationChanged += (_, _) => QueuePlacement();
            window.SizeChanged += (_, _) => QueuePlacement();
            window.StateChanged += (_, _) => { if (window.WindowState == WindowState.Minimized) { SavePlacement(); window.Hide(); } };
            window.Loaded += (_, _) => KeepVisible();
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show(); KeepVisible(); window.Activate();
    }
    public void ShowWidget(bool force = false)
    {
        if (force) { transient = false; idleExit.Stop(); }
        else if (data.ReadWidget()?.Hidden == true) return;
        if (widget is null)
        {
            widget = new WidgetWindow(WidgetState, ShowMain, HideWidget, data, AcknowledgeReminder);
            widget.Closing += (_, e) => { if (!Exiting) { e.Cancel = true; HideWidget(); } };
        }
        widget.Refresh(); widget.Show();
        if (force) widget.SavePlacement(hidden: false);
    }
    public bool WidgetVisible => widget?.IsVisible == true;
    public void RefreshWidget() => widget?.Refresh();
    private void HideWidget()
    {
        if (widget is null) return;
        widget.SavePlacement(hidden: true); widget.Hide();
    }
    private WidgetSnapshot WidgetState()
    {
        UserSettings settings;
        try { settings = data.ReadSettings(); }
        catch (Exception ex) { Log(ex); settings = new(); }
        return new(DateOnly.FromDateTime(DateTime.Now), settings.FirstWeekMonday, settings.OutputDirectory, settings.Topmost,
            Theme: settings.WidgetTheme, Opacity: settings.WidgetOpacity, Corner: settings.WidgetCorner, Locked: settings.WidgetLocked, ReminderPending: ReminderPending);
    }
    public bool RequestExit(bool forUpdate = false)
    {
        if ((window?.IsWorking == true || widget?.IsWorking == true) && !forUpdate)
        {
            ShowMain(); MessageBox.Show(window, "Сначала завершите работу с файлами или отмените загрузку обновления.", "MenuWidget"); return false;
        }
        SavePlacement(); Exiting = true; app.Shutdown(); return true;
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (Exiting) return;
        e.Cancel = true; SavePlacement(); window!.Hide();
    }
    private void RestorePlacement()
    {
        try
        {
            if (data.ReadPlacement() is not { } saved || !new[] { saved.Left, saved.Top, saved.Width, saved.Height }.All(double.IsFinite)) return;
            restoring = true; window!.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Width = Math.Clamp(saved.Width, 520, 1600); window.Height = Math.Clamp(saved.Height, 500, 1600);
            window.Left = saved.Left; window.Top = saved.Top;
        }
        catch (Exception ex) { Log(ex); }
        finally { restoring = false; }
    }
    private void KeepVisible()
    {
        if (window is null || window.WindowState != WindowState.Normal) return;
        restoring = true;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var screen = Forms.Screen.FromHandle(handle);
            var dpi = VisualTreeHelper.GetDpi(window);
            var area = screen.WorkingArea;
            var header = new System.Drawing.Rectangle((int)(window.Left * dpi.DpiScaleX), (int)(window.Top * dpi.DpiScaleY), (int)(window.Width * dpi.DpiScaleX), 40);
            if (!Forms.Screen.AllScreens.Any(s => { var visible = System.Drawing.Rectangle.Intersect(s.WorkingArea, header); return visible.Width >= 80 && visible.Height >= 20; }))
            {
                window.Left = SystemParameters.WorkArea.Left + 24; window.Top = SystemParameters.WorkArea.Top + 24;
                area = Forms.Screen.PrimaryScreen?.WorkingArea ?? area;
            }
            var maxWidth = Math.Max(300, area.Width / dpi.DpiScaleX); var maxHeight = Math.Max(350, area.Height / dpi.DpiScaleY);
            window.MinWidth = Math.Min(520, maxWidth); window.MinHeight = Math.Min(700, maxHeight);
            window.Width = Math.Clamp(window.Width, window.MinWidth, maxWidth); window.Height = Math.Clamp(window.Height, window.MinHeight, maxHeight);
            window.Top = Math.Clamp(window.Top, area.Top / dpi.DpiScaleY, (area.Bottom / dpi.DpiScaleY) - window.Height);
            window.Left = Math.Clamp(window.Left, area.Left / dpi.DpiScaleX, (area.Right / dpi.DpiScaleX) - window.Width);
        }
        finally { restoring = false; }
    }
    private void QueuePlacement() { if (restoring || Exiting) return; placementTimer.Stop(); placementTimer.Start(); }
    private void SavePlacement()
    {
        if (window is null || restoring || window.WindowState != WindowState.Normal) return;
        try { data.SavePlacement(new(window.Left, window.Top, window.Width, window.Height)); }
        catch (Exception ex) { Log(ex); }
    }

    public async Task ApplySettingsAsync(UserSettings previous, UserSettings updated)
    {
        var oldState = data.ReadReminderState();
        try
        {
            await windows.ApplyAsync(AsReminder(updated), default); windows.SetAutoStart(updated.AutoStart);
            if (!previous.ReminderEnabled && updated.ReminderEnabled) data.SaveReminderState(ReminderRules.Start(DateTimeOffset.Now, AsReminder(updated)));
            else if (!updated.ReminderEnabled) data.SaveReminderState(oldState with { SnoozedUntil = null });
            data.SaveSettings(updated);
        }
        catch
        {
            try { await windows.ApplyAsync(AsReminder(previous), default); windows.SetAutoStart(previous.AutoStart); data.SaveReminderState(oldState); data.SaveSettings(previous); }
            catch (Exception rollbackError) { Log(rollbackError); }
            throw;
        }
        CheckReminders();
    }
    public async Task RestoreIntegrationsAsync()
    {
        try { GitHubUpdateService.CleanupDownloads(data, DateTime.UtcNow.AddDays(-1)); }
        catch (Exception ex) { Log(ex); }
        var settings = data.ReadSettings();
        await windows.ApplyAsync(AsReminder(settings), default);
        windows.SetAutoStart(settings.AutoStart);
        if (settings.ReminderEnabled && data.ReadReminderState().LastSunday is null)
            data.SaveReminderState(ReminderRules.Start(DateTimeOffset.Now, AsReminder(settings)));
    }

    public void CheckReminders()
    {
        try
        {
            var settings = data.ReadSettings(); var reminder = AsReminder(settings); var state = data.ReadReminderState();
            var now = DateTimeOffset.Now;
            if (!ReminderRules.IsDue(now, reminder, state)) { reminderShownAt = null; return; }
            // The reminder counts as seen only after the user reacts; until then it repeats every 30 minutes
            // and keeps a scheduler-started copy of the program alive in the tray.
            if (reminderShownAt is { } shown && now - shown < ReminderRepeat) return;
            reminderShownAt = now;
            ShowNotification(); widget?.Refresh();
        }
        catch (Exception ex) { Log(ex); }
    }
    public bool ReminderPending => reminderShownAt is not null;
    public void AcknowledgeReminder()
    {
        try
        {
            var settings = data.ReadSettings(); var reminder = AsReminder(settings); var state = data.ReadReminderState(); var now = DateTimeOffset.Now;
            if (ReminderRules.IsDue(now, reminder, state)) data.SaveReminderState(ReminderRules.Acknowledge(now, reminder, state));
        }
        catch (Exception ex) { Log(ex); }
        reminderShownAt = null; widget?.Refresh();
    }
    public void TestNotification() => ShowNotification(test: true);
    private void ShowNotification(bool test = false)
    {
        var monday = new CycleService().SuggestedMonday(DateOnly.FromDateTime(DateTime.Now));
        // A reminder on Sunday prepares the next work week; a missed reminder prepares the current one.
        tray.BalloonTipTitle = test ? "MenuWidget · проверка уведомления" : "Пора подготовить меню питания";
        tray.BalloonTipText = $"Меню на {monday:dd.MM}–{monday.AddDays(4):dd.MM}. Нажмите, чтобы открыть или напомнить позже.";
        tray.ShowBalloonTip(15000);
    }
    private void OpenReminder()
    {
        if (ReminderPending) AcknowledgeReminder();
        if (reminderWindow is not null) { reminderWindow.Activate(); return; }
        var body = new System.Windows.Controls.StackPanel { Margin = new Thickness(22) };
        body.Children.Add(new System.Windows.Controls.TextBlock { Text = "Пора подготовить меню питания", FontSize = 21, TextWrapping = TextWrapping.Wrap });
        var open = new System.Windows.Controls.Button { Content = "Открыть меню", Margin = new Thickness(0, 20, 0, 10) };
        var snooze = new System.Windows.Controls.Button { Content = "Напомнить через 30 минут" };
        body.Children.Add(open); body.Children.Add(snooze);
        reminderWindow = new Window { Title = "Напоминание MenuWidget", Width = 430, Height = 260, Content = body, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        open.Click += (_, _) => { reminderWindow?.Close(); ShowMain(); };
        snooze.Click += async (_, _) => { if (await SnoozeAsync()) reminderWindow?.Close(); };
        reminderWindow.Closed += (_, _) => reminderWindow = null;
        reminderWindow.Show();
    }
    private async Task<bool> SnoozeAsync()
    {
        try
        {
            var settings = data.ReadSettings();
            if (!settings.ReminderEnabled) { MessageBox.Show("Сначала включите напоминания в настройках.", "MenuWidget"); return false; }
            var until = DateTimeOffset.UtcNow.AddMinutes(30);
            await windows.SnoozeAsync(until, default);
            data.SaveReminderState(data.ReadReminderState() with { SnoozedUntil = until });
            reminderShownAt = null; widget?.Refresh(); return true;
        }
        catch (Exception ex) { MessageBox.Show("Не удалось отложить напоминание: " + ex.Message, "MenuWidget"); return false; }
    }
    // Own green "М" icon instead of the generic Windows "i", which is easy to mistake for other apps.
    private static System.Drawing.Icon TrayIcon()
    {
        using var stream = typeof(DesktopRuntime).Assembly.GetManifestResourceStream("MenuWidget.app.ico");
        return stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
    private static ReminderSettings AsReminder(UserSettings settings) => new(settings.ReminderEnabled, settings.ReminderTime, TimeSpan.FromMinutes(30));
    private void Dispatch(Action action) { if (!disposed && !Exiting) app.Dispatcher.BeginInvoke(action); }
    private void Log(Exception ex)
    {
        try { Directory.CreateDirectory(data.Root); File.AppendAllText(Path.Combine(data.Root, "diagnostics.log"), $"{DateTimeOffset.Now:O} {ex.GetType().Name}: {ex.Message}\n"); }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        disposed = true; clock.Stop(); placementTimer.Stop(); idleExit.Stop(); tray.Dispose();
    }
}
