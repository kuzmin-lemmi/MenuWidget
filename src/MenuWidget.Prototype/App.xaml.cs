using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MenuWidget.Prototype;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            if (e.Args.Length == 2 && e.Args[0] == "--capture")
            {
                var output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
                foreach (var scene in new[] { "welcome", "empty", "partial", "ready", "error", "settings", "updates" })
                {
                    window.SetScene(scene);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, scene + ".png")); encoder.Save(stream);
                }
                Shutdown();
            }
        }
        catch (Exception ex)
        {
            if (e.Args.Length > 0) { Console.Error.WriteLine(ex); Shutdown(1); }
            else MessageBox.Show(ex.Message, "Не удалось открыть прототип");
        }
    }
}
