using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MenuWidget.Core;
using Microsoft.Win32;

namespace MenuWidget.Prototype;

public partial class MainWindow : Window
{
    private readonly CycleService cycle = new();
    private readonly string?[][] files = [new string?[5], new string?[5]];
    private readonly CultureInfo ru = CultureInfo.GetCultureInfo("ru-RU");
    private bool loaded;
    private DateOnly? anchor;
    private string? error;
    private int SetIndex => SecondSet.IsChecked == true ? 1 : 0;

    public MainWindow()
    {
        InitializeComponent();
        WeekPicker.SelectedDate = cycle.SuggestedMonday(DateOnly.FromDateTime(DateTime.Now)).ToDateTime(TimeOnly.MinValue);
        loaded = true;
        ScenePicker.SelectedIndex = 0;
        SetScene("welcome");
    }

    public void SetScene(string scene)
    {
        var sceneItem = ScenePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == scene);
        if (sceneItem is not null && ScenePicker.SelectedItem != sceneItem) ScenePicker.SelectedItem = sceneItem;
        WelcomePanel.Visibility = scene == "welcome" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = scene == "settings" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPanel.Visibility = scene == "updates" ? Visibility.Visible : Visibility.Collapsed;
        HomePanel.Visibility = scene is "welcome" or "settings" or "updates" ? Visibility.Collapsed : Visibility.Visible;
        if (scene is "empty" or "partial" or "ready" or "error")
        {
            Array.Clear(files[SetIndex]); error = null;
            var count = scene == "ready" ? 5 : scene == "partial" ? 2 : 0;
            var monday = DateOnly.FromDateTime(WeekPicker.SelectedDate ?? DateTime.Today);
            for (var i = 0; i < count; i++) files[SetIndex][i] = $"{monday.AddDays(i):yyyy-MM-dd}-sm.xlsx";
            if (scene == "error") error = "Не удалось прочитать файл «меню.xlsx». Выберите другой файл. Прежний комплект не изменён.";
        }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (!loaded) return;
        var now = DateTime.Now;
        TodayText.Text = now.ToString("dddd, d MMMM yyyy", ru);
        CurrentText.Text = anchor is null ? "Настройте цикл меню" : now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? "Сегодня выходной · готовим новую неделю"
            : $"Сегодня меню № {(((int)now.DayOfWeek + 6) % 7) + 1 + (cycle.GetSet(DateOnly.FromDateTime(now), new(anchor.Value)) == MenuSetId.Second ? 5 : 0)}";
        var monday = DateOnly.FromDateTime(WeekPicker.SelectedDate ?? now);
        var count = files[SetIndex].Count(f => f is not null);
        FileStatus.Text = count == 0 ? "Пять файлов Excel · понедельник — пятница" : $"Выбрано {count} из 5 файлов · комплект {(SetIndex == 0 ? "1–5" : "6–10")}";
        PrepareButton.IsEnabled = count == 5 && monday.DayOfWeek == DayOfWeek.Monday && error is null;
        Feedback.Text = error ?? (monday.DayOfWeek != DayOfWeek.Monday ? "Выберите понедельник начала недели." : count == 5 ? "Демонстрация: проверьте порядок. Экспорт подключим на следующем этапе." : $"Добавьте ещё {5 - count} файлов. Исходные меню останутся без изменений.");
        Feedback.Foreground = error is null ? (Brush)FindResource("Muted") : new SolidColorBrush(Color.FromRgb(159, 56, 47));
        DayRows.Items.Clear();
        if (monday.DayOfWeek != DayOfWeek.Monday) return;
        foreach (var row in cycle.Preview(monday, (MenuSetId)SetIndex))
        {
            var index = (int)row.Day - 1;
            var grid = new Grid { Height = 31 };
            foreach (var width in new[] { new GridLength(38), new GridLength(43), new GridLength(1, GridUnitType.Star), new GridLength(76) }) grid.ColumnDefinitions.Add(new() { Width = width });
            void Text(string value, int col, Brush? brush = null)
            {
                var text = new TextBlock { Text = value, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, ToolTip = value };
                if (brush is not null) text.Foreground = brush;
                Grid.SetColumn(text, col); grid.Children.Add(text);
            }
            Text(ru.DateTimeFormat.GetAbbreviatedDayName(row.Day), 0);
            Text("№ " + row.MenuNumber, 1);
            Text(files[SetIndex][index] is { } file ? Path.GetFileName(file) : "Файл не добавлен", 2, (Brush)FindResource("Muted"));
            Text(row.Date.ToString("dd.MM.yyyy", ru), 3);
            DayRows.Items.Add(grid);
        }
    }

    private void LoadFiles(string[] paths)
    {
        error = null;
        if (paths.Length is < 1 or > 5 || paths.Any(p => !p.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal)) || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
        {
            error = "Выберите от одного до пяти разных файлов .xlsx. Временные файлы Excel не подходят.";
            RefreshPreview(); return;
        }
        Array.Clear(files[SetIndex]);
        for (var i = 0; i < paths.Length; i++) files[SetIndex][i] = paths[i];
        RefreshPreview();
        Feedback.Text = "Макет принял имена файлов. Проверка Excel, изменение порядка и сохранение появятся на следующем этапе.";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Файлы Excel (*.xlsx)|*.xlsx", Multiselect = true, Title = "Выберите файлы меню — в порядке дней недели" };
        if (dialog.ShowDialog(this) == true) LoadFiles(dialog.FileNames);
    }
    private void Files_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Files_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) LoadFiles(paths); }
    private void Set_Changed(object sender, RoutedEventArgs e) { if (loaded) { error = null; RefreshPreview(); } }
    private void Week_Changed(object? sender, SelectionChangedEventArgs e) => RefreshPreview();
    private void Scene_Changed(object sender, SelectionChangedEventArgs e) { if (loaded && ScenePicker.SelectedItem is ComboBoxItem item) SetScene((string)item.Tag); }
    private void Settings_Click(object sender, RoutedEventArgs e) => SetScene("settings");
    private void Updates_Click(object sender, RoutedEventArgs e) => SetScene("updates");
    private void Home_Click(object sender, RoutedEventArgs e) { WelcomePanel.Visibility = SettingsPanel.Visibility = UpdatesPanel.Visibility = Visibility.Collapsed; HomePanel.Visibility = Visibility.Visible; RefreshPreview(); }
    private void Topmost_Changed(object sender, RoutedEventArgs e) { if (sender is CheckBox check) Topmost = check.IsChecked == true; }
    private void Prepare_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "Это прототип интерфейса. Экспорт файлов будет подключён на этапе 2. Исходники не изменены.", "Предпросмотр сценария");
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (AnchorPicker.SelectedDate is not { DayOfWeek: DayOfWeek.Monday } selected) { SettingsFeedback.Text = "Укажите понедельник недели с меню 1–5."; return; }
        if (!TimeOnly.TryParseExact(ReminderTime.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) { SettingsFeedback.Text = "Введите время в формате ЧЧ:ММ, например 18:00."; return; }
        anchor = DateOnly.FromDateTime(selected);
        ReminderSummary.Text = ReminderEnabled.IsChecked == true ? $"Воскресенье, {time:HH:mm} · макет" : "Выключено";
        SettingsFeedback.Text = "Применено только в макете. Настройки ещё не сохраняются на диск; системное напоминание не создано.";
        RefreshPreview();
    }
}
