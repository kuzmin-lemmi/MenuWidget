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
        try
        {
            var demo = e.Args.Any(arg => arg is "--capture" or "--layout-checks" or "--drag-probe");
            if (demo || e.Args.Contains("--health-check")) { await RunDemoAsync(e.Args, demo); return; }
            if (e.Args.Contains("--uninstall-integrations"))
            {
                await SingleInstance.SendAsync("exit"); new WindowsIntegration().RemoveAll(); Shutdown(); return;
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
            if (!silent) Runtime.ShowMain();
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
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, scene + ".png")); encoder.Save(stream);
            }
            Shutdown();
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Runtime?.Dispose(); instance?.Dispose(); base.OnExit(e);
    }
}
