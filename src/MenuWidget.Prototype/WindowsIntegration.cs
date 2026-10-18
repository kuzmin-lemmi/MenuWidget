using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MenuWidget.Core;
using Microsoft.Win32;

namespace MenuWidget.Prototype;

internal sealed class WindowsIntegration : IReminderScheduler
{
    private static string Suffix => WindowsIdentity.GetCurrent().User!.Value;
    internal static string InstanceName => @"Local\MenuWidget." + Suffix;
    private static string WeeklyName => "MenuWidget.Reminder." + Suffix;
    private static string SnoozeName => "MenuWidget.Snooze." + Suffix;
    private static string Executable => Environment.ProcessPath ?? throw new IOException("Не найден путь программы.");
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        if (enabled) key.SetValue("MenuWidget", '"' + Executable + "\" --background");
        else key.DeleteValue("MenuWidget", throwOnMissingValue: false);
    }

    public Task ApplyAsync(ReminderSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.Enabled) return RemoveAsync(cancellationToken);
        WithService(service =>
        {
            dynamic task = Definition(service);
            dynamic weekly = task.Triggers.Create(3);
            var today = DateTime.Today;
            var sunday = today.AddDays((7 - (int)today.DayOfWeek) % 7).Add(settings.SundayTime.ToTimeSpan());
            if (sunday <= DateTime.Now) sunday = sunday.AddDays(7);
            weekly.StartBoundary = sunday.ToString("yyyy-MM-ddTHH:mm:ss"); weekly.DaysOfWeek = 1; weekly.WeeksInterval = 1;
            dynamic logon = task.Triggers.Create(9); logon.UserId = Suffix;
            service.GetFolder(@"\").RegisterTaskDefinition(WeeklyName, task, 6, Suffix, null, 3);
        });
        return Task.CompletedTask;
    }

    public Task SnoozeAsync(DateTimeOffset until, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WithService(service =>
        {
            dynamic task = Definition(service);
            dynamic trigger = task.Triggers.Create(1); trigger.StartBoundary = until.LocalDateTime.ToString("yyyy-MM-ddTHH:mm:ss");
            service.GetFolder(@"\").RegisterTaskDefinition(SnoozeName, task, 6, Suffix, null, 3);
        });
        return Task.CompletedTask;
    }
    public Task RemoveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WithService(service => { DeleteTask(service, WeeklyName); DeleteTask(service, SnoozeName); });
        return Task.CompletedTask;
    }
    public void RemoveAll() { SetAutoStart(false); RemoveAsync(default).GetAwaiter().GetResult(); }

    private static dynamic Definition(dynamic service)
    {
        dynamic task = service.NewTask(0);
        task.RegistrationInfo.Description = "MenuWidget: напоминание о подготовке питания";
        task.Principal.UserId = Suffix; task.Principal.LogonType = 3; task.Principal.RunLevel = 0;
        task.Settings.Enabled = true; task.Settings.StartWhenAvailable = true;
        task.Settings.DisallowStartIfOnBatteries = false; task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S"; task.Settings.MultipleInstances = 2;
        dynamic action = task.Actions.Create(0); action.Path = Executable; action.Arguments = "--check-reminder";
        action.WorkingDirectory = AppContext.BaseDirectory;
        return task;
    }
    private static void DeleteTask(dynamic service, string name)
    {
        try { service.GetFolder(@"\").DeleteTask(name, 0); }
        catch (COMException ex) when (ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F)) { }
    }
    private static void WithService(Action<dynamic> action)
    {
        var service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Планировщик Windows недоступен."))!;
        try { ((dynamic)service).Connect(); action(service); }
        finally { if (Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service); }
    }
}
