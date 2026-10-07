using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using LampaWin.Core;
using LampaWin.Core.Runtime;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
var fixturePeerHost = IPAddress.Parse("192.168.1.17");
var fixtureProcess = new Process
{
    StartInfo = new ProcessStartInfo("python", $"\"{Path.Combine(root, "tests", "libtorrent_fixture.py")}\" --peer-host {fixturePeerHost}")
    {
        WorkingDirectory = root,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    }
};
fixtureProcess.StartInfo.Environment["PYTHONPATH"] = Path.Combine(root, ".tools", "test-python-site");
fixtureProcess.Start();
using var fixtureCancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var manifestLine = await fixtureProcess.StandardOutput.ReadLineAsync(fixtureCancel.Token)
    ?? throw new InvalidOperationException("Local MSE fixture exited before publishing its manifest.");
using var fixtureManifest = JsonDocument.Parse(manifestLine);
var fixtureTorrent = new Uri(fixtureManifest.RootElement.GetProperty("torrentUrl").GetString()!);
var fixturePeerPort = fixtureManifest.RootElement.GetProperty("peerPort").GetInt32();
if (fixtureManifest.RootElement.GetProperty("seed").GetString() != "libtorrent-mse"
    || !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == fixtureTorrent.Port && IPAddress.IsLoopback(x.Address))
    || !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == fixturePeerPort && x.Address.Equals(fixturePeerHost)))
    throw new InvalidOperationException("Isolated local tracker/MSE peer listeners are missing.");
var data = Path.Combine(root, ".cache", "runtime-stream-smoke", Guid.NewGuid().ToString("N"));
await using var runtime = new LocalRuntime(new AppPaths(root, data));
try
{
    var snapshot = await runtime.StartAsync();
    Console.WriteLine("Local runtime ready; probing TorrServer using isolated profile.");
    using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{snapshot.TorrServerUser}:{snapshot.TorrServerPassword}")));
    using (var settingsRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(snapshot.TorrServerBaseUri, "settings"))
    {
        Content = new StringContent("{\"action\":\"get\"}", Encoding.UTF8, "application/json")
    })
    {
        settingsRequest.Headers.Authorization = auth;
        using var settingsResponse = await client.SendAsync(settingsRequest);
        settingsResponse.EnsureSuccessStatusCode();
        using var settings = System.Text.Json.JsonDocument.Parse(await settingsResponse.Content.ReadAsStringAsync());
        if (!settings.RootElement.TryGetProperty("EnableBonjour", out var bonjour) || bonjour.ValueKind != System.Text.Json.JsonValueKind.False)
            throw new InvalidOperationException("Fresh local runtime must disable Bonjour LAN advertisement.");
        Console.WriteLine("Fresh TorrServer profile has LAN Bonjour advertisement disabled.");
    }
    var link = Uri.EscapeDataString(fixtureTorrent.AbsoluteUri);
    foreach (var suffix in new[] { "&index=1&play" })
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(snapshot.TorrServerBaseUri, $"stream?link={link}{suffix}"));
        request.Headers.Authorization = auth;
        request.Headers.Range = new RangeHeaderValue(0, 65535);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            Console.WriteLine($"Probe {suffix}: HTTP {(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType ?? "no content-type"}; range={response.Content.Headers.ContentRange?.ToString() ?? "none"}.");
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
            {
                var buffer = new byte[65536];
                await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
                var read = await stream.ReadAsync(buffer.AsMemory(), cancel.Token);
                Console.WriteLine($"Read {read} stream bytes.");
                if (read > 0) return;
            }
            var brief = await response.Content.ReadAsStringAsync(cancel.Token);
            Console.WriteLine($"Response body length: {brief.Length}; error body content withheld.");
        }
        catch (OperationCanceledException) { Console.WriteLine($"Probe {suffix} timed out; no response body recorded."); }
        catch (HttpRequestException ex) { Console.WriteLine($"Probe {suffix} failed: {ex.GetType().Name}."); }
        await PrintFixtureStatsAsync(client, fixtureTorrent);
    }
    throw new InvalidOperationException("No tested TorrServer stream request returned bytes.");
}
finally
{
    await runtime.StopAsync();
    if (!fixtureProcess.HasExited)
    {
        fixtureProcess.Kill(entireProcessTree: true);
        await fixtureProcess.WaitForExitAsync();
    }
}

static async Task PrintFixtureStatsAsync(HttpClient client, Uri torrentUri)
{
    try
    {
        using var response = await client.GetAsync(new Uri(torrentUri, "/stats"));
        if (!response.IsSuccessStatusCode) return;
        using var stats = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Console.WriteLine($"Fixture counters: announces={stats.RootElement.GetProperty("announces").GetInt32()}, "
            + $"valid-announces={stats.RootElement.GetProperty("validAnnounces").GetInt32()}, "
            + $"peer-connections={stats.RootElement.GetProperty("peerConnections").GetInt32()}, "
            + $"handshake-attempts={stats.RootElement.GetProperty("handshakeAttempts").GetInt32()}, "
            + $"unsupported-headers={stats.RootElement.GetProperty("unsupportedHandshakeHeaders").GetInt32()}, "
            + $"peer-handshakes={stats.RootElement.GetProperty("peerHandshakes").GetInt32()}, "
            + $"piece-requests={stats.RootElement.GetProperty("pieceRequests").GetInt32()}, "
            + $"registered-peers={stats.RootElement.GetProperty("registeredPeers").GetInt32()}.");
    }
    catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { }
}
