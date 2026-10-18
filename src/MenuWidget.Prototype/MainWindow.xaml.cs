using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MenuWidget.Core;
using MenuWidget.Services;
using Microsoft.Win32;

namespace MenuWidget.Prototype;

public partial class MainWindow : Window
{
    private readonly CycleService cycle = new();
    private readonly XlsxDateEditor editor = new();
    private readonly LocalData data;
    private readonly TemplateStore store;
    private readonly MenuSet?[] saved = new MenuSet?[2];
    private readonly List<ImportItem>[] drafts = [[], []];
    private readonly string?[][] demoFiles = [new string?[5], new string?[5]];
    private readonly CultureInfo ru = CultureInfo.GetCultureInfo("ru-RU");
    private UserSettings settings;
    private bool loaded, busy;
    private string? message;
    private bool messageIsError;
    private string[]? dragProbeFiles;
    public bool IsDemo { get; }
    public bool IsWorking => busy;
    public void RefreshToday() => RefreshPreview();
    private GitHubUpdateService? updater;
    private UpdateCandidate? candidate;
    private CancellationTokenSource? updateCancellation;
    private int SetIndex => SecondSet.IsChecked == true ? 1 : 0;

    public MainWindow(bool demo = false)
    {
        IsDemo = demo;
        data = new(demo ? Path.Combine(Path.GetTempPath(), "MenuWidget-Demo-" + Guid.NewGuid().ToString("N")) : null);
        store = new(data, editor);
        settings = demo ? new() { OutputDirectory = "Папка готовых меню" } : data.ReadSettings();
        // A damaged saved set must not stop the program from opening: show it as empty with an explanation.
        if (!demo) for (var i = 0; i < 2; i++)
        {
            try { saved[i] = store.ReadAsync((MenuSetId)i, default).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException || ImportWindow.IsUserFileError(ex))
            { message = $"Сохранённый комплект {(i == 0 ? "1–5" : "6–10")} не прочитан: {ex.Message} Добавьте его файлы заново."; messageIsError = true; }
        }
        InitializeComponent();
        Title = demo ? "MenuWidget · демонстрация" : "Меню для сайта";
        VersionText.Text = "MenuWidget · " + typeof(App).Assembly.GetName().Version!.ToString(3);
        WeekPicker.SelectedDate = cycle.SuggestedMonday(DateOnly.FromDateTime(DateTime.Now)).ToDateTime(TimeOnly.MinValue);
        AnchorPicker.SelectedDate = settings.FirstWeekMonday?.ToDateTime(TimeOnly.MinValue);
        ReminderTime.Text = settings.ReminderTime.ToString("HH:mm"); OutputPath.Text = settings.OutputDirectory;
        ReminderEnabled.IsChecked = settings.ReminderEnabled; AutoStartCheck.IsChecked = settings.AutoStart;
        TopmostCheck.IsChecked = settings.Topmost; Topmost = settings.Topmost;
        ShowWidgetSettings();
        loaded = true;
        if (settings.FirstWeekMonday is { } anchor && cycle.GetSet(DateOnly.FromDateTime(WeekPicker.SelectedDate!.Value), new(anchor)) == MenuSetId.Second) SecondSet.IsChecked = true;
        ScenePicker.Visibility = demo ? Visibility.Visible : Visibility.Collapsed;
        if (demo) { VersionText.Text = "Демонстрация интерфейса"; ScenePicker.SelectedIndex = 0; SetScene("welcome"); }
        else ShowPanel(saved.All(s => s is null) && settings.FirstWeekMonday is null ? "welcome" : "home");
        RefreshPreview();
    }

