using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using LampaWin.Core;
using LampaWin.Core.Runtime;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
var fixturePeerHost = NetworkInterface.GetAllNetworkInterfaces()
    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
        && adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
        && adapter.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
    .Select(address => address.Address)
    .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(address))
    ?? throw new InvalidOperationException("A local IPv4 adapter is required for the isolated torrent fixture.");
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
var manifestLine = await fixtureProcess.StandardOutput.ReadLineAsync(fixtureCancel.Token);
if (manifestLine is null)
    throw new InvalidOperationException("Local MSE fixture exited before publishing its manifest: "
        + await fixtureProcess.StandardError.ReadToEndAsync(fixtureCancel.Token));
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
        if (settings.RootElement.GetProperty("ConnectionsLimit").GetInt32() != 100
            || settings.RootElement.GetProperty("DownloadRateLimit").GetInt32() != 0
            || settings.RootElement.GetProperty("PreloadCache").GetInt32() != 50)
            throw new InvalidOperationException("Fresh local runtime does not have the expected desktop throughput settings.");
        Console.WriteLine("Fresh TorrServer profile has LAN Bonjour advertisement disabled.");
        Console.WriteLine("TorrServer uses 100 peer connections, unlimited download rate and 50% preload cache.");
    }
    var link = Uri.EscapeDataString(fixtureTorrent.AbsoluteUri);
    if (args.Contains("--player-ui"))
    {
        await using var gateway = new LocalGateway(new AppPaths(root, data), snapshot);
        await gateway.StartAsync();
        if (!gateway.TryCreateMediaUri(new Uri(gateway.Origin, $"torrserver/stream?link={link}&index=1&play").AbsoluteUri, out var media))
            throw new InvalidOperationException("Could not create isolated torrent playback capability.");
        var report = Path.Combine(root, "artifacts", "torrent-player-ui-smoke.json");
        var start = new ProcessStartInfo(Path.Combine(root, ".tools", "dotnet", "dotnet.exe"))
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root, "tools", "testing", "PlayerUiSmoke", "bin", "Debug", "net10.0-windows", "PlayerUiSmoke.dll"));
        start.ArgumentList.Add(media.AbsoluteUri);
        start.ArgumentList.Add(report);
        using var ui = Process.Start(start) ?? throw new InvalidOperationException("Could not start torrent player UI smoke.");
        var output = ui.StandardOutput.ReadToEndAsync();
        var errors = ui.StandardError.ReadToEndAsync();
        try { await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        finally { if (!ui.HasExited) { ui.Kill(entireProcessTree: true); await ui.WaitForExitAsync(); } }
        await output;
        await errors;
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        if (ui.ExitCode != 0 || !result.RootElement.GetProperty("success").GetBoolean())
            throw new InvalidOperationException("Torrent player UI smoke failed; see artifacts/torrent-player-ui-smoke.json.");
        Console.WriteLine("Torrent player UI smoke passed through the actual gateway, TorrServer and native VLC.");
        return;
    }
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
