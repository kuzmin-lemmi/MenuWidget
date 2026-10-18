using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

if (args.Length != 5) return 2;
var fallback = args[4];
try
{
    var msi = Path.GetFullPath(args[0]); var hash = args[1];
    if (!msi.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Неподдерживаемый установщик.");
    using (var file = File.OpenRead(msi))
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Установщик изменился после проверки.");
    try
    {
        using var previous = Process.GetProcessById(int.Parse(args[2]));
        if (previous.StartTime.ToUniversalTime().Ticks == long.Parse(args[3]) && !previous.WaitForExit(90000)) throw new IOException("Программа не закрылась. Обновление отменено.");
    }
    catch (ArgumentException) { }
    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe")) { UseShellExecute = true };
    foreach (var value in new[] { "/i", msi, "/passive", "/norestart", "REBOOT=ReallySuppress" }) start.ArgumentList.Add(value);
    using var installer = Process.Start(start) ?? throw new IOException("Не удалось открыть установщик Windows.");
    installer.WaitForExit();
    var code = installer.ExitCode;
    if (code is not (0 or 3010)) Native.MessageBoxW(0, code == 1602 ? "Установка отменена. Предыдущая версия сохранена." : "Обновление не установлено. Будет открыта предыдущая версия.", "MenuWidget", 0x40);
    var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MenuWidget", "MenuWidget.exe");
    var launch = File.Exists(installed) ? installed : fallback;
    if (File.Exists(launch)) Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true });
    return code is 0 or 3010 ? 0 : code;
}
catch (Exception ex)
{
    Native.MessageBoxW(0, "Не удалось установить обновление: " + ex.Message, "MenuWidget", 0x10);
    if (File.Exists(fallback)) Process.Start(new ProcessStartInfo(fallback) { UseShellExecute = true });
    return 1;
}

internal static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBoxW(nint owner, string text, string caption, uint flags);
}
