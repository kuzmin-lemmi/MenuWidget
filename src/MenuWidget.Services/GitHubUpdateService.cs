using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MenuWidget.Core;

namespace MenuWidget.Services;

public record UpdateManifest(int SchemaVersion, string Version, string Architecture, int MinimumWindowsBuild, string AssetName, long Size, string Sha256);

public sealed class GitHubUpdateService : IUpdateService
{
    private const string Repository = "kuzmin-lemmi/MenuWidget";
    private const long MaxInstallerSize = 350 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly LocalData data;
    private readonly Version current;
    private readonly Dictionary<string, (UpdateManifest Manifest, Uri Uri)> validated = new();
    private readonly Dictionary<string, string> verifiedFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public GitHubUpdateService(LocalData data, Version current, HttpClient? http = null)
    {
        this.data = data; this.current = new(current.Major, current.Minor, Math.Max(0, current.Build));
        this.http = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    }

    public static Version ParseVersion(string value)
    {
        var match = Regex.Match(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch))
            throw new InvalidDataException("В релизе указан неподдерживаемый номер версии.");
        return new(major, minor, patch);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        validated.Clear();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await GetAsync(new($"https://api.github.com/repos/{Repository}/releases/latest"), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateCheckStatus.NoReleases, null);
        RequireSuccess(response);
        var release = await ReadSmallJson(response, timeout.Token);
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Этот релиз не предназначен для обычного обновления.");
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version <= current) return new(UpdateCheckStatus.UpToDate, null);
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var descriptor = assets.SingleOrDefault(a => a.GetProperty("name").GetString() == "update-manifest.json");
        if (descriptor.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("В новой версии отсутствует манифест обновления. Установка недоступна.");
        var manifestUri = new Uri(descriptor.GetProperty("browser_download_url").GetString()!);
        RequireReleaseAsset(manifestUri, tag);
        using var manifestResponse = await GetAsync(manifestUri, timeout.Token); RequireSuccess(manifestResponse);
        var manifestJson = await ReadSmallJson(manifestResponse, timeout.Token);
        var manifest = manifestJson.Deserialize<UpdateManifest>(Json) ?? throw new InvalidDataException("Манифест обновления повреждён.");
        ValidateManifest(manifest, version);
        var asset = assets.SingleOrDefault(a => a.GetProperty("name").GetString() == manifest.AssetName);
        if (asset.ValueKind == JsonValueKind.Undefined || asset.GetProperty("size").GetInt64() != manifest.Size) throw new InvalidDataException("Установщик не найден или его размер не совпадает с манифестом.");
        var installerUri = new Uri(asset.GetProperty("browser_download_url").GetString()!); RequireReleaseAsset(installerUri, tag);
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var candidate = new UpdateCandidate(version.ToString(), notes, manifestUri);
        validated.Clear(); validated.Add(candidate.Version, (manifest, installerUri));
        return new(UpdateCheckStatus.Available, candidate);
    }

    public async Task<string> DownloadVerifiedAsync(UpdateCandidate candidate, IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        if (!validated.TryGetValue(candidate.Version, out var update)) throw new InvalidOperationException("Сначала выполните проверку обновлений.");
        var directory = Path.Combine(data.Root, "updates"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"MenuWidget-{candidate.Version}-{Guid.NewGuid():N}.msi");
        try
        {
            using var response = await GetAsync(update.Uri, cancellationToken); RequireSuccess(response);
            if (response.Content.Headers.ContentLength is { } length && length != update.Manifest.Size) throw new InvalidDataException("Размер загрузки не соответствует релизу.");
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                var buffer = new byte[81920]; long received = 0; int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    received += count;
                    if (received > update.Manifest.Size || received > MaxInstallerSize) throw new InvalidDataException("Загружаемый файл больше ожидаемого.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken); progress.Report(new(received, update.Manifest.Size));
                }
                if (received != update.Manifest.Size) throw new InvalidDataException("Загрузка установщика прервалась.");
            }
            string hash;
            await using (var input = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
            if (!hash.Equals(update.Manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Проверка SHA-256 не пройдена. Установщик не будет запущен.");
            cancellationToken.ThrowIfCancellationRequested();
            verifiedFiles.Add(path, hash);
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    public Task LaunchInstallerAsync(string verifiedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!verifiedFiles.TryGetValue(verifiedPath, out var expected)) throw new InvalidOperationException("Установщик не был проверен.");
        using (var input = File.OpenRead(verifiedPath))
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Установщик изменился после загрузки.");
        var source = Path.Combine(AppContext.BaseDirectory, "MenuWidget.Updater.exe");
        if (!File.Exists(source)) throw new FileNotFoundException("Не найден компонент установки обновлений.");
        var runner = Path.Combine(data.Root, "updates", "runner-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(source, runner);
        var start = new ProcessStartInfo(runner) { UseShellExecute = true };
        foreach (var value in new[] { verifiedPath, expected, Environment.ProcessId.ToString(), Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(), Environment.ProcessPath ?? "" }) start.ArgumentList.Add(value);
        _ = Process.Start(start) ?? throw new IOException("Не удалось запустить установку обновления.");
        return Task.CompletedTask;
    }

    private static void ValidateManifest(UpdateManifest manifest, Version expected)
    {
        if (manifest.SchemaVersion != 1 || ParseVersion(manifest.Version) != expected || manifest.Architecture != "x64" || !manifest.AssetName.Equals("MenuWidget-Setup-x64.msi", StringComparison.Ordinal)
            || manifest.Size is <= 0 or > MaxInstallerSize || !Regex.IsMatch(manifest.Sha256 ?? "", "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Манифест обновления несовместим с программой.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || (OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build < manifest.MinimumWindowsBuild))
            throw new InvalidDataException("Новая версия не поддерживает этот компьютер.");
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (!AllowedHost(uri)) throw new InvalidDataException("Недопустимый адрес загрузки обновления.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MenuWidget/" + current); request.Headers.Accept.ParseAdd("application/vnd.github+json");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new InvalidDataException("Некорректное перенаправление загрузки.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            return response;
        }
        throw new InvalidDataException("Слишком много перенаправлений загрузки.");
    }
    private static bool AllowedHost(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host is "api.github.com" or "github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com";
    private static void RequireReleaseAsset(Uri uri, string tag)
    {
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/", StringComparison.Ordinal))
            throw new InvalidDataException("Файл обновления принадлежит другому источнику.");
    }
    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new HttpRequestException("GitHub временно ограничил запросы. Повторите проверку позже.");
        response.EnsureSuccessStatusCode();
    }
    private static async Task<JsonElement> ReadSmallJson(HttpResponseMessage response, CancellationToken token)
    {
        const int limit = 512 * 1024;
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0) { if (output.Length + count > limit) throw new InvalidDataException("Ответ обновления слишком большой."); output.Write(buffer, 0, count); }
        using var document = JsonDocument.Parse(output.ToArray()); return document.RootElement.Clone();
    }
}