    private void ShowPanel(string panel)
    {
        WelcomePanel.Visibility = panel == "welcome" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = panel == "settings" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPanel.Visibility = panel == "updates" ? Visibility.Visible : Visibility.Collapsed;
        HomePanel.Visibility = panel == "home" ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetScene(string scene)
    {
        if (!IsDemo) return;
        var item = ScenePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == scene);
        if (item is not null && ScenePicker.SelectedItem != item) ScenePicker.SelectedItem = item;
        ShowPanel(scene is "welcome" or "settings" or "updates" ? scene : "home");
        if (scene is "empty" or "partial" or "ready" or "error")
        {
            Array.Clear(demoFiles[SetIndex]); message = null; messageIsError = scene == "error";
            var count = scene == "ready" ? 5 : scene == "partial" ? 2 : 0;
            var date = DateOnly.FromDateTime(WeekPicker.SelectedDate ?? DateTime.Today);
            for (var i = 0; i < count; i++) demoFiles[SetIndex][i] = $"{date.AddDays(i):yyyy-MM-dd}-sm.xlsx";
            if (scene == "error") message = "Не удалось прочитать файл. Выберите другой: сохранённый комплект не изменён.";
        }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (!loaded) return;
        var now = DateTime.Now; TodayText.Text = now.ToString("dddd, d MMMM yyyy", ru);
        CurrentText.Text = settings.FirstWeekMonday is not { } anchor ? "Настройте цикл меню" : now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? "Сегодня выходной · готовим новую неделю" : $"Сегодня меню № {1 + ((int)now.DayOfWeek + 6) % 7 + (cycle.GetSet(DateOnly.FromDateTime(now), new(anchor)) == MenuSetId.Second ? 5 : 0)}";
        var monday = DateOnly.FromDateTime(WeekPicker.SelectedDate ?? now);
        var draft = drafts[SetIndex]; var set = saved[SetIndex];
        var count = IsDemo ? demoFiles[SetIndex].Count(f => f is not null) : draft.Count > 0 ? draft.Count : set?.Templates.Count ?? 0;
        var ready = IsDemo ? count == 5 : draft.Count == 0 && set?.Templates.Count == 5;
        FileStatus.Text = count == 0 ? "Пять файлов Excel · понедельник — пятница" : $"{(ready ? "Сохранено" : "Добавлено")} {count} из 5 · комплект {(SetIndex == 0 ? "1–5" : "6–10")}";
        PrepareButton.IsEnabled = !busy && ready && monday.DayOfWeek == DayOfWeek.Monday;
        SaveDraftButton.Visibility = !IsDemo && draft.Count == 5 ? Visibility.Visible : Visibility.Collapsed;
        SaveDraftButton.IsEnabled = !busy;
        Feedback.Text = message ?? (monday.DayOfWeek != DayOfWeek.Monday ? "Выберите понедельник начала недели." : draft.Count > 0 ? $"Добавьте недостающие файлы и подтвердите порядок. Сохранённые шаблоны пока не изменены." : ready ? "Проверьте даты. Исходные шаблоны останутся без изменений." : $"Добавьте пять файлов для выбранного комплекта.");
        Feedback.Foreground = messageIsError ? Brushes.Firebrick : (Brush)FindResource("Muted");
        ReminderSummary.Text = settings.ReminderEnabled ? $"Воскресенье, {settings.ReminderTime:HH:mm}" : "Выключено";
        DayRows.Items.Clear();
        if (monday.DayOfWeek != DayOfWeek.Monday) return;
        foreach (var row in cycle.Preview(monday, (MenuSetId)SetIndex))
        {
            var index = (int)row.Day - 1;
            var name = IsDemo ? demoFiles[SetIndex][index] : draft.Count > 0 ? (index < draft.Count ? Path.GetFileName(draft[index].Path) : null) : set?.Templates[index].OriginalName;
            var grid = new Grid { Height = 31 };
            foreach (var width in new[] { new GridLength(38), new GridLength(43), new GridLength(1, GridUnitType.Star), new GridLength(76) }) grid.ColumnDefinitions.Add(new() { Width = width });
            void Text(string text, int column, bool muted = false)
            {
                var block = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
                if (muted) block.Foreground = (Brush)FindResource("Muted");
                Grid.SetColumn(block, column); grid.Children.Add(block);
            }
            Text(ru.DateTimeFormat.GetAbbreviatedDayName(row.Day), 0); Text("№ " + row.MenuNumber, 1);
            Text(name ?? "Файл не добавлен", 2, true); Text(row.Date.ToString("dd.MM.yyyy", ru), 3); DayRows.Items.Add(grid);
        }
    }

