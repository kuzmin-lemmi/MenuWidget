using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MenuWidget.Core;
using MenuWidget.Services;

namespace MenuWidget.Probes;

internal static class DesktopChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        var schedule = new ReminderSettings(true, new(18, 0), TimeSpan.FromMinutes(30));
        var sunday = new DateTimeOffset(2026, 10, 18, 18, 0, 0, TimeSpan.FromHours(3));
        void Assert(bool value) { if (!value) throw new Exception("Unexpected result"); }
        check("reminder/enable-weekday-no-replay", () => Assert(!ReminderRules.IsDue(sunday.AddDays(1), schedule, ReminderRules.Start(sunday.AddDays(1), schedule))));
        check("reminder/sunday-selected-time", () => { var state = ReminderRules.Start(sunday.AddMinutes(-1), schedule); Assert(!ReminderRules.IsDue(sunday.AddSeconds(-1), schedule, state)); Assert(ReminderRules.IsDue(sunday, schedule, state)); });
        check("reminder/enable-after-time", () => Assert(ReminderRules.IsDue(sunday, schedule, ReminderRules.Start(sunday, schedule))));
        var previous = new ReminderState(new(2026, 10, 11), null);
        check("reminder/missed-on-monday", () => Assert(ReminderRules.IsDue(sunday.AddDays(1), schedule, previous)));
        var acknowledged = ReminderRules.Acknowledge(sunday.AddDays(1), schedule, previous);
        check("reminder/no-duplicate", () => Assert(!ReminderRules.IsDue(sunday.AddDays(2), schedule, acknowledged)));
        check("reminder/next-sunday", () => Assert(ReminderRules.IsDue(sunday.AddDays(7), schedule, acknowledged)));
        check("reminder/disabled", () => Assert(!ReminderRules.IsDue(sunday, schedule with { Enabled = false }, previous)));
        check("reminder/snooze-survives-restart", () => { var data = new LocalData(Path.Combine(root, "reminders")); data.SaveReminderState(acknowledged with { SnoozedUntil = sunday.AddMinutes(30) }); Assert(!ReminderRules.IsDue(sunday.AddMinutes(29), schedule, data.ReadReminderState())); Assert(ReminderRules.IsDue(sunday.AddMinutes(30).ToUniversalTime(), schedule, data.ReadReminderState())); });
        check("reminder/clock-back-no-duplicate", () => Assert(!ReminderRules.IsDue(sunday.AddDays(-7), schedule, ReminderRules.Acknowledge(sunday.AddDays(-7), schedule, acknowledged))));
        check("placement/persistence", () => { var data = new LocalData(Path.Combine(root, "placement")); var placement = new WindowPlacement(-500, 25, 570, 900); data.SavePlacement(placement); Assert(data.ReadPlacement() == placement); });
        check("updates/semantic-version", () => Assert(GitHubUpdateService.ParseVersion("v0.10.0") > GitHubUpdateService.ParseVersion("0.2.0")));
        check("updates/reject-prerelease", () => Reject<InvalidDataException>(() => GitHubUpdateService.ParseVersion("v0.3.0-beta.1")));
        foreach (var scenario in new[] { "available", "current", "none", "hash", "size", "source", "architecture", "prerelease", "rate", "cancel" })
            check("updates/" + scenario, () => TestUpdate(root, scenario).GetAwaiter().GetResult());
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task TestUpdate(string root, string scenario)
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var manifest = new UpdateManifest(1, "0.3.0", scenario == "architecture" ? "arm64" : "x64", 22000, "MenuWidget-Setup-x64.msi", 5, scenario == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(payload)));
        const string prefix = "https://github.com/kuzmin-lemmi/MenuWidget/releases/download/v0.3.0/";
        var release = new { draft = false, prerelease = scenario == "prerelease", tag_name = "v0.3.0", body = "Changes", assets = new[] {
            new { name = "update-manifest.json", size = 200L, browser_download_url = (scenario == "source" ? "https://example.com/" : prefix) + "update-manifest.json" },
            new { name = manifest.AssetName, size = scenario == "size" ? 6L : 5L, browser_download_url = prefix + manifest.AssetName } } };
        using var client = new HttpClient(new Handler(uri => uri.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
            ? scenario == "none" ? new(HttpStatusCode.NotFound) : scenario == "rate" ? new(HttpStatusCode.Forbidden) : Json(release)
            : uri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal) ? Json(manifest) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var data = new LocalData(Path.Combine(root, "update-" + scenario));
        var service = new GitHubUpdateService(data, scenario == "current" ? new(0, 3, 0) : new(0, 2, 0), client);
        if (scenario is "source" or "size" or "architecture" or "prerelease") { Reject<InvalidDataException>(() => service.CheckAsync(default).GetAwaiter().GetResult()); return; }
        if (scenario == "rate") { Reject<HttpRequestException>(() => service.CheckAsync(default).GetAwaiter().GetResult()); return; }
        var result = await service.CheckAsync(default);
        var expected = scenario == "none" ? UpdateCheckStatus.NoReleases : scenario == "current" ? UpdateCheckStatus.UpToDate : UpdateCheckStatus.Available;
        if (result.Status != expected) throw new Exception("Unexpected status");
        if (result.Candidate is null) return;
        var progress = new Progress<DownloadProgress>();
        if (scenario == "hash") { Reject<InvalidDataException>(() => service.DownloadVerifiedAsync(result.Candidate, progress, default).GetAwaiter().GetResult()); if (Directory.GetFiles(Path.Combine(data.Root, "updates")).Length != 0) throw new Exception("Partial file left"); return; }
        if (scenario == "cancel") { using var cts = new CancellationTokenSource(); cts.Cancel(); Reject<OperationCanceledException>(() => service.DownloadVerifiedAsync(result.Candidate, progress, cts.Token).GetAwaiter().GetResult()); return; }
        var path = await service.DownloadVerifiedAsync(result.Candidate, progress, default);
        if (!File.ReadAllBytes(path).SequenceEqual(payload)) throw new Exception("Wrong download");
        Reject<InvalidOperationException>(() => service.LaunchInstallerAsync(path + ".unverified", default).GetAwaiter().GetResult());
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<Uri, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(response(request.RequestUri!)); }
    }
}
