using LampaWin.Core;
using LampaWin.Core.Runtime;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
var data = Path.Combine(root, ".cache", "runtime-smoke", Guid.NewGuid().ToString("N"));
var runtime = new LocalRuntime(new AppPaths(root, data));
var recovering = false;
var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
runtime.StatusChanged += status =>
{
    Console.WriteLine($"{status.State}: {status.Message}");
    if (status.State is RuntimeState.Degraded or RuntimeState.Recovering) recovering = true;
    if (recovering && status.State == RuntimeState.Ready) recovered.TrySetResult();
};
var childIds = new List<int>();
try
{
    var snapshot = await runtime.StartAsync();
    Console.WriteLine($"TorrServer ready on loopback port {snapshot.TorrServerBaseUri.Port}");
    Console.WriteLine($"Jackett ready on loopback port {snapshot.JackettBaseUri.Port}");
    Console.WriteLine($"Configured indexers: {string.Join(", ", snapshot.Indexers)}");
    Console.WriteLine($"Warnings: {string.Join(" | ", snapshot.Warnings)}");
    Console.WriteLine($"Jackett API key length: {snapshot.JackettApiKey.Length} characters.");
    AssertLoopbackListener(snapshot.TorrServerBaseUri.Port);
    AssertLoopbackListener(snapshot.JackettBaseUri.Port);

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    using var wrongAuth = new HttpRequestMessage(HttpMethod.Post, new Uri(snapshot.TorrServerBaseUri, "settings"))
    {
        Content = new StringContent("{\"action\":\"get\"}", Encoding.UTF8, "application/json")
    };
    wrongAuth.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("wrong:wrong")));
    using var wrongTorr = await http.SendAsync(wrongAuth);
    if (wrongTorr.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException("TorrServer rejected wrong-credential check.");

    var invalidJackett = new Uri(snapshot.JackettBaseUri, "api/v2.0/indexers/all/results/torznab/api?apikey=invalid&t=indexers&configured=true");
    using var wrongJackett = await http.GetAsync(invalidJackett);
    var wrongBody = await wrongJackett.Content.ReadAsStringAsync();
    var wrongKeyRejected = wrongBody.Contains("<error", StringComparison.OrdinalIgnoreCase)
        || wrongBody.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
        || wrongBody.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    if (!wrongKeyRejected) throw new InvalidOperationException("Jackett did not reject an invalid Torznab key in the response body.");
    Console.WriteLine($"Invalid-key probe rejected: HTTP {(int)wrongJackett.StatusCode} with Torznab error.");

    foreach (var configured in new[] { false, true })
    {
        var probeUri = new Uri(snapshot.JackettBaseUri,
            $"api/v2.0/indexers/all/results/torznab/api?apikey={Uri.EscapeDataString(snapshot.JackettApiKey)}&t=indexers&configured={configured.ToString().ToLowerInvariant()}");
        using var probe = await http.GetAsync(probeUri);
        var body = await probe.Content.ReadAsStringAsync();
        var ids = 0;
        string bodyRoot = "unparsed";
        try
        {
            var xml = System.Xml.Linq.XDocument.Parse(body);
            bodyRoot = xml.Root?.Name.LocalName ?? "empty";
            ids = xml.Descendants().Count(element => element.Name.LocalName.Equals("indexer", StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Xml.XmlException) { bodyRoot = body.Length == 0 ? "empty" : ((int)body[0]).ToString(); }
        Console.WriteLine($"Jackett indexer listing configured={configured}: HTTP {(int)probe.StatusCode}, root={bodyRoot}, items={ids}.");
    }
    foreach (var id in new[] { "byrutor", "1337x", "nyaasi", "rutor", "megapeer", "newstudio", "torrentby", "noname-club" })
    {
        var configUri = new Uri(snapshot.JackettBaseUri, $"api/v2.0/indexers/{id}/config?apikey={Uri.EscapeDataString(snapshot.JackettApiKey)}");
        using var configResponse = await http.GetAsync(configUri);
        var configBody = await configResponse.Content.ReadAsStringAsync();
        var shape = "unparsed";
        try
        {
            using var configJson = System.Text.Json.JsonDocument.Parse(configBody);
            shape = configJson.RootElement.ValueKind.ToString();
            if (configJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                shape += $"[{configJson.RootElement.GetArrayLength()}]";
            if (configJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                shape += "{" + string.Join(",", configJson.RootElement.EnumerateObject().Select(property => property.Name)) + "}";
            if (configJson.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var details = configJson.RootElement.EnumerateArray().Select(item =>
                {
                    var idName = item.TryGetProperty("id", out var idValue) ? idValue.ToString() : "?";
                    var typeName = item.TryGetProperty("type", out var typeValue) ? typeValue.ToString() : "?";
                    var fieldName = item.TryGetProperty("name", out var nameValue) ? nameValue.ToString() : "?";
                    var valueSet = item.TryGetProperty("value", out var value) && value.ValueKind is not System.Text.Json.JsonValueKind.Null
                        and not System.Text.Json.JsonValueKind.Undefined && (value.ValueKind != System.Text.Json.JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString()));
                    return $"{idName}/{fieldName}/{typeName}:valueSet={valueSet}";
                });
                shape += " [" + string.Join("; ", details) + "]";
            }
        }
        catch (System.Text.Json.JsonException) { shape = configBody.StartsWith("<", StringComparison.Ordinal) ? "html" : "non-json"; }
        Console.WriteLine($"Indexer setup endpoint {id}: HTTP {(int)configResponse.StatusCode}, {configResponse.Content.Headers.ContentType?.MediaType ?? "unknown"}, shape={shape}.");
    }

    var searchWorked = false;
    foreach (var id in snapshot.Indexers)
    {
        try
        {
            var searchUri = new Uri(snapshot.JackettBaseUri,
                $"api/v2.0/indexers/{Uri.EscapeDataString(id)}/results/torznab/api?apikey={Uri.EscapeDataString(snapshot.JackettApiKey)}&t=search&q={Uri.EscapeDataString(id.Equals("nyaasi", StringComparison.OrdinalIgnoreCase) ? "Naruto" : "Интерстеллар")}");
            using var search = await http.GetAsync(searchUri);
            var body = await search.Content.ReadAsStringAsync();
            if (search.IsSuccessStatusCode && body.Contains("<item>", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Public search returned at least one result from {id}.");
                searchWorked = true;
                break;
            }
            Console.WriteLine($"Source {id} responded {((int)search.StatusCode)} without results in this probe.");
        }
        catch (HttpRequestException) { Console.WriteLine($"Source {id} could not be reached in this probe."); }
        catch (TaskCanceledException) { Console.WriteLine($"Source {id} timed out in this probe."); }
    }
    if (!searchWorked) Console.WriteLine("No public source returned a result in this external network probe.");

    var torrField = typeof(LocalRuntime).GetField("_torr", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var child = (System.Diagnostics.Process)torrField.GetValue(runtime)!;
    childIds.Add(child.Id);
    child.Kill(entireProcessTree: true);
    var recovery = await Task.WhenAny(recovered.Task, Task.Delay(TimeSpan.FromSeconds(60)));
    if (recovery != recovered.Task) throw new TimeoutException("Owned child did not recover within 60 seconds.");
    AssertLoopbackListener(snapshot.TorrServerBaseUri.Port);
    childIds.Add(((System.Diagnostics.Process)torrField.GetValue(runtime)!).Id);
    var jackettField = typeof(LocalRuntime).GetField("_jackett", BindingFlags.Instance | BindingFlags.NonPublic)!;
    childIds.Add(((System.Diagnostics.Process)jackettField.GetValue(runtime)!).Id);

    await runtime.StopAsync();
    for (var attempt = 0; attempt < 10 && (IsListening(snapshot.TorrServerBaseUri.Port) || IsListening(snapshot.JackettBaseUri.Port)); attempt++)
        await Task.Delay(300);
    if (IsListening(snapshot.TorrServerBaseUri.Port) || IsListening(snapshot.JackettBaseUri.Port))
        throw new InvalidOperationException("A local component still has a listening socket after StopAsync.");
    foreach (var pid in childIds)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); if (!process.HasExited) throw new InvalidOperationException("A child process survived StopAsync."); }
        catch (ArgumentException) { }
    }
    Console.WriteLine("Runtime smoke passed: local auth, readiness, child recovery and shutdown verified.");
}
finally
{
    await runtime.DisposeAsync();
}

static void AssertLoopbackListener(int port)
{
    if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
        .Any(endpoint => endpoint.Port == port && IPAddress.IsLoopback(endpoint.Address)))
        throw new InvalidOperationException($"No loopback listener found for port {port}.");
}

static bool IsListening(int port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
