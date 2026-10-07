using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using LampaWin.Core;
using LampaWin.Core.Runtime;

// Bounded read-only network measurement against an isolated app runtime.
// At most 128 MiB per source, no downloaded content is persisted.
if (args.Length < 2) throw new ArgumentException("Usage: ThroughputProbe <report.json> <public-torrent-url> [more-urls]");
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
var reportPath = Path.GetFullPath(args[0]);
var connectionsLimit = args.Contains("--baseline") ? 25 : 100;
var ownedBefore = Process.GetProcessesByName("TorrServer").Select(p => p.Id).ToHashSet();
var data = Path.Combine(root, ".cache", "throughput-probe", Guid.NewGuid().ToString("N"));
await using var runtime = new LocalRuntime(new AppPaths(root, data));
var measurements = new List<object>();
var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(a => a.OperationalStatus == OperationalStatus.Up)
    .Select(a => new { a.Name, a.Description, bitsPerSecond = a.Speed,
        gateways = a.GetIPProperties().GatewayAddresses.Select(g => g.Address.ToString()).ToArray() }).ToArray();
try
{
    var snapshot = await runtime.StartAsync();
    using var backend = Process.GetProcessesByName("TorrServer").Single(p => !ownedBefore.Contains(p.Id));
    using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{snapshot.TorrServerUser}:{snapshot.TorrServerPassword}")));
    if (connectionsLimit == 25)
    {
        using var currentResponse = await client.PostAsJsonAsync(new Uri(snapshot.TorrServerBaseUri, "settings"), new { action = "get" });
        currentResponse.EnsureSuccessStatusCode();
        var settings = System.Text.Json.Nodes.JsonNode.Parse(await currentResponse.Content.ReadAsStringAsync())!.AsObject();
        settings["ConnectionsLimit"] = 25;
        using var changed = await client.PostAsJsonAsync(new Uri(snapshot.TorrServerBaseUri, "settings"), new { action = "set", sets = settings });
        changed.EnsureSuccessStatusCode();
    }
    foreach (var raw in args.Skip(1).Where(value => value != "--baseline"))
    {
        var source = new Uri(raw);
        if (source.Scheme != "https" || source.UserInfo.Length > 0) throw new ArgumentException("Public HTTPS torrent URL required.");
        var samples = new List<object>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var started = Stopwatch.StartNew();
        var cpuStart = backend.TotalProcessorTime;
        long bytes = 0;
        string? failure = null;
        string? hash = null;
        var readTask = Task.CompletedTask;
        try
        {
            using var added = await client.PostAsJsonAsync(new Uri(snapshot.TorrServerBaseUri, "torrents"),
                new { action = "add", link = source.AbsoluteUri, title = "Throughput probe", save_to_db = false }, timeout.Token);
            added.EnsureSuccessStatusCode();
            using var info = JsonDocument.Parse(await added.Content.ReadAsStringAsync(timeout.Token));
            hash = info.RootElement.GetProperty("hash").GetString();
            readTask = ReadAsync();
            while (!readTask.IsCompleted && started.Elapsed < TimeSpan.FromSeconds(45))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
                using var stat = await client.PostAsJsonAsync(new Uri(snapshot.TorrServerBaseUri, "torrents"),
                    new { action = "get", hash }, timeout.Token);
                stat.EnsureSuccessStatusCode();
                using var status = JsonDocument.Parse(await stat.Content.ReadAsStringAsync(timeout.Token));
                var item = status.RootElement;
                backend.Refresh();
                samples.Add(new { seconds = started.Elapsed.TotalSeconds, streamBytes = Interlocked.Read(ref bytes),
                    downloadBytesPerSecond = Number(item, "download_speed"), activePeers = Number(item, "active_peers"),
                    seeders = Number(item, "connected_seeders"), usefulBytes = Number(item, "bytes_read_useful_data"),
                    cpuSeconds = (backend.TotalProcessorTime - cpuStart).TotalSeconds, memoryBytes = backend.WorkingSet64 });
                Console.WriteLine($"{source.Segments.Last()}: {started.Elapsed.TotalSeconds:F0}s, {Interlocked.Read(ref bytes)} bytes, peers={Number(item, "active_peers")}, speed={Number(item, "download_speed") * 8 / 1_000_000:F2} Mbit/s");
            }
            timeout.Cancel();
            try { await readTask; } catch (OperationCanceledException) { }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        { failure = ex.GetType().Name; timeout.Cancel(); try { await readTask; } catch (Exception) { } }
        finally
        {
            if (hash is not null)
            {
                using var dropTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var dropped = await client.PostAsJsonAsync(new Uri(snapshot.TorrServerBaseUri, "torrents"), new { action = "drop", hash }, dropTimeout.Token);
            }
        }
        measurements.Add(new { source = source.AbsoluteUri, elapsedSeconds = started.Elapsed.TotalSeconds,
            streamBytes = bytes, failure, samples });

        async Task ReadAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(snapshot.TorrServerBaseUri,
                $"stream?link={Uri.EscapeDataString(source.AbsoluteUri)}&index=1&play"));
            request.Headers.Range = new RangeHeaderValue(0, 128L * 1024 * 1024 - 1);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[65536];
            while (Interlocked.Read(ref bytes) < 128L * 1024 * 1024)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                Interlocked.Add(ref bytes, count);
            }
        }
    }
}
finally
{
    await runtime.StopAsync();
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow,
        diskCache = false, connectionsLimit, downloadRateLimit = 0, adapters, measurements }, new JsonSerializerOptions { WriteIndented = true }));
}

static double Number(JsonElement item, string name) => item.TryGetProperty(name, out var number) && number.TryGetDouble(out var value) ? value : 0;