    private async Task LoadFilesAsync(string[] paths)
    {
        if (busy) return;
        if (IsDemo) { Array.Clear(demoFiles[SetIndex]); for (var i = 0; i < Math.Min(5, paths.Length); i++) demoFiles[SetIndex][i] = Path.GetFileName(paths[i]); RefreshPreview(); return; }
        var index = SetIndex;
        var accepted = false;
        try
        {
            if (paths.Length is < 1 or > 5 || paths.Any(p => !p.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal)) || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                throw new InvalidDataException("Выберите от одного до пяти разных файлов .xlsx. Временные файлы не подходят.");
            SetBusy(true, "Читаю файлы меню…");
            var proposed = paths.Length == 5 ? new List<ImportItem>() : drafts[index].ToList();
            foreach (var path in paths)
            {
                if (proposed.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Один и тот же файл выбран дважды.");
                var info = await Task.Run(() => editor.Inspect(path)); proposed.Add(new(path, info));
            }
            if (proposed.Count > 5) throw new InvalidDataException("В комплекте должно быть ровно пять файлов. Выберите все пять заново.");
            drafts[index] = proposed;
            accepted = true;
            message = null; messageIsError = false;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
        if (accepted && drafts[index].Count == 5 && index == SetIndex) await ConfirmDraftAsync();
    }

    private async Task ConfirmDraftAsync()
    {
        if (busy || drafts[SetIndex].Count != 5) return;
        var index = SetIndex;
        // ISO date names sort chronologically; the user confirms/changes every assignment.
        var ordered = drafts[index].OrderBy(i => Path.GetFileName(i.Path), StringComparer.OrdinalIgnoreCase).ToList();
        var dialog = new ImportWindow(ordered, (MenuSetId)index, editor) { Owner = this };
        if (dialog.ShowDialog() != true) { message = "Порядок пока не подтверждён. Сохранённый комплект не изменён."; RefreshPreview(); return; }
        try
        {
            SetBusy(true, "Сохраняю шаблоны…");
            await store.ReplaceAsync(dialog.Result, default);
            saved[index] = await store.ReadAsync((MenuSetId)index, default); drafts[index].Clear();
            message = "Пять шаблонов сохранены. Выберите неделю и подготовьте файлы."; messageIsError = false;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo) { MessageBox.Show(this, "Демонстрационный режим не изменяет файлы."); return; }
        if (busy) return;
        try
        {
            var request = new ExportRequest(DateOnly.FromDateTime(WeekPicker.SelectedDate ?? DateTime.Today), (MenuSetId)SetIndex, settings.OutputDirectory);
            SetBusy(true, "Подготавливаю пять меню…");
            var result = await new ExportService(store, editor, cycle).ExportAsync(request, default);
            message = "Готово: пять меню сохранены в отдельную папку."; messageIsError = false;
            ((App)Application.Current).Runtime?.AcknowledgeReminder();
            try { Process.Start(new ProcessStartInfo("explorer.exe", '"' + result.Directory + '"') { UseShellExecute = true }); }
            catch { message += " Папка: " + result.Directory; }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (AnchorPicker.SelectedDate is not { DayOfWeek: DayOfWeek.Monday } date) { SettingsFeedback.Text = "Укажите понедельник недели с меню 1–5."; return; }
        if (!TimeOnly.TryParseExact(ReminderTime.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) { SettingsFeedback.Text = "Введите время в формате ЧЧ:ММ, например 18:00."; return; }
        if (!IsDemo && OutputFolderProblem(OutputPath.Text) is { } problem) { SettingsFeedback.Text = problem; return; }
        try
        {
            var updated = settings with { FirstWeekMonday = DateOnly.FromDateTime(date), ReminderTime = time, ReminderEnabled = ReminderEnabled.IsChecked == true, AutoStart = AutoStartCheck.IsChecked == true, Topmost = TopmostCheck.IsChecked == true, OutputDirectory = OutputPath.Text,
                WidgetTheme = SelectedTag(WidgetThemeBox, "system"), WidgetCorner = SelectedTag(WidgetCornerBox, "top-right"), WidgetOpacity = (int)Math.Round(WidgetOpacitySlider.Value), WidgetLocked = WidgetLockedCheck.IsChecked == true };
            if (!IsDemo) await ((App)Application.Current).Runtime!.ApplySettingsAsync(settings, updated);
            settings = updated;
            SettingsFeedback.Text = IsDemo ? "Применено только в демонстрации." : "Настройки сохранены. После закрытия программы они останутся.";
            SelectCycleSet(); RefreshPreview();
            if (!IsDemo) ((App)Application.Current).Runtime?.RefreshWidget();
        }
        catch (Exception ex) { SettingsFeedback.Text = "Не удалось сохранить настройки: " + ex.Message; }
    }
    private void ShowWidgetSettings()
    {
        SelectTag(WidgetThemeBox, settings.WidgetTheme); SelectTag(WidgetCornerBox, settings.WidgetCorner);
        WidgetOpacitySlider.Value = Math.Clamp(settings.WidgetOpacity, 80, 100); WidgetLockedCheck.IsChecked = settings.WidgetLocked;
    }
    private static void SelectTag(ComboBox box, string tag) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];
    private static string SelectedTag(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
    private void WidgetOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (WidgetOpacityText is not null) WidgetOpacityText.Text = $"Непрозрачность фона: {Math.Round(e.NewValue)}%"; }
    // Checked on save so a bad folder is reported in settings, not later in the middle of preparing files.
    private static string? OutputFolderProblem(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Выберите папку для готовых меню.";
        try
        {
            if (!Path.IsPathFullyQualified(path)) return "Выберите папку кнопкой «Выбрать папку».";
            Directory.CreateDirectory(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return "В эту папку нельзя сохранить меню: " + ex.Message + " Выберите другую."; }
    }
    private void SelectCycleSet()
    {
        if (settings.FirstWeekMonday is not { } anchor || WeekPicker.SelectedDate is not { } selected || selected.DayOfWeek != DayOfWeek.Monday) return;
        if (cycle.GetSet(DateOnly.FromDateTime(selected), new(anchor)) == MenuSetId.First) FirstSet.IsChecked = true; else SecondSet.IsChecked = true;
    }
    private void SetBusy(bool value, string? text = null) { busy = value; FirstSet.IsEnabled = SecondSet.IsEnabled = WeekPicker.IsEnabled = !value; if (text is not null) { message = text; messageIsError = false; } RefreshPreview(); }
    private void ShowError(Exception ex) { message = "Не удалось выполнить действие: " + ex.Message; messageIsError = true; }
    private async void Browse_Click(object sender, RoutedEventArgs e) { if (busy) return; var picker = new OpenFileDialog { Filter = "Меню Excel (*.xlsx)|*.xlsx", Multiselect = true }; if (picker.ShowDialog(this) == true) await LoadFilesAsync(picker.FileNames); }
    private async void Files_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await LoadFilesAsync(paths); }
    private void Files_DragOver(object sender, DragEventArgs e) { e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void SaveDraft_Click(object sender, RoutedEventArgs e) => await ConfirmDraftAsync();
    private void Set_Changed(object sender, RoutedEventArgs e) { if (loaded) { message = null; messageIsError = false; RefreshPreview(); } }
    private void Week_Changed(object? sender, SelectionChangedEventArgs e) { if (loaded) { message = null; SelectCycleSet(); RefreshPreview(); } }
    private void Scene_Changed(object sender, SelectionChangedEventArgs e) { if (loaded && ScenePicker.SelectedItem is ComboBoxItem item) SetScene((string)item.Tag); }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        // Dragging the widget switches its position to "custom" on disk; show that instead of a stale choice.
        if (!IsDemo) try { settings = settings with { WidgetCorner = data.ReadSettings().WidgetCorner }; ShowWidgetSettings(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        ShowPanel("settings");
    }
    private async void Updates_Click(object sender, RoutedEventArgs e) { if (!busy) { ShowPanel("updates"); await CheckUpdatesAsync(); } }
    private async void CheckAgain_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => updateCancellation?.Cancel();
    private void TestReminder_Click(object sender, RoutedEventArgs e) { if (!IsDemo) ((App)Application.Current).Runtime!.TestNotification(); }
    private async Task CheckUpdatesAsync()
    {
        if (busy || IsDemo) return;
        candidate = null; InstallUpdate.IsEnabled = false; UpdateNotes.Text = "";
        updateCancellation = new(); SetBusy(true); CancelUpdate.Visibility = Visibility.Visible;
        UpdateStatus.Text = "Проверяю GitHub Releases…";
        try
        {
            updater ??= new(data, typeof(App).Assembly.GetName().Version!);
            var result = await updater.CheckAsync(updateCancellation.Token); candidate = result.Candidate;
            UpdateStatus.Text = result.Status switch { UpdateCheckStatus.Available => $"Доступна версия {candidate!.Version}", UpdateCheckStatus.UpToDate => "Установлена актуальная версия.", _ => "Стабильных версий пока нет." };
            UpdateNotes.Text = candidate?.Notes ?? ""; InstallUpdate.IsEnabled = candidate is not null;
        }
        catch (OperationCanceledException) { UpdateStatus.Text = "Проверка отменена или истекло время ожидания."; }
        catch (Exception ex) { UpdateStatus.Text = "Не удалось проверить обновления: " + ex.Message; }
        finally { updateCancellation.Dispose(); updateCancellation = null; CancelUpdate.Visibility = Visibility.Collapsed; SetBusy(false); }
    }
    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (busy || candidate is null || updater is null || IsDemo) return;
        updateCancellation = new(); SetBusy(true); InstallUpdate.IsEnabled = false; CancelUpdate.Visibility = Visibility.Visible;
        UpdateStatus.Text = "Скачиваю и проверяю установщик…";
        try
        {
            var progress = new Progress<DownloadProgress>(p => UpdateProgress.Value = p.TotalBytes > 0 ? 100.0 * p.ReceivedBytes / p.TotalBytes.Value : 0);
            var path = await updater.DownloadVerifiedAsync(candidate, progress, updateCancellation.Token);
            data.SaveSettings(settings);
            await updater.LaunchInstallerAsync(path, updateCancellation.Token);
            ((App)Application.Current).Runtime!.RequestExit(forUpdate: true);
        }
        catch (OperationCanceledException) { UpdateStatus.Text = "Загрузка отменена. Версия программы не изменена."; }
        catch (Exception ex) { UpdateStatus.Text = "Не удалось установить обновление: " + ex.Message; }
        finally { updateCancellation.Dispose(); updateCancellation = null; CancelUpdate.Visibility = Visibility.Collapsed; InstallUpdate.IsEnabled = candidate is not null; SetBusy(false); }
    }
    private void Home_Click(object sender, RoutedEventArgs e) { ShowPanel("home"); RefreshPreview(); }
    private void Topmost_Changed(object sender, RoutedEventArgs e) { if (sender is CheckBox check) Topmost = check.IsChecked == true; }
    private void OutputFolder_Click(object sender, RoutedEventArgs e) { var picker = new OpenFolderDialog { Title = "Куда сохранять готовые меню" }; if (picker.ShowDialog(this) == true) OutputPath.Text = picker.FolderName; }
    public void EnableDragProbe(string directory) { dragProbeFiles = Directory.GetFiles(directory, "*.xlsx"); SetScene("empty"); DragProbeSource.Visibility = Visibility.Visible; }
    private void DragProbe_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) { if (dragProbeFiles is not null && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragDrop.DoDragDrop(DragProbeSource, new DataObject(DataFormats.FileDrop, dragProbeFiles), DragDropEffects.Copy); }
}
