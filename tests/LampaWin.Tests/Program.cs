using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LampaWin.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var passed = new List<string>();
void Assert(bool value, string name)
{
    if (!value) throw new InvalidOperationException("FAILED: " + name);
    passed.Add(name); Console.WriteLine("PASS " + name);
}
Assert(FoodServices.All.Count == 2 && FoodServices.All.All(x => FoodServices.IsSafeNavigation(x.HomeUri.AbsoluteUri)), "food chooser uses two official HTTPS sites");
Assert(new[] { "https://passport.yandex.ru/", "https://payments.example/3ds" }.All(FoodServices.IsSafeNavigation), "food auth and payment HTTPS redirects allowed");
Assert(new[] { "http://eda.yandex.ru", "file:///C:/secret", "javascript:alert(1)", "https://localhost/", "https://192.168.1.8/", "https://[::1]/", "https://user:pass@eda.yandex.ru/" }.All(x => !FoodServices.IsSafeNavigation(x)), "food navigation blocks insecure/local/credential URLs");
Assert(FoodServices.All.Select(x => FoodServices.ProfileDirectory(Path.GetTempPath(), x)).Distinct().Count() == 2, "food services have separate persistent browser profiles");
var titlePolicyCases = JsonNode.Parse("""
    [
      {"Title":"Blue Planet 2017 1080p","Seeders":20,"Category":[5000]},
      {"Title":"Blue Valentine 2010","Seeders":400,"Category":[2000]},
      {"Title":"Blue Planet 2001","Seeders":200,"Category":[5000]},
      {"Title":"Blue Planet Steam-Rip","Seeders":900,"Category":[8000]}
    ]
    """)!.AsArray();
TorrentResultPolicy.FilterAndRank(titlePolicyCases, "Blue Planet 2017");
Assert(titlePolicyCases.Count == 1 && titlePolicyCases[0]!["Title"]!.ToString() == "Blue Planet 2017 1080p",
    "torrent relevance requires a matching multiword title and rejects conflicting release years and normalized game markers");
var numericCases = JsonNode.Parse("""[{"Title":"1917 (2019)","Seeders":5},{"Title":"Other movie (2019)","Seeders":50}]""")!.AsArray();
TorrentResultPolicy.FilterAndRank(numericCases, "1917");
Assert(numericCases.Count == 1 && numericCases[0]!["Title"]!.ToString().StartsWith("1917"),
    "numeric movie title remains a required identity token");
var seasonCases = JsonNode.Parse("""[{"Title":"Blue Planet S01","Seeders":80},{"Title":"Blue Planet Сезоны 1-3","Seeders":10},{"Title":"Blue Planet S02","Seeders":5}]""")!.AsArray();
TorrentResultPolicy.FilterAndRank(seasonCases, "Blue Planet S02");
Assert(seasonCases.Count == 2 && !seasonCases.Any(item => item!["Title"]!.ToString() == "Blue Planet S01"),
    "explicit season search rejects other seasons and keeps inclusive season packs");
var episodeCases = JsonNode.Parse("""[{"Title":"Blue Planet S02E01","Seeders":80},{"Title":"Blue Planet S02 Серии 1-10","Seeders":10},{"Title":"Blue Planet S02E03","Seeders":5}]""")!.AsArray();
TorrentResultPolicy.FilterAndRank(episodeCases, "Blue Planet S02E03");
Assert(episodeCases.Count == 2 && !episodeCases.Any(item => item!["Title"]!.ToString() == "Blue Planet S02E01"),
    "explicit episode search rejects other episodes and keeps inclusive episode packs");
var instituteCases = JsonNode.Parse("""
    [
      {"Title":"Институт / The Institute [S01] (2025) WEB-DL-AVC","Seeders":12},
      {"Title":"The Misfit of Demon King Academy S02E15 Institute of the Gods 1080p","Seeders":18},
      {"Title":"[ReleaseGroup] The Institute S01 WEBRip","Seeders":10}
    ]
    """)!.AsArray();
