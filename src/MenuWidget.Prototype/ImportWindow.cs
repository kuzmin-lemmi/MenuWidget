using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MenuWidget.Core;
using MenuWidget.Services;

namespace MenuWidget.Prototype;

internal sealed class ImportItem(string path, WorkbookInfo info)
{
    public string Path { get; } = path;
    public string[] Sheets { get; } = info.Sheets.ToArray();
    public string Sheet { get; set; } = info.DefaultCell.SheetName;
    public string Address { get; set; } = info.DefaultCell.Address;
}

internal sealed class ImportWindow : Window
{
    private readonly List<ImportItem> items;
    private readonly MenuSetId set;
    private readonly XlsxDateEditor editor;
    private readonly StackPanel rows = new() { Width = 620 };
    private readonly TextBlock error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) };
    private readonly string[] days = ["Понедельник", "Вторник", "Среда", "Четверг", "Пятница"];
    public MenuSet Result => new(set, items.Select((item, i) => new MenuTemplate((DayOfWeek)(i + 1), item.Path, System.IO.Path.GetFileName(item.Path), new(item.Sheet, item.Address.Trim().ToUpperInvariant()))).ToArray());

    public ImportWindow(IEnumerable<ImportItem> items, MenuSetId set, XlsxDateEditor editor)
    {
        this.items = items.ToList(); this.set = set; this.editor = editor;
        Title = "Проверьте пять меню"; Width = 710; Height = Math.Min(760, SystemParameters.WorkArea.Height - 20); MinWidth = 610; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13; Background = new SolidColorBrush(Color.FromRgb(245, 245, 239));
        var root = new DockPanel { Margin = new(22) };
        var title = new StackPanel { Margin = new(0, 0, 0, 18) };
        title.Children.Add(new TextBlock { Text = "Проверьте порядок и даты", FontSize = 23, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "Стрелками расположите файлы с понедельника по пятницу. Укажите лист и ячейку, где стоит дата (обычно J1).", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var bottom = new StackPanel(); bottom.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new(0, 0, 10, 0) };
        var save = new Button { Content = "Сохранить пять шаблонов", Background = (Brush)Application.Current.FindResource("Green"), Foreground = Brushes.White, IsDefault = true };
        save.Click += (_, _) =>
        {
            try { foreach (var item in this.items) _ = editor.ReadDate(item.Path, new(item.Sheet, item.Address)); DialogResult = true; }
            catch (Exception ex) when (IsUserFileError(ex)) { error.Text = ex.Message; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        // No scrolling: on a short screen the five rows scale down together.
        root.Children.Add(new Viewbox { Child = rows, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top }); Content = root;
        Rebuild();
    }

    // InvalidDataException is not an IOException: without it a missing date or a half-typed address closed the program.
    internal static bool IsUserFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    private void Rebuild()
    {
        rows.Children.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var index = i; var item = items[i];
            var body = new StackPanel();
            var heading = new DockPanel();
            var order = new StackPanel { Orientation = Orientation.Horizontal };
            var up = new Button { Content = "↑", Padding = new(10, 4, 10, 4), Margin = new(0, 0, 5, 0), IsEnabled = i > 0, ToolTip = "На день раньше" };
            var down = new Button { Content = "↓", Padding = new(10, 4, 10, 4), IsEnabled = i < 4, ToolTip = "На день позже" };
            up.Click += (_, _) => { (items[index], items[index - 1]) = (items[index - 1], items[index]); Rebuild(); };
            down.Click += (_, _) => { (items[index], items[index + 1]) = (items[index + 1], items[index]); Rebuild(); };
            order.Children.Add(up); order.Children.Add(down); DockPanel.SetDock(order, Dock.Right); heading.Children.Add(order);
            heading.Children.Add(new TextBlock { Text = $"{days[i]} · меню № {i + 1 + (set == MenuSetId.Second ? 5 : 0)}", FontWeight = FontWeights.SemiBold }); body.Children.Add(heading);
            body.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(item.Path), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 8) });
            var fields = new StackPanel { Orientation = Orientation.Horizontal };
            var sheet = new ComboBox { ItemsSource = item.Sheets, SelectedItem = item.Sheet, Width = 155, Padding = new(4), ToolTip = "Лист с меню" };
            var address = new TextBox { Text = item.Address, Width = 58, Margin = new(8, 0, 8, 0), ToolTip = "Ячейка даты" };
            var date = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextWrapping = TextWrapping.Wrap, Width = 220 };
            void Validate()
            {
                item.Sheet = (string?)sheet.SelectedItem ?? item.Sheet; item.Address = address.Text.Trim().ToUpperInvariant();
                try { date.Text = "Сейчас: " + editor.ReadDate(item.Path, new(item.Sheet, item.Address)).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture); date.Foreground = (Brush)Application.Current.FindResource("Muted"); }
                catch (Exception ex) when (IsUserFileError(ex)) { date.Text = ex.Message; date.Foreground = Brushes.Firebrick; }
            }
            sheet.SelectionChanged += (_, _) => Validate(); address.TextChanged += (_, _) => Validate();
            fields.Children.Add(sheet); fields.Children.Add(address); fields.Children.Add(date); body.Children.Add(fields); Validate();
            rows.Children.Add(new Border { Background = Brushes.White, CornerRadius = new(10), Padding = new(12, 9, 12, 9), Margin = new(0, 0, 0, 8), Child = body });
        }
    }
}
