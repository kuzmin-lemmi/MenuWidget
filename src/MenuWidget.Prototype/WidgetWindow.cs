using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MenuWidget.Core;
using MenuWidget.Services;
using Microsoft.Win32;

namespace MenuWidget.Prototype;

// Snapshot the widget draws from; demo captures pass a fixed one instead of the user's profile.
internal record WidgetSnapshot(DateOnly Today, DateOnly? FirstWeekMonday, string OutputDirectory, bool Topmost, bool DemoReady = false,
    string Theme = WidgetLook.Auto, int Opacity = 100, string Corner = WidgetLook.TopRight, bool Locked = false, bool ReminderPending = false);

internal static class WidgetLook
{
    public const string Auto = "system", Light = "light", Dark = "dark";
    public const string TopRight = "top-right", BottomRight = "bottom-right", TopLeft = "top-left", BottomLeft = "bottom-left", Custom = "custom";
}

// Compact desktop card: which week of the cycle it is, today's menu and a click to open or prepare the week's files.
internal sealed class WidgetWindow : Window
{
    private sealed record Palette(Color Card, Brush Ink, Brush Muted, Brush Faint, Brush Accent, Brush Soft, Brush Line, Brush AccentLine);
    private static readonly Palette LightPalette = new(Color.FromRgb(0xFF, 0xFF, 0xFF), Brush("#26382F"), Brush("#65726A"), Brush("#A3ADA6"), Brush("#28634D"), Brush("#E8EEE6"), Brush("#E0E5DE"), Brush("#7FB39C"));
    private static readonly Palette DarkPalette = new(Color.FromRgb(0x1D, 0x1F, 0x24), Brush("#E8ECE9"), Brush("#9AA3A0"), Brush("#5E6663"), Brush("#8BCAAF"), Brush("#2A3A33"), Brush("#2C2F35"), Brush("#5E8F79"));
    private readonly CycleService cycle = new();
    private readonly XlsxDateEditor editor = new();
    private readonly CultureInfo ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly Func<WidgetSnapshot> snapshot;
    private readonly Action openMain;
    private readonly Action? hide;
    private readonly Action? prepared;
    private readonly LocalData? data;
    private readonly Border card = new() { CornerRadius = new(10), BorderThickness = new(1), Margin = new(6), Cursor = Cursors.Hand };
    private Palette palette = LightPalette;
    private WidgetSnapshot? last;
    private WeekInfo? week;
    private ExportResult? ready;
    private bool pressed, moving, busy;
    private Point pressPoint;
    public bool IsWorking => busy;

