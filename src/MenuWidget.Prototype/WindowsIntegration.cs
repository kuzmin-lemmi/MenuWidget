using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MenuWidget.Core;
using Microsoft.Win32;

namespace MenuWidget.Prototype;

internal sealed class WindowsIntegration(string? userSid = null) : IReminderScheduler
{
    private static string CurrentSid => WindowsIdentity.GetCurrent().User!.Value;
    private readonly string ownerSid = userSid is null ? CurrentSid : new SecurityIdentifier(userSid).Value;
    internal static string InstanceName => @"Local\MenuWidget." + CurrentSid;
    private string StartupName => "MenuWidget.Startup." + ownerSid;
    private string WeeklyName => "MenuWidget.Reminder." + ownerSid;
    private string SnoozeName => "MenuWidget.Snooze." + ownerSid;
    private static string Executable => Environment.ProcessPath ?? throw new IOException("Не найден путь программы.");
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void SetAutoStart(bool enabled)
    {
        // Migrate the old Run entry; all current integration now lives in Task Scheduler.
        using (var key = Registry.Users.CreateSubKey(ownerSid + @"\" + RunPath))
        { key.DeleteValue("MenuWidget", throwOnMissingValue: false); key.Flush(); }
        WithService(service =>
        {
            if (!enabled) { DeleteTask(service, StartupName); return; }
            dynamic task = Definition(service);
            dynamic logon = task.Triggers.Create(9); logon.UserId = ownerSid;
            task.Actions.Item(1).Arguments = "--background";
            service.GetFolder(@"\").RegisterTaskDefinition(StartupName, task, 6, ownerSid, null, 3);
        });
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
            dynamic logon = task.Triggers.Create(9); logon.UserId = ownerSid;
            service.GetFolder(@"\").RegisterTaskDefinition(WeeklyName, task, 6, ownerSid, null, 3);
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
            service.GetFolder(@"\").RegisterTaskDefinition(SnoozeName, task, 6, ownerSid, null, 3);
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

    private dynamic Definition(dynamic service)
    {
        dynamic task = service.NewTask(0);
        task.RegistrationInfo.Description = "MenuWidget: напоминание о подготовке питания";
        task.Principal.UserId = ownerSid; task.Principal.LogonType = 3; task.Principal.RunLevel = 0;
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
        // COM interop maps ERROR_FILE_NOT_FOUND to FileNotFoundException on .NET.
        catch (Exception ex) when (ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x8004130F)) { }
    }
    private static void WithService(Action<dynamic> action)
    {
        var service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Планировщик Windows недоступен."))!;
        try { ((dynamic)service).Connect(); action(service); }
        finally { if (Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service); }
    }
}
