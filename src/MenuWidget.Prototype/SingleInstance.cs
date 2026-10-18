namespace MenuWidget.Prototype;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly List<EventWaitHandle> events = [];
    private readonly List<RegisteredWaitHandle> waits = [];
    public bool IsPrimary { get; }

    public SingleInstance()
    {
        mutex = new Mutex(true, WindowsIntegration.InstanceName, out var created);
        IsPrimary = created;
    }

    public void Listen(string command, Action action)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, WindowsIntegration.InstanceName + "." + command);
        events.Add(handle);
        waits.Add(ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => action(), null, Timeout.Infinite, false));
    }
    public static async Task<bool> SendAsync(string command)
    {
        for (var i = 0; i < 12; i++)
        {
            try { using var handle = EventWaitHandle.OpenExisting(WindowsIntegration.InstanceName + "." + command); handle.Set(); return true; }
            catch (WaitHandleCannotBeOpenedException) { await Task.Delay(100); }
        }
        return false;
    }
    public void Dispose()
    {
        foreach (var wait in waits) wait.Unregister(null);
        foreach (var handle in events) handle.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
