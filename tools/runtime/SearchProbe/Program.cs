using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using LampaWin.Core;
using LampaWin.Core.Runtime;

if (args.Length < 2) throw new ArgumentException("Usage: SearchProbe <report.json> <query>");
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
var paths = new AppPaths(root, Path.Combine(root, ".cache", "search-probe", Guid.NewGuid().ToString("N")));
await using var runtime = new LocalRuntime(paths);
var checks = new Dictionary<string, object?> { ["query"] = args[1], ["utc"] = DateTimeOffset.UtcNow };
try
{
    var snapshot = await runtime.StartAsync();
    await using var gateway = new LocalGateway(paths, snapshot);
    await gateway.StartAsync();
    using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { Timeout = TimeSpan.FromSeconds(110) };
    using var login = await client.GetAsync(gateway.LoginUri);
    login.EnsureSuccessStatusCode();
    var yearArgument = args.FirstOrDefault(argument => argument.StartsWith("--year=", StringComparison.Ordinal));
    var contextQuery = yearArgument is not null && int.TryParse(yearArgument[7..], out var selectedYear)
        ? "&lampawin_year=" + selectedYear + "&lampawin_kind=tv" : string.Empty;
    using var response = await client.GetAsync(new Uri(gateway.Origin,
        "jackett/api/v2.0/indexers/all/results?Query=" + Uri.EscapeDataString(args[1]) + "&Category[]=2000&Category[]=5000" + contextQuery));
    response.EnsureSuccessStatusCode();
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var results = body.RootElement.GetProperty("Results");
    checks["resultsCount"] = results.GetArrayLength();
    checks["results"] = results.EnumerateArray().Take(15).Select(result => new
    {
        title = result.TryGetProperty("Title", out var title) ? title.GetString() : null,
        seeders = result.TryGetProperty("Seeders", out var seeders) ? seeders.GetRawText() : null,
        category = result.TryGetProperty("Category", out var category) ? category.GetRawText() : null
    }).ToArray();
    checks["success"] = results.GetArrayLength() > 0;
    checks["searchSuccess"] = results.GetArrayLength() > 0;
    Console.WriteLine($"Search returned {results.GetArrayLength()} filtered results; report contains titles and seed counts only.");
    if (args.Contains("--playback") && results.GetArrayLength() > 0)
    {
        var first = results[0];
        var link = first.TryGetProperty("MagnetUri", out var magnet) && !string.IsNullOrWhiteSpace(magnet.GetString())
            ? magnet.GetString() : first.GetProperty("Link").GetString();
        using var torrents = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        torrents.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{snapshot.TorrServerUser}:{snapshot.TorrServerPassword}")));
        using var added = await torrents.PostAsJsonAsync(new Uri(snapshot.TorrServerBaseUri, "torrents"), new { action = "add", link, save_to_db = false });
        added.EnsureSuccessStatusCode();
        using var info = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        var hash = info.RootElement.GetProperty("hash").GetString();
        var torrentInfo = info.RootElement.Clone();
        var metadataDeadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (DateTimeOffset.UtcNow < metadataDeadline &&
            (!torrentInfo.TryGetProperty("file_stats", out var knownFiles) || knownFiles.GetArrayLength() == 0))
        {
            await Task.Delay(1000);
            using var current = await torrents.PostAsJsonAsync(new Uri(snapshot.TorrServerBaseUri, "torrents"), new { action = "get", hash });
            current.EnsureSuccessStatusCode();
            using var currentInfo = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
            torrentInfo = currentInfo.RootElement.Clone();
        }
        checks["torrentMetadataReady"] = torrentInfo.TryGetProperty("file_stats", out var availableFiles) && availableFiles.GetArrayLength() > 0;
        foreach (var field in new[] { "stat", "active_peers", "connected_seeders", "download_speed", "torrent_size" })
            if (torrentInfo.TryGetProperty(field, out var value)) checks[field] = value.Clone();
        if (checks["torrentMetadataReady"] is false) throw new InvalidOperationException("Torrent metadata was not available within the diagnostic window.");
        var index = 1;
        if (torrentInfo.TryGetProperty("file_stats", out var files))
        {
            var video = files.EnumerateArray().Where(file => file.TryGetProperty("path", out var path)
                && new[] { ".mp4", ".mkv", ".avi", ".mov", ".m4v" }.Contains(Path.GetExtension(path.GetString()!).ToLowerInvariant()))
                .OrderByDescending(file => file.GetProperty("length").GetInt64()).FirstOrDefault();
            if (video.ValueKind == JsonValueKind.Object) index = video.GetProperty("id").GetInt32();
        }
        if (!gateway.TryCreateMediaUri(new Uri(gateway.Origin, $"torrserver/stream?link={hash}&index={index}&play").AbsoluteUri, out var media))
            throw new InvalidOperationException("No media capability for external torrent.");
        var start = new ProcessStartInfo(Path.Combine(root, ".tools", "dotnet", "dotnet.exe"))
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root, "tools", "testing", "PlayerUiSmoke", "bin", "Debug", "net10.0-windows10.0.17763.0", "PlayerUiSmoke.dll"));
        start.ArgumentList.Add(media.AbsoluteUri);
        start.ArgumentList.Add(Path.Combine(root, "artifacts", "external-torrent-player.json"));
        start.ArgumentList.Add("--external-video");
        using var player = Process.Start(start)!;
        var output = player.StandardOutput.ReadToEndAsync();
        var errors = player.StandardError.ReadToEndAsync();
        try { await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100)); }
        finally { if (!player.HasExited) { player.Kill(entireProcessTree: true); await player.WaitForExitAsync(); } }
        await output; await errors;
        using var playback = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts", "external-torrent-player.json")));
        checks["playbackSuccess"] = playback.RootElement.GetProperty("success").GetBoolean();
        if (checks["playbackSuccess"] is false) checks["success"] = false;
    }
}
catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or TimeoutException)
{
    checks["success"] = false;
    checks["failure"] = error.GetType().Name;
}
finally
{
    await runtime.StopAsync();
    var report = Path.GetFullPath(args[0]);
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
}
