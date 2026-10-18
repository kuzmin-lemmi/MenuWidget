using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MenuWidget.Core;
using MenuWidget.Services;

namespace MenuWidget.Prototype;

public partial class App : Application
{
    private SingleInstance? instance;
    internal DesktopRuntime? Runtime { get; private set; }
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            var demo = e.Args.Any(arg => arg is "--capture" or "--layout-checks" or "--drag-probe");
            if (demo || e.Args.Contains("--health-check")) { await RunDemoAsync(e.Args, demo); return; }
            if (e.Args.Contains("--uninstall-integrations"))
            {
                await SingleInstance.SendAsync("exit"); new WindowsIntegration(e.Args.ElementAtOrDefault(1)).RemoveAll(); Shutdown(); return;
            }
            // Explicit developer acceptance command; does not change the user's settings.
            if (e.Args.Length == 2 && e.Args[0] == "--diagnostics-integrations")
            {
                var integration = new WindowsIntegration();
                integration.RemoveAll(); integration.RemoveAll(); // Missing integrations must be harmless.
                await integration.ApplyAsync(new(true, new TimeOnly(18, 0), TimeSpan.FromMinutes(30)), default);
                await integration.SnoozeAsync(DateTimeOffset.Now.AddMinutes(30), default);
                integration.SetAutoStart(true);
                File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(new { success = true, executable = Environment.ProcessPath }));
                Shutdown(); return;
            }
            instance = new SingleInstance();
            if (!instance.IsPrimary)
            {
                var command = e.Args.Contains("--check-reminder") || e.Args.Contains("--background") ? "check" : "open";
                if (!await SingleInstance.SendAsync(command)) throw new IOException("Программа ещё запускается. Повторите открытие через несколько секунд.");
                Shutdown(); return;
            }
            var silent = e.Args.Contains("--check-reminder") || e.Args.Contains("--background");
            var data = new LocalData(); var settings = data.ReadSettings();
            if (data.RecoveredSettingsBackup is { } backup)
                MessageBox.Show("Файл настроек был повреждён, поэтому настройки сброшены. Шаблоны меню и готовые файлы не затронуты.\nОткройте «Настройки» и укажите начало цикла заново.\nКопия старого файла: " + backup, "MenuWidget");
            if (e.Args.Contains("--background") && !settings.AutoStart) { Shutdown(); return; }
            if (e.Args.Contains("--check-reminder") && !ReminderRules.IsDue(DateTimeOffset.Now, new(settings.ReminderEnabled, settings.ReminderTime, TimeSpan.FromMinutes(30)), data.ReadReminderState())) { Shutdown(); return; }
            Runtime = new DesktopRuntime(this, e.Args.Contains("--check-reminder"));
            instance.Listen("open", () => Dispatcher.BeginInvoke(() => Runtime.ShowMain()));
            instance.Listen("check", () => Dispatcher.BeginInvoke(() => Runtime.CheckReminders()));
            instance.Listen("exit", () => Dispatcher.BeginInvoke(() => Runtime.RequestExit()));
            try { await Runtime.RestoreIntegrationsAsync(); }
            catch (Exception integrationError)
            {
                if (!silent) MessageBox.Show("Не удалось восстановить автозапуск или расписание: " + integrationError.Message + "\nПроверьте настройки программы.", "MenuWidget");
            }
            // The widget is the everyday face of the program; the full window opens on first run or when the widget is hidden.
            if (!e.Args.Contains("--check-reminder")) Runtime.ShowWidget();
            if (!silent && (settings.FirstWeekMonday is null || !Runtime.WidgetVisible)) Runtime.ShowMain();
            Runtime.CheckReminders();
        }
        catch (Exception ex)
        {
            if (e.Args.Length > 0) Console.Error.WriteLine(ex);
            else MessageBox.Show(ex.Message, "Не удалось открыть MenuWidget");
            Shutdown(1);
        }
    }

    private async Task RunDemoAsync(string[] args, bool demo)
    {
        var window = new MainWindow(demo); MainWindow = window;
        if (args.Contains("--health-check")) { Shutdown(); return; }
        window.Show();
        if (args.Length == 2 && args[0] == "--drag-probe") window.EnableDragProbe(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--layout-checks") { await LayoutProbe.RunAsync(window, Path.GetFullPath(args[1])); Shutdown(); return; }
        if (args.Length == 2 && args[0] == "--capture")
        {
            var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            foreach (var scene in new[] { "welcome", "empty", "partial", "ready", "error", "settings", "updates" })
            {
                window.SetScene(scene); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Capture((FrameworkElement)window.Content, window.ActualWidth, window.ActualHeight, Path.Combine(output, scene + ".png"));
            }
            // Synthetic widget states: a weekday with prepared files and a weekend before preparation.
            foreach (var (scene, today, prepared) in new[] { ("widget", new DateOnly(2026, 10, 22), true), ("widget-weekend", new DateOnly(2026, 10, 24), false) })
            {
                var widget = new WidgetWindow(() => new(today, new DateOnly(2026, 10, 12), "Папка готовых меню", false, prepared), () => { }, () => { }, null);
                widget.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); widget.UpdateLayout();
                Capture((FrameworkElement)widget.Content, widget.ActualWidth, widget.ActualHeight, Path.Combine(output, scene + ".png"));
                widget.Close();
            }
            Shutdown();
        }
    }
    private static void Capture(FrameworkElement content, double width, double height, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    // Last line of defence: a UI handler error must not silently close the widget with the user's work.
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try { var root = new LocalData().Root; Directory.CreateDirectory(root); File.AppendAllText(Path.Combine(root, "diagnostics.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n"); }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
        MessageBox.Show("Не удалось выполнить действие: " + e.Exception.Message + "\nПрограмма продолжит работу; сохранённые шаблоны не изменены.", "MenuWidget");
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Runtime?.Dispose(); instance?.Dispose(); base.OnExit(e);
    }
}
