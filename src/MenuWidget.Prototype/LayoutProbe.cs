using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MenuWidget.Prototype;

internal static class LayoutProbe
{
    public static async Task RunAsync(MainWindow window, string output)
    {
        Directory.CreateDirectory(output);
        window.Activate();
        var checks = new List<object>();
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            window.Top = 0; window.Left = 0;
            window.MinHeight = 450;
            window.Width = Math.Min(570 * scale, SystemParameters.WorkArea.Width);
            window.Height = Math.Min(900 * scale, SystemParameters.WorkArea.Height);
            var root = (FrameworkElement)window.Content;
            root.LayoutTransform = new ScaleTransform(scale, scale);
            foreach (var scene in new[] { "welcome", "ready", "error", "settings", "updates" })
            {
                window.SetScene(scene);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var buttons = Descendants<Button>(root).Where(b => b.IsVisible && b.IsEnabled && b.Focusable && KeyboardNavigation.GetIsTabStop(b) && b.ActualHeight > 0).ToArray();
                if (buttons.Length == 0) throw new InvalidOperationException("No accessible actions in " + scene);
                foreach (var button in buttons)
                {
                    button.BringIntoView(); window.UpdateLayout();
                    if (!button.Focus() || Keyboard.FocusedElement != button) throw new InvalidOperationException("Cannot focus action " + button.Content);
                    var position = button.TransformToAncestor(root).Transform(new Point());
                    if (position.Y < -1 || position.Y + button.ActualHeight > root.ActualHeight + 1) throw new InvalidOperationException("Action not reachable after scroll: " + button.Content);
                    if (!button.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))) throw new InvalidOperationException("Keyboard navigation stopped.");
                }
                foreach (var scroll in Descendants<ScrollViewer>(root)) scroll.ScrollToTop();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{scene}-{scale * 100:0}.png")); png.Save(file);
                checks.Add(new { scene, scale, buttons = buttons.Length, passed = true });
            }
        }
        File.WriteAllText(Path.Combine(output, "layout-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