TorrentResultPolicy.FilterAndRank(instituteCases, "The Institute");
Assert(instituteCases.Count == 2 && !instituteCases.Any(item => item!["Title"]!.ToString().Contains("Misfit")),
    "The Institute rejects the exact unrelated anime from the user screenshot while retaining aliases and group tags");
var remakeCases = JsonNode.Parse("""[{"Title":"The Institute (2017)","Seeders":20},{"Title":"The Institute S01 (2025)","Seeders":10},{"Title":"The Institute S02 (2026)","Seeders":5}]""")!.AsArray();
TorrentResultPolicy.FilterAndRank(remakeCases, "The Institute", 2025, isSeries: true);
Assert(remakeCases.Count == 2 && !remakeCases.Any(item => item!["Title"]!.ToString().Contains("2017")),
    "selected series premiere year rejects old same-name films while retaining later seasons");
var temporary = Path.Combine(Path.GetTempPath(), "LampaWin-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temporary, "components", "lampa"));
var paths = new AppPaths(temporary, Path.Combine(temporary, "data"));
paths.EnsureDirectories();
await File.WriteAllTextAsync(Path.Combine(paths.LampaRoot, "index.html"), "<html><head></head><body>Lampa fixture</body></html>");
await File.WriteAllTextAsync(Path.Combine(paths.LampaRoot, "test.js"), "window.fixture=true;");
const string apiKey = "test-only-api-key-123456789";
const string password = "test-only-password";
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
await using var backend = builder.Build();
string backendBase = "";
var sawTorrAuth = false;
var sawInjectedKey = false;
backend.MapGet("/echo", (HttpContext context) =>
{
    sawTorrAuth = context.Request.Headers.Authorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:" + password));
    return Results.Text("MatriX.fixture");
});
backend.MapGet("/stream", async context =>
{
    var auth = context.Request.Headers.Authorization.ToString();
    if (auth != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:" + password))) { context.Response.StatusCode = 401; return; }
    context.Response.StatusCode = context.Request.Headers.Range.ToString() == "bytes=2-5" ? 206 : 200;
    context.Response.Headers["Accept-Ranges"] = "bytes";
    context.Response.Headers["Content-Range"] = "bytes 2-5/10";
    context.Response.Headers["Access-Control-Allow-Origin"] = "*";
    context.Response.Headers["Set-Cookie"] = "backend=leak";
    context.Response.ContentType = "video/mp4";
    await context.Response.WriteAsync("2345");
});
backend.MapGet("/api/v2.0/indexers/all/results", async context =>
{
    if (context.Request.Query.ContainsKey("oversized"))
    {
        context.Response.ContentType = "application/json";
        var chunk = new string('x', 131072);
        for (var i = 0; i < 136 && !context.RequestAborted.IsCancellationRequested; i++) await context.Response.WriteAsync(chunk, context.RequestAborted);
        return;
    }
    sawInjectedKey = context.Request.Query["apikey"] == apiKey && context.Request.Query["Query"] == "fixture";
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(JsonSerializer.Serialize(new { Results = new object[]
    {
        new { Title = "fixture weak", Seeders = 0, Peers = 40, Category = new[] { 2000 }, Link = backendBase + "dl/fixture/?apikey=" + apiKey, MagnetUri = "magnet:?xt=urn:btih:0123456789012345678901234567890123456789" },
        new { Title = "fixture PC RePack by FitGirl", Seeders = 900, Peers = 20, Category = new[] { 4000 }, Link = backendBase + "dl/fixture/?apikey=" + apiKey, MagnetUri = "magnet:?xt=urn:btih:1123456789012345678901234567890123456789" },
        new { Title = "unrelated popular movie", Seeders = 500, Peers = 10, Category = new[] { 2000 }, Link = backendBase + "dl/fixture/?apikey=" + apiKey, MagnetUri = "magnet:?xt=urn:btih:2123456789012345678901234567890123456789" },
        new { Title = "fixture healthy", Seeders = 25, Peers = 3, Category = new[] { 2000 }, Link = backendBase + "dl/fixture/?apikey=" + apiKey, MagnetUri = "magnet:?xt=urn:btih:3123456789012345678901234567890123456789" }
    } }));
});
const string torrentFixture = "d4:infod4:name7:fixtureee";
backend.MapGet("/dl/fixture/", () => Results.Bytes(Encoding.UTF8.GetBytes(torrentFixture), "application/x-bittorrent"));
await backend.StartAsync();
backendBase = backend.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/') + '/';
var runtime = new RuntimeSnapshot(new Uri(backendBase), new Uri(backendBase), apiKey, "fixture", password, ["fixture"], []);
await using var gateway = new LocalGateway(paths);
try
{
    await gateway.StartAsync();
    using var anonymous = new HttpClient();
    Assert((await anonymous.GetAsync(gateway.Origin)).StatusCode == HttpStatusCode.Unauthorized, "anonymous catalog rejected");
    Assert((await anonymous.GetAsync(new Uri(gateway.Origin, "bootstrap?token=wrong"))).StatusCode == HttpStatusCode.Forbidden, "wrong bootstrap rejected");
    using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
    using var client = new HttpClient(handler);
    var login = await client.GetAsync(gateway.LoginUri);
    Assert(login.StatusCode == HttpStatusCode.Redirect, "bootstrap authenticates and redirects");
    Assert(login.Headers.GetValues("Set-Cookie").Single().Contains("httponly", StringComparison.OrdinalIgnoreCase), "session cookie HttpOnly");
    Assert((await client.GetAsync(gateway.LoginUri)).StatusCode == HttpStatusCode.Forbidden, "bootstrap token single use");
    Assert((await client.GetAsync(gateway.RenewLoginUri())).StatusCode == HttpStatusCode.Redirect, "recreated browser can authenticate with a fresh one-use bootstrap");
    var html = await client.GetStringAsync(gateway.Origin);
    Assert(html.Contains("lampawin-bridge.js"), "catalog opens before local services are ready");
    Assert((await client.GetAsync(new Uri(gateway.Origin, "torrserver/echo"))).StatusCode == HttpStatusCode.ServiceUnavailable,
        "pending local services return a retryable response");
    gateway.UpdateRuntime(runtime);
    Assert(html.Contains("/lampawin-bridge.js"), "Lampa integration injected");
    Assert((await client.GetStringAsync(new Uri(gateway.Origin, "test.js"))).Contains("fixture=true"), "bundled assets served");
    var config = await client.GetStringAsync(new Uri(gateway.Origin, "lampawin-config.json"));
    Assert(!config.Contains(apiKey) && !config.Contains(password) && config.Contains("managed"), "browser receives no backend credentials");
    await client.GetAsync(new Uri(gateway.Origin, "torrserver/echo"));
    Assert(sawTorrAuth, "TorrServer credentials injected server side");
    var search = await client.GetStringAsync(new Uri(gateway.Origin, "jackett/api/v2.0/indexers/all/results?Query=fixture&apikey=attacker"));
    Assert(sawInjectedKey, "Jackett key cannot be overridden by browser");
    Assert(!search.Contains(apiKey) && search.Contains("/download/"), "download URLs rewritten without API keys");
    using var results = JsonDocument.Parse(search);
    Assert(results.RootElement.GetProperty("Results").GetArrayLength() == 1
        && results.RootElement.GetProperty("Results")[0].GetProperty("Title").GetString() == "fixture healthy",
        "torrent results reject games, unrelated titles and zero-seed choices when a healthy swarm exists");
    var link = results.RootElement.GetProperty("Results")[0].GetProperty("Link").GetString();
    Assert(await anonymous.GetStringAsync(link) == torrentFixture, "TorrServer can download torrent without browser cookies");
    Assert((await anonymous.GetAsync(new Uri(gateway.Origin, "download/fake"))).StatusCode == HttpStatusCode.Forbidden, "fake download capability rejected");
    Assert((await client.GetAsync(new Uri(gateway.Origin, "jackett/UI/Dashboard"))).StatusCode == HttpStatusCode.NotFound, "Jackett admin UI not exposed by gateway");
    using var crossSite = new HttpRequestMessage(HttpMethod.Get, new Uri(gateway.Origin, "torrserver/echo"));
    crossSite.Headers.Add("Origin", "https://attacker.invalid");
    Assert((await client.SendAsync(crossSite)).StatusCode == HttpStatusCode.Forbidden, "cross-origin requests rejected");
    using var rebinding = new HttpRequestMessage(HttpMethod.Get, gateway.Origin); rebinding.Headers.Host = "attacker.invalid";
    Assert((await client.SendAsync(rebinding)).StatusCode == HttpStatusCode.Forbidden, "DNS rebinding Host rejected");
    Assert(!gateway.TryCreateMediaUri("file:///C:/Windows/win.ini", out _), "file URI bridge playback rejected");
    Assert(!gateway.TryCreateMediaUri("http://127.0.0.1:1/torrserver/stream?link=x", out _), "foreign service bridge playback rejected");
    Assert(!gateway.TryCreateMediaUri(new Uri(gateway.Origin, "torrserver/settings").AbsoluteUri, out _), "media capability limited to stream");
    Assert(gateway.TryCreateMediaUri(new Uri(gateway.Origin, "torrserver/stream?link=fixture&index=1&play").AbsoluteUri, out var media), "local torrent media admitted");
    Assert(gateway.TryCreateMediaUri(new Uri(gateway.Origin, "torrserver/stream/Русский%20фильм.mkv?link=fixture&index=1&play").AbsoluteUri, out _), "named torrent stream path admitted");
    using var range = new HttpRequestMessage(HttpMethod.Get, media); range.Headers.Range = new RangeHeaderValue(2, 5);
    var partial = await anonymous.SendAsync(range);
    Assert(partial.StatusCode == HttpStatusCode.PartialContent && await partial.Content.ReadAsStringAsync() == "2345", "VLC stream preserves HTTP Range and bytes");
    Assert(!partial.Headers.Contains("Set-Cookie") && !partial.Headers.Contains("Access-Control-Allow-Origin"), "unsafe backend response headers stripped");
    Assert((await anonymous.PostAsync(media, new StringContent("bad"))).StatusCode == HttpStatusCode.MethodNotAllowed, "media capability cannot mutate backend");
    Assert(!PublicNetworkPolicy.IsPublic(IPAddress.Parse("127.0.0.1")) && !PublicNetworkPolicy.IsPublic(IPAddress.Parse("192.168.1.1")) && !PublicNetworkPolicy.IsPublic(IPAddress.Parse("::ffff:10.0.0.1")), "torrent download redirects cannot target private IPv4 networks");
    Assert(!PublicNetworkPolicy.IsPublic(IPAddress.Parse("fd00::1")) && !PublicNetworkPolicy.IsPublic(IPAddress.Parse("fe80::1")) && PublicNetworkPolicy.IsPublic(IPAddress.Parse("2606:4700:4700::1111")), "torrent download IPv6 policy excludes local networks");
    var profile = new ProfileStore(paths);
    await profile.SaveAsync(new() { ["history"] = "fixture-watch-position-42" });
    Assert(profile.Load()["history"] == "fixture-watch-position-42", "profile roundtrip survives origin changes");
    var stored = await File.ReadAllBytesAsync(Path.Combine(paths.DataRoot, "lampa-profile.bin"));
    Assert(!Encoding.UTF8.GetString(stored).Contains("fixture-watch-position"), "profile encrypted with user DPAPI");
    await profile.SaveAsync(new() { ["history"] = "new-position" });
    await File.WriteAllBytesAsync(Path.Combine(paths.DataRoot, "lampa-profile.bin"), [1, 2, 3]);
    Assert(profile.Load().GetValueOrDefault("history") == "fixture-watch-position-42" && profile.RecoveryMessage is not null
        && File.Exists(Path.Combine(paths.DataRoot, "lampa-profile.bin.recovery")), "damaged profile restores last good backup and reports recovery");
    await profile.SaveAsync(new() { ["history"] = "after-recovery" });
    await File.WriteAllBytesAsync(Path.Combine(paths.DataRoot, "lampa-profile.bin"), [4, 5, 6]);
    Assert(profile.Load().GetValueOrDefault("history") == "fixture-watch-position-42"
        && (await File.ReadAllBytesAsync(Path.Combine(paths.DataRoot, "lampa-profile.bin.recovery"))).SequenceEqual(new byte[] { 1, 2, 3 }),
        "repeated corruption preserves previous recovery evidence and the good backup");
    var preferences = new DesktopPreferences { Volume = 37, AudioLanguage = "eng", PreviewEnabled = false, ShowAllSearchResults = true };
    preferences.Save(paths.SettingsFile);
    var loadedPreferences = DesktopPreferences.Load(paths.SettingsFile);
    Assert(loadedPreferences.Volume == 37 && loadedPreferences.AudioLanguage == "eng" && !loadedPreferences.PreviewEnabled && loadedPreferences.ShowAllSearchResults, "native player preferences survive a new store instance");
    gateway.ShowAllSearchResults = true;
    using (var relaxed = JsonDocument.Parse(await client.GetStringAsync(new Uri(gateway.Origin, "jackett/api/v2.0/indexers/all/results?Query=fixture"))))
        Assert(relaxed.RootElement.GetProperty("Results").GetArrayLength() == 3, "show hidden results restores uncertain titles and zero-seed results while still excluding games");
    Assert((await client.GetAsync(new Uri(gateway.Origin, "jackett/api/v2.0/indexers/all/results?oversized=true"))).StatusCode == HttpStatusCode.BadGateway,
        "oversized chunked search response is rejected while streaming");
    var streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var streamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    // A dedicated origin models a torrent response that never finishes on its own.
    var slowBuilder = WebApplication.CreateSlimBuilder(); slowBuilder.Logging.ClearProviders(); slowBuilder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var slowBackend = slowBuilder.Build();
    slowBackend.Run(async context =>
    {
        await context.Response.WriteAsync("stream", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        streamStarted.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
        catch (OperationCanceledException) { streamCancelled.TrySetResult(); }
    });
    await slowBackend.StartAsync();
    var slowOrigin = new Uri(slowBackend.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    await using var streamingGateway = new LocalGateway(paths, runtime with { TorrServerBaseUri = slowOrigin });
    await streamingGateway.StartAsync();
    streamingGateway.TryCreateMediaUri(new Uri(streamingGateway.Origin, "torrserver/stream").AbsoluteUri, out var streamingUri);
    using var streamingClient = new HttpClient();
    using var streamingResponse = await streamingClient.GetAsync(streamingUri, HttpCompletionOption.ResponseHeadersRead);
    await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var shutdownTimer = System.Diagnostics.Stopwatch.StartNew();
    await streamingGateway.DisposeAsync();
    await streamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(shutdownTimer.Elapsed < TimeSpan.FromSeconds(3), "gateway cancels active streaming and closes without the default drain wait");
    await backend.StopAsync();
    var unavailable = await client.GetAsync(new Uri(gateway.Origin, "torrserver/echo"));
    Assert(unavailable.StatusCode == HttpStatusCode.BadGateway, "backend outage yields controlled error");
    Console.WriteLine($"{passed.Count} checks passed.");
}
finally
{
    // Only remove this exact, freshly-created test directory.
    var resolved = Path.GetFullPath(temporary);
    if (Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(resolved).StartsWith("LampaWin-tests-", StringComparison.Ordinal))
        Directory.Delete(resolved, true);
}