    public WidgetWindow(Func<WidgetSnapshot> snapshot, Action openMain, Action? hide, LocalData? data, Action? prepared = null)
    {
        this.snapshot = snapshot; this.openMain = openMain; this.hide = hide; this.data = data; this.prepared = prepared;
        Title = "Меню для сайта · виджет"; Width = 178; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; FontFamily = new FontFamily("Segoe UI"); FontSize = 13; Language = System.Windows.Markup.XmlLanguage.GetLanguage("ru-RU");
        card.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.16, Color = Colors.Black };
        Content = card;
        card.MouseLeftButtonDown += (_, e) => { pressed = true; pressPoint = e.GetPosition(this); };
        card.MouseMove += (_, e) =>
        {
            if (!pressed || last?.Locked == true || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - pressPoint).Length < 5) return;
            pressed = false; moving = true;
            try { DragMove(); } finally { moving = false; SavePlacement(); MarkCustomCorner(); }
        };
        card.MouseLeftButtonUp += async (_, _) => { if (pressed && !moving) { pressed = false; await ActivateAsync(); } };
        card.MouseLeave += (_, _) => pressed = false;
        SizeChanged += (_, _) => PlaceInCorner();
        RestorePlacement();
        Refresh();
    }

    public void Refresh()
    {
        var state = last = snapshot();
        Topmost = state.Topmost;
        palette = IsDark(state.Theme) ? DarkPalette : LightPalette;
        var alpha = (byte)Math.Round(Math.Clamp(state.Opacity, 80, 100) * 2.55);
        card.Background = new SolidColorBrush(Color.FromArgb(alpha, palette.Card.R, palette.Card.G, palette.Card.B));
        card.BorderBrush = palette.Line;
        var body = new StackPanel();
        week = null; ready = null;
        if (state.FirstWeekMonday is not { } anchor)
        {
            body.Children.Add(Header("Меню", null));
            body.Children.Add(new TextBlock { Text = "Укажите неделю с меню 1–5", Foreground = palette.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(10, 0, 10, 6) });
            body.Children.Add(Divider());
            body.Children.Add(Footer("Не настроено", "Настроить →", false));
            card.Child = body; card.ToolTip = "Открыть программу и настроить цикл"; PlaceInCorner(); return;
        }
        var info = week = cycle.Describe(state.Today, new(anchor));
        var rows = cycle.Preview(info.Monday, info.Set);
        ready = state.DemoReady ? new(state.OutputDirectory, rows.Select(r => r.FileName).ToArray()) : SafeFind(state.OutputDirectory, info.Monday, rows);
        var weekend = info.TodayMenuNumber is null;
        // Sunday (or a pending reminder) with nothing prepared yet: the card asks for attention.
        var due = ready is null && (weekend || state.ReminderPending);
        card.BorderBrush = due ? palette.AccentLine : palette.Line;
        body.Children.Add(Header($"Неделя {info.WeekNumber}", weekend ? "вых." : $"№ {info.TodayMenuNumber}"));
        var days = new UniformGrid { Columns = 5, Rows = 1, Margin = new(5, 0, 5, 5) };
        for (var i = 0; i < rows.Count; i++) days.Children.Add(Day(rows[i], state.Today, ready?.Files[i]));
        body.Children.Add(days);
        body.Children.Add(Divider());
        body.Children.Add(busy ? Footer("Готовлю…", "", false)
            : ready is not null ? Footer("✓ Готовы", "Открыть →", false)
            : due ? Footer("Пора!", "Подготовить →", true)
            : Footer("Не готовы", "Подготовить →", false));
        card.Child = body;
        var today = state.Today.ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMMM", ru);
        card.ToolTip = $"{today} · меню {SetName(info.Set)}{(weekend ? " на следующей неделе" : "")}\n"
            + (ready is null ? "Щелчок — подготовить пять меню этой недели" : "Щелчок — открыть папку, по дню — открыть файл этого дня");
        PlaceInCorner();
    }

    private static string SetName(MenuSetId set) => set == MenuSetId.First ? "1–5" : "6–10";

    private static ExportResult? SafeFind(string output, DateOnly monday, IReadOnlyList<ExportRow> rows)
    {
        try { return ExportService.FindLatest(output, monday, rows); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static bool IsDark(string theme)
    {
        if (theme == WidgetLook.Dark) return true;
        if (theme == WidgetLook.Light) return false;
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light && light == 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private Grid Header(string title, string? badge)
    {
        var grid = new Grid { Margin = new(10, 6, 3, 4) };
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = palette.Ink, VerticalAlignment = VerticalAlignment.Center });
        if (badge is not null)
            line.Children.Add(new Border { Background = palette.Soft, CornerRadius = new(6), Padding = new(5, 0, 5, 1), Margin = new(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = badge, Foreground = palette.Accent, FontWeight = FontWeights.SemiBold, FontSize = 12 } });
        grid.Children.Add(line);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(Tool("⚙", "Открыть программу", openMain));
        if (hide is not null) tools.Children.Add(Tool("✕", "Скрыть до следующего запуска · вернуть: значок у часов → «Показать виджет»", hide));
        Grid.SetColumn(tools, 1); grid.Children.Add(tools);
        return grid;
    }

    private Button Tool(string glyph, string tip, Action action)
    {
        var button = new Button { Content = glyph, ToolTip = tip, Width = 20, Height = 20, Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0), Foreground = palette.Faint, FontSize = 11, Focusable = false };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    // One weekday as a tiny column: day, menu number, date. Today is highlighted, past days are dimmed.
    private Border Day(ExportRow row, DateOnly today, string? file)
    {
        var current = row.Date == today; var past = row.Date < today;
        var main = current ? palette.Accent : past ? palette.Faint : palette.Ink;
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        TextBlock Line(string text, double size, FontWeight weight, Brush brush) => new() { Text = text, FontSize = size, FontWeight = weight, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        stack.Children.Add(Line(ru.DateTimeFormat.GetAbbreviatedDayName(row.Day), 9.5, FontWeights.Normal, current ? palette.Accent : past ? palette.Faint : palette.Muted));
        stack.Children.Add(Line(row.MenuNumber.ToString(CultureInfo.InvariantCulture), 14, FontWeights.SemiBold, main));
        stack.Children.Add(Line(row.Date.ToString("dd.MM", ru), 9, current ? FontWeights.SemiBold : FontWeights.Normal, current ? palette.Accent : palette.Faint));
        var cell = new Border { Child = stack, Padding = new(0, 2, 0, 3), Margin = new(1, 0, 1, 0), CornerRadius = new(6), Background = current ? palette.Soft : Brushes.Transparent,
            ToolTip = $"{row.Date.ToString("dddd, d MMMM", ru)} · меню № {row.MenuNumber}" + (file is null ? "" : "\nЩелчок — открыть " + Path.GetFileName(file)) };
        if (file is not null)
            cell.MouseLeftButtonUp += (_, e) =>
            {
                if (!pressed || moving) return;
                pressed = false; e.Handled = true;
                Open(file, "файл меню");
            };
        return cell;
    }

    private Grid Footer(string status, string action, bool highlight)
    {
        var grid = new Grid { Margin = new(10, 4, 10, 5) };
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = status, Foreground = highlight ? palette.Accent : palette.Muted, FontWeight = highlight ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var link = new TextBlock { Text = action, Foreground = palette.Accent, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0), TextWrapping = TextWrapping.NoWrap };
        Grid.SetColumn(link, 1); grid.Children.Add(link);
        return grid;
    }

    private Border Divider() => new() { Height = 1, Background = palette.Line };

    private async Task ActivateAsync()
    {
        if (busy) return;
        Refresh(); // The folder may have been prepared or removed since the last tick.
        if (week is null || data is null) { openMain(); return; }
        if (ready is not null) { Open(ready.Directory, "папку с меню"); return; }
        await PrepareAsync(week);
    }

    // One-click weekly preparation from the templates the user already confirmed; the result is a new folder, templates stay untouched.
    private async Task PrepareAsync(WeekInfo info)
    {
        var store = new TemplateStore(data!, editor);
        MenuSet? set;
        try { set = await store.ReadAsync(info.Set, default); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException || ImportWindow.IsUserFileError(ex)) { set = null; }
        if (set is null)
        {
            MessageBox.Show($"Сначала добавьте пять файлов комплекта {SetName(info.Set)} в программе.", "MenuWidget");
            openMain(); return;
        }
        var question = $"Подготовить меню {SetName(info.Set)} на {info.Monday:dd.MM}–{info.Monday.AddDays(4):dd.MM}?\nФайлы появятся в отдельной папке, шаблоны не изменятся.";
        if (MessageBox.Show(question, "MenuWidget", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        busy = true; Refresh();
        try
        {
            var result = await new ExportService(store, editor, cycle).ExportAsync(new(info.Monday, info.Set, last!.OutputDirectory), default);
            prepared?.Invoke();
            Open(result.Directory, "папку с меню");
        }
        catch (Exception ex) { MessageBox.Show("Не удалось подготовить меню: " + ex.Message, "MenuWidget"); }
        finally { busy = false; Refresh(); }
    }

    private static void Open(string path, string what)
    {
        try
        {
            var start = Directory.Exists(path) ? new ProcessStartInfo("explorer.exe", '"' + path + '"') : new ProcessStartInfo(path);
            start.UseShellExecute = true; Process.Start(start);
        }
        catch (Exception ex) { MessageBox.Show($"Не удалось открыть {what}: {ex.Message}\n{path}", "MenuWidget"); }
    }

    private void PlaceInCorner()
    {
        var corner = last?.Corner ?? WidgetLook.TopRight;
        if (corner == WidgetLook.Custom || ActualHeight <= 0) return;
        var area = SystemParameters.WorkArea;
        Left = corner is WidgetLook.TopLeft or WidgetLook.BottomLeft ? area.Left : area.Right - ActualWidth;
        Top = corner is WidgetLook.TopLeft or WidgetLook.TopRight ? area.Top : area.Bottom - ActualHeight;
    }

    // Dragging means the user chose their own place: stop snapping to a corner.
    private void MarkCustomCorner()
    {
        if (data is null || last is null || last.Corner == WidgetLook.Custom) return;
        try { data.SaveSettings(data.ReadSettings() with { WidgetCorner = WidgetLook.Custom }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        last = last with { Corner = WidgetLook.Custom };
    }

    private void RestorePlacement()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width; Top = area.Top;
        if (data?.ReadWidget() is not { } saved || !double.IsFinite(saved.Left) || !double.IsFinite(saved.Top)) return;
        // Ignore a position left on a monitor that is no longer connected.
        if (saved.Left < SystemParameters.VirtualScreenLeft - Width + 80 || saved.Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
            || saved.Top < SystemParameters.VirtualScreenTop || saved.Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60) return;
        Left = saved.Left; Top = saved.Top;
    }

    public void SavePlacement(bool? hidden = null)
    {
        if (data is null) return;
        try { data.SaveWidget(new(Left, Top, hidden ?? data.ReadWidget()?.Hidden ?? false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static SolidColorBrush Brush(string hex) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; brush.Freeze(); return brush; }
}
