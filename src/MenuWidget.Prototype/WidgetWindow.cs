using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MenuWidget.Core;
using MenuWidget.Services;

namespace MenuWidget.Prototype;

// Snapshot the widget draws from; demo captures pass a fixed one instead of the user's profile.
internal record WidgetSnapshot(DateOnly Today, DateOnly? FirstWeekMonday, string OutputDirectory, bool Topmost, bool DemoReady = false);

// Compact desktop card: which week of the cycle it is, today's menu and a click to open the prepared files.
internal sealed class WidgetWindow : Window
{
    private static readonly Brush Ink = Brush("#26382F"), Muted = Brush("#65726A"), Faint = Brush("#A3ADA6"), Green = Brush("#28634D"), Soft = Brush("#E8EEE6"), Line = Brush("#E0E5DE");
    private readonly CycleService cycle = new();
    private readonly CultureInfo ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly Func<WidgetSnapshot> snapshot;
    private readonly Action openMain;
    private readonly Action? hide;
    private readonly LocalData? data;
    private readonly Border card = new() { Background = Brushes.White, CornerRadius = new(14), BorderBrush = Line, BorderThickness = new(1), Margin = new(12), Cursor = Cursors.Hand };
    private ExportResult? ready;
    private bool configured, pressed, moving;
    private Point pressPoint;

    public WidgetWindow(Func<WidgetSnapshot> snapshot, Action openMain, Action? hide, LocalData? data)
    {
        this.snapshot = snapshot; this.openMain = openMain; this.hide = hide; this.data = data;
        Title = "Меню для сайта · виджет"; Width = 344; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; FontFamily = new FontFamily("Segoe UI"); FontSize = 13; Foreground = Ink; Language = System.Windows.Markup.XmlLanguage.GetLanguage("ru-RU");
        card.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.16, Color = Colors.Black };
        Content = card;
        card.MouseLeftButtonDown += (_, e) => { pressed = true; pressPoint = e.GetPosition(this); };
        card.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - pressPoint).Length < 5) return;
            pressed = false; moving = true;
            try { DragMove(); } finally { moving = false; SavePlacement(); }
        };
        card.MouseLeftButtonUp += (_, _) => { if (pressed && !moving) { pressed = false; OpenWeek(); } };
        card.MouseLeave += (_, _) => pressed = false;
        RestorePlacement();
        Refresh();
    }

    public void Refresh()
    {
        var state = snapshot();
        Topmost = state.Topmost;
        configured = state.FirstWeekMonday is not null;
        var body = new StackPanel();
        if (state.FirstWeekMonday is not { } anchor)
        {
            ready = null;
            body.Children.Add(Header("Меню для сайта", "Укажите понедельник недели с меню 1–5", null));
            body.Children.Add(Divider());
            body.Children.Add(new TextBlock { Text = "Тогда здесь будет видно, какая сейчас неделя — 1 или 2 — и какое меню сегодня.", Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new(18, 12, 18, 12) });
            body.Children.Add(Divider());
            body.Children.Add(Footer("Цикл не настроен", "Настроить →"));
            card.Child = body; return;
        }
        var info = cycle.Describe(state.Today, new(anchor));
        var rows = cycle.Preview(info.Monday, info.Set);
        ready = state.DemoReady ? new(state.OutputDirectory, rows.Select(r => r.FileName).ToArray()) : SafeFind(state.OutputDirectory, info.Monday, rows);
        var weekend = info.TodayMenuNumber is null;
        var date = state.Today.ToDateTime(TimeOnly.MinValue).ToString("dddd, d MMMM", ru);
        var subtitle = $"{char.ToUpper(date[0], ru)}{date[1..]} · меню {(info.Set == MenuSetId.First ? "1–5" : "6–10")}";
        body.Children.Add(Header($"Неделя {info.WeekNumber}", subtitle, weekend ? "Выходной" : $"Сегодня № {info.TodayMenuNumber}"));
        body.Children.Add(Divider());
        var list = new StackPanel { Margin = new(8, 6, 8, 6) };
        foreach (var row in rows) list.Children.Add(Row(row, state.Today));
        body.Children.Add(list);
        body.Children.Add(Divider());
        body.Children.Add(ready is null
            ? Footer(weekend ? "Меню ещё не подготовлены" : "Меню недели не найдены", "Подготовить →")
            : Footer("✓ Пять меню готовы", "Открыть →"));
        card.Child = body;
        card.ToolTip = ready is null ? "Открыть программу и подготовить меню" : "Открыть папку с меню: " + ready.Directory;
    }

    private static ExportResult? SafeFind(string output, DateOnly monday, IReadOnlyList<ExportRow> rows)
    {
        try { return ExportService.FindLatest(output, monday, rows); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private Grid Header(string title, string subtitle, string? badge)
    {
        var grid = new Grid { Margin = new(18, 14, 10, 12) };
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (badge is not null)
            line.Children.Add(new Border { Background = Soft, CornerRadius = new(8), Padding = new(8, 2, 8, 3), Margin = new(10, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = badge, Foreground = Green, FontWeight = FontWeights.SemiBold, FontSize = 13 } });
        text.Children.Add(line);
        text.Children.Add(new TextBlock { Text = subtitle, Foreground = Muted, Margin = new(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(Tool("⚙", "Открыть программу", openMain));
        if (hide is not null) tools.Children.Add(Tool("✕", "Скрыть виджет · вернуть можно из значка в трее", hide));
        Grid.SetColumn(tools, 1); grid.Children.Add(tools);
        return grid;
    }

    private static Button Tool(string glyph, string tip, Action action)
    {
        var button = new Button { Content = glyph, ToolTip = tip, Width = 30, Height = 30, Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0), Foreground = Faint, FontSize = 15, Focusable = false };
        AutomationPropertiesName(button, tip);
        button.Click += (_, _) => action();
        return button;
    }
    private static void AutomationPropertiesName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

    private Border Row(ExportRow row, DateOnly today)
    {
        var current = row.Date == today; var past = row.Date < today;
        var grid = new Grid();
        foreach (var width in new[] { 34.0, 58.0, double.NaN, 64.0 }) grid.ColumnDefinitions.Add(new() { Width = double.IsNaN(width) ? new(1, GridUnitType.Star) : new(width) });
        void Cell(string text, int column, double size, FontWeight weight, Brush brush, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = brush, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(block, column); grid.Children.Add(block);
        }
        var main = current ? Green : past ? Faint : Ink;
        Cell(ru.DateTimeFormat.GetAbbreviatedDayName(row.Day), 0, 13, FontWeights.Normal, current ? Green : past ? Faint : Muted);
        Cell("№ " + row.MenuNumber, 1, 16, FontWeights.SemiBold, main);
        Cell(current ? "сегодня" : "", 2, 12, FontWeights.Normal, Green);
        Cell(row.Date.ToString("dd.MM", ru), 3, 15, current ? FontWeights.SemiBold : FontWeights.Normal, main, HorizontalAlignment.Right);
        return new Border { Child = grid, Padding = new(10, 6, 10, 6), CornerRadius = new(9), Background = current ? Soft : Brushes.Transparent };
    }

    private static Grid Footer(string status, string action)
    {
        var grid = new Grid { Margin = new(18, 11, 18, 13) };
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = status, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var link = new TextBlock { Text = action, Foreground = Green, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0) };
        Grid.SetColumn(link, 1); grid.Children.Add(link);
        return grid;
    }

    private static Border Divider() => new() { Height = 1, Background = Line };

    private void OpenWeek()
    {
        Refresh(); // The folder may have been prepared or removed since the last tick.
        if (!configured || ready is null || data is null) { openMain(); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", '"' + ready.Directory + '"') { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Не удалось открыть папку с меню: " + ex.Message + "\n" + ready.Directory, "MenuWidget"); }
    }

    private void RestorePlacement()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12; Top = area.Top + 12;
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
