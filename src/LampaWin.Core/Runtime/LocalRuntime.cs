using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace LampaWin.Core.Runtime;

/// <summary>Owns the two loopback services shipped with LampaWin.</summary>
public sealed class LocalRuntime : ILocalRuntime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan JackettStartupTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SourceSetupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(4);
    private const long MaxLogBytes = 512 * 1024;
    private const long MaxJackettLogBytes = 2 * 1024 * 1024;
    // Public definitions only: Russian-language sources plus a broad fallback and anime source.
    // NewStudio is IP allowlisted; Noname-Club commonly challenges; TorrentBy has TLS failures.
    private static readonly string[] DefaultIndexerIds = ["rutor", "1337x", "nyaasi"];

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly HttpClient _setupHttp = new() { Timeout = SourceSetupTimeout };
    private readonly object _logGate = new();
    private readonly List<string> _indexerSetupFailures = [];
    private Process? _torr;
    private Process? _jackett;
    private ChildJob? _torrJob;
    private ChildJob? _jackettJob;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private RuntimeSnapshot? _snapshot;
    private int _torrPort;
    private int _jackettPort;
    private bool _disposed;

    public LocalRuntime(AppPaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public event Action<RuntimeStatus>? StatusChanged;
    /// <summary>Last verified endpoints and generated credentials for the local gateway.</summary>
    public RuntimeSnapshot? Snapshot => _snapshot;

    public async Task<RuntimeSnapshot> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_snapshot is not null && await AreHealthyAsync(_snapshot, cancellationToken).ConfigureAwait(false))
                return _snapshot;

            SetStatus(RuntimeState.Starting, "Запуск локальных компонентов…");
            await StopCoreAsync().ConfigureAwait(false);
            _paths.EnsureDirectories();
            BoundJackettLog();
            EnsureExecutable(_paths.TorrServerExecutable, "TorrServer");
            EnsureExecutable(_paths.JackettExecutable, "Jackett");

            var freshTorrData = !File.Exists(Path.Combine(_paths.TorrServerData, "config.db"))
                && !File.Exists(Path.Combine(_paths.TorrServerData, "settings.json"));
            var credentials = await LoadOrCreateTorrCredentialsAsync(cancellationToken).ConfigureAwait(false);
            _torrPort = GetFreeLoopbackPort();
            _jackettPort = GetFreeLoopbackPort(exclude: _torrPort);
            var warnings = new List<string>();

            try
            {
                _torr = StartChild(_paths.TorrServerExecutable, _paths.ComponentsRoot,
                    ["--ip", "127.0.0.1", "--port", _torrPort.ToString(), "--path", _paths.TorrServerData,
                     "--torrentsdir", Path.Combine(_paths.TorrServerData, "torrents"), "--httpauth"],
                    "torrserver.stdout.log");
                _torrJob = ChildJob.TryAssign(_torr);

                await PrepareJackettConfigAsync(cancellationToken).ConfigureAwait(false);
                _jackett = StartChild(_paths.JackettExecutable, Path.GetDirectoryName(_paths.JackettExecutable)!,
                    ["--ListenPrivate", "--NoUpdates", "--DataFolder", _paths.JackettData], "jackett.stdout.log");
                _jackettJob = ChildJob.TryAssign(_jackett);

                var torrUri = new Uri($"http://127.0.0.1:{_torrPort}/");
                var jackettUri = new Uri($"http://127.0.0.1:{_jackettPort}/");
                var key = await WaitForJackettAsync(jackettUri, _jackett, cancellationToken).ConfigureAwait(false);
                await WaitForTorrServerAsync(torrUri, credentials.User, credentials.Password, _torr, cancellationToken).ConfigureAwait(false);
                if (freshTorrData)
                    await ApplyTorrDefaultsOnceAsync(torrUri, credentials.User, credentials.Password, cancellationToken).ConfigureAwait(false);

                var indexers = await BootstrapIndexersOnceAsync(jackettUri, key, cancellationToken).ConfigureAwait(false);
                lock (_indexerSetupFailures)
                    if (_indexerSetupFailures.Count > 0)
                        warnings.Add("Не удалось автоматически включить некоторые источники поиска: " + string.Join(", ", _indexerSetupFailures));
                if (indexers.Count == 0)
                    warnings.Add("В Jackett пока нет настроенных источников поиска.");
                if (indexers.Count < DefaultIndexerIds.Length)
                    warnings.Add("Некоторые общедоступные источники поиска временно недоступны.");

                _snapshot = new RuntimeSnapshot(torrUri, jackettUri, key, credentials.User, credentials.Password,
                    indexers, warnings);
                StartMonitor();
                SetStatus(RuntimeState.Ready, "Локальные компоненты готовы.");
                return _snapshot;
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                SetStatus(RuntimeState.Failed, "Не удалось запустить локальные компоненты. Проверьте установку и попробуйте восстановление.");
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<RuntimeSnapshot> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SetStatus(RuntimeState.Recovering, "Восстановление локальных компонентов…");
            if (_snapshot is { } snapshot)
            {
                await StopMonitorAsync().ConfigureAwait(false);
                try
                {
                    var health = await ReadHealthAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    if (!health.Torr) await RestartComponentAsync(true, cancellationToken).ConfigureAwait(false);
                    if (!health.Jackett) await RestartComponentAsync(false, cancellationToken).ConfigureAwait(false);
                }
                finally { if (!_disposed && !cancellationToken.IsCancellationRequested) StartMonitor(); }
                SetStatus(RuntimeState.Ready, "Локальные компоненты готовы.");
                return _snapshot!;
            }
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(RuntimeState.Stopped, "Локальные компоненты остановлены.");
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _http.Dispose();
        _setupHttp.Dispose();
        _gate.Dispose();
    }

    private Process StartChild(string executable, string workingDirectory, IReadOnlyList<string> args, string logName)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Component process could not be started.");
        var logPath = Path.Combine(_paths.LogsDirectory, logName);
        _ = PumpOutputAsync(process.StandardOutput, logPath);
        _ = PumpOutputAsync(process.StandardError, logPath);
        return process;
    }

    private async Task PumpOutputAsync(StreamReader reader, string logPath)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var safe = Redact(line);
                lock (_logGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    var info = new FileInfo(logPath);
                    if (info.Exists && info.Length >= MaxLogBytes)
                    {
                        using var stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                        var marker = Encoding.UTF8.GetBytes("[earlier log data removed at size limit]\n");
                        stream.Write(marker);
                    }
                    File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} {safe}{Environment.NewLine}", Encoding.UTF8);
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private string Redact(string text)
    {
        var snapshot = _snapshot;
        if (snapshot is not null)
            text = RedactKnownSecrets(text, snapshot.TorrServerPassword, snapshot.JackettApiKey);
        text = System.Text.RegularExpressions.Regex.Replace(text,
            """(?i)(apikey|api_key|password|authorization\s*[:=])([\s=:"]+)([^&\s",}]+)""", "$1$2[REDACTED]");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            "(?i)(authorization\\s*[:=]\\s*)(basic\\s+)?[a-z0-9+/=_-]+", "$1[REDACTED]");
        return text.Length > 4000 ? text[..4000] : text;
    }

    private static string RedactKnownSecrets(string text, params string[] secrets)
    {
        foreach (var secret in secrets)
            if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }

    private async Task<string> WaitForJackettAsync(Uri baseUri, Process process, CancellationToken token)
    {
        var until = DateTime.UtcNow + JackettStartupTimeout;
        while (DateTime.UtcNow < until)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("Jackett exited during startup.");
            try
            {
                using var response = await _http.GetAsync(new Uri(baseUri, "api/v2.0/server/config"), token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                    if ((TryGetString(document.RootElement, "APIKey", out var key)
                         || TryGetString(document.RootElement, "api_key", out key)) && key.Length >= 16)
                        return key;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        throw new TimeoutException("Jackett did not become ready.");
    }

    private async Task WaitForTorrServerAsync(Uri baseUri, string user, string password, Process process, CancellationToken token)
    {
        var until = DateTime.UtcNow + StartupTimeout;
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        while (DateTime.UtcNow < until)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("TorrServer exited during startup.");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "echo"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        throw new TimeoutException("TorrServer did not become ready.");
    }

    private async Task<IReadOnlyList<string>> ReadConfiguredIndexerIdsAsync(Uri baseUri, string apiKey, CancellationToken token)
    {
        var endpoint = TorznabIndexersUri(baseUri, apiKey, true);
        try
        {
            using var response = await _http.GetAsync(endpoint, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<string>();
            var document = XDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            return document.Descendants().Where(element => element.Name.LocalName.Equals("indexer", StringComparison.OrdinalIgnoreCase))
                .Select(element => (string?)element.Attribute("id")).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).Cast<string>().ToArray();
        }
        catch (Exception ex) when (!token.IsCancellationRequested
            && ex is HttpRequestException or JsonException or TaskCanceledException or System.Xml.XmlException)
        { return Array.Empty<string>(); }
    }

    private async Task<IReadOnlyList<string>> BootstrapIndexersOnceAsync(Uri baseUri, string apiKey, CancellationToken token)
    {
        var configured = await ReadConfiguredIndexerIdsAsync(baseUri, apiKey, token).ConfigureAwait(false);
        var marker = Path.Combine(_paths.JackettData, "lampawin-indexers-bootstrap-v2.json");
        if (configured.Count == 0 && !File.Exists(marker))
        {
            lock (_indexerSetupFailures) _indexerSetupFailures.Clear();
            var available = await ReadAvailableIndexerIdsAsync(baseUri, apiKey, token).ConfigureAwait(false);
            if (available is not null)
            {
                foreach (var id in DefaultIndexerIds)
                {
                    if (!available.Contains(id)) continue;
                    Exception? failure = null;
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            await ConfigureAnonymousIndexerAsync(baseUri, apiKey, id, token).ConfigureAwait(false);
                            failure = null;
                            break;
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested
                            && ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
                        {
                            failure = ex;
                            if (attempt < 2 && (ex is JsonException or TaskCanceledException
                                || ex is HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError }))
                                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), token).ConfigureAwait(false);
                            else break;
                        }
                    }
                    // A source failure is a warning; it never grounds for deleting user data.
                    if (failure is not null)
                    {
                        var status = failure is HttpRequestException { StatusCode: { } code } ? $" HTTP {(int)code}" : string.Empty;
                        lock (_indexerSetupFailures) _indexerSetupFailures.Add($"{id} ({failure.GetType().Name}{status})");
                    }
                }
                var temp = marker + ".new";
                await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(new { version = 2, indexers = DefaultIndexerIds }), token).ConfigureAwait(false);
                File.Move(temp, marker, overwrite: true);
            }
            else
            {
                lock (_indexerSetupFailures) _indexerSetupFailures.Add("список источников (временно недоступен)");
            }
            configured = await ReadConfiguredIndexerIdsAsync(baseUri, apiKey, token).ConfigureAwait(false);
        }
        return configured;
    }

    private async Task<HashSet<string>?> ReadAvailableIndexerIdsAsync(Uri baseUri, string apiKey, CancellationToken token)
    {
        var endpoint = TorznabIndexersUri(baseUri, apiKey, false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await _setupHttp.GetAsync(endpoint, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt == 0 && (int)response.StatusCode >= 500)
                    {
                        await Task.Delay(250, token).ConfigureAwait(false);
                        continue;
                    }
                    return null;
                }
                var document = XDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var element in document.Descendants().Where(element => element.Name.LocalName.Equals("indexer", StringComparison.OrdinalIgnoreCase)))
                    if ((string?)element.Attribute("id") is { Length: > 0 } id) result.Add(id);
                return result;
            }
            catch (Exception ex) when (!token.IsCancellationRequested
                && ex is HttpRequestException or JsonException or TaskCanceledException or System.Xml.XmlException)
            {
                if (attempt == 0)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    continue;
                }
                return null;
            }
        }
        return null;
    }

    private static Uri TorznabIndexersUri(Uri baseUri, string apiKey, bool configured)
        => new(baseUri, $"api/v2.0/indexers/all/results/torznab/api?apikey={Uri.EscapeDataString(apiKey)}&t=indexers&configured={configured.ToString().ToLowerInvariant()}");

    private async Task ConfigureAnonymousIndexerAsync(Uri baseUri, string apiKey, string indexerId, CancellationToken token)
    {
        var uri = new Uri(baseUri, $"api/v2.0/indexers/{Uri.EscapeDataString(indexerId)}/config?apikey={Uri.EscapeDataString(apiKey)}");
        using var get = await _setupHttp.GetAsync(uri, token).ConfigureAwait(false);
        get.EnsureSuccessStatusCode();
        using var config = JsonDocument.Parse(await get.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (config.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (var item in config.RootElement.EnumerateArray())
        {
            if ((TryGetString(item, "id", out var fieldId) && IsCredentialField(fieldId))
                || (TryGetString(item, "name", out var fieldName) && IsCredentialField(fieldName))) return;
        }
        using var content = new StringContent(config.RootElement.GetRawText(), Encoding.UTF8, "application/json");
        using var post = await _setupHttp.PostAsync(uri, content, token).ConfigureAwait(false);
        post.EnsureSuccessStatusCode();
    }

    private static bool IsCredentialField(string field)
    {
        var value = field.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return value.Contains("user", StringComparison.Ordinal) || value.Contains("pass", StringComparison.Ordinal)
            || value.Contains("cookie", StringComparison.Ordinal) || value.Contains("token", StringComparison.Ordinal)
            || value.Contains("apikey", StringComparison.Ordinal) || value.Contains("secret", StringComparison.Ordinal)
            || value.Contains("auth", StringComparison.Ordinal);
    }

    private async Task ApplyTorrDefaultsOnceAsync(Uri baseUri, string user, string password, CancellationToken token)
    {
        var marker = Path.Combine(_paths.TorrServerData, "lampawin-settings-v2.json");
        if (File.Exists(marker)) return;
        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        using var get = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "settings"))
        {
            Content = new StringContent("{\"action\":\"get\"}", Encoding.UTF8, "application/json")
        };
        get.Headers.Authorization = auth;
        using var response = await _setupHttp.SendAsync(get, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var current = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.RootElement.GetRawText()) ?? new();
        using var cacheValue = JsonDocument.Parse("268435456");
        using var preloadValue = JsonDocument.Parse("50");
        using var connectionsValue = JsonDocument.Parse("100");
        using var unlimitedValue = JsonDocument.Parse("0");
        using var diskValue = JsonDocument.Parse("false");
        using var pathValue = JsonDocument.Parse("\"\"");
        settings["CacheSize"] = cacheValue.RootElement.Clone();
        settings["PreloadCache"] = preloadValue.RootElement.Clone();
        // TorrServer defaults to 25 peers, which is unnecessarily restrictive on a desktop
        // connection. Keep rate limiting disabled and allow a broader peer set while retaining
        // the upstream TCP/uTP/DHT/PEX/UPnP defaults.
        settings["ConnectionsLimit"] = connectionsValue.RootElement.Clone();
        settings["DownloadRateLimit"] = unlimitedValue.RootElement.Clone();
        settings["UseDisk"] = diskValue.RootElement.Clone();
        settings["TorrentsSavePath"] = pathValue.RootElement.Clone();
        // This runtime is intentionally loopback-only; it does not need to advertise its control API on the LAN.
        settings["EnableBonjour"] = diskValue.RootElement.Clone();
        var payload = new Dictionary<string, object?> { ["action"] = "set", ["sets"] = settings };
        using var set = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "settings")) { Content = JsonContent.Create(payload) };
        set.Headers.Authorization = auth;
        using var setResponse = await _setupHttp.SendAsync(set, token).ConfigureAwait(false);
        setResponse.EnsureSuccessStatusCode();
        await File.WriteAllTextAsync(marker, "{\"version\":2}", token).ConfigureAwait(false);
    }

    private async Task<bool> AreHealthyAsync(RuntimeSnapshot snapshot, CancellationToken token)
    {
        var health = await ReadHealthAsync(snapshot, token).ConfigureAwait(false);
        return health.Torr && health.Jackett;
    }

    private async Task<(bool Torr, bool Jackett)> ReadHealthAsync(RuntimeSnapshot snapshot, CancellationToken token)
    {
        var torr = IsRunning(_torr) ? ProbeAsync(new Uri(snapshot.TorrServerBaseUri, "echo"), snapshot.TorrServerUser, snapshot.TorrServerPassword, token) : Task.FromResult(false);
        var jackett = IsRunning(_jackett) ? ProbeAsync(new Uri(snapshot.JackettBaseUri, "api/v2.0/server/config"), null, null, token) : Task.FromResult(false);
        await Task.WhenAll(torr, jackett).ConfigureAwait(false);
        return (await torr, await jackett);
    }

    private async Task<bool> ProbeAsync(Uri uri, string? user, string? password, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (user is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    private void StartMonitor()
    {
        _monitorCancellation = new CancellationTokenSource();
        _monitorTask = MonitorAsync(_monitorCancellation.Token);
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        var backoff = new[] { 1, 2, 4 };
        var misses = new int[2];
        var attempts = new int[2];
        var healthyTicks = new int[2];
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ProbeInterval, token).ConfigureAwait(false);
                BoundJackettLog();
                if (_snapshot is not { } snapshot) return;
                var health = await ReadHealthAsync(snapshot, token).ConfigureAwait(false);
                for (var component = 0; component < 2; component++)
                {
                    var healthy = component == 0 ? health.Torr : health.Jackett;
                    if (healthy)
                    {
                        misses[component] = 0;
                        if (++healthyTicks[component] >= 15) attempts[component] = 0;
                        continue;
                    }
                    healthyTicks[component] = 0;
                    if (++misses[component] < 3) continue;
                    var name = component == 0 ? "TorrServer" : "Jackett";
                    if (attempts[component] >= backoff.Length)
                    {
                        SetStatus(RuntimeState.Failed, name + " недоступен. Нажмите «Восстановить». Другой сервис продолжает работать.");
                        continue;
                    }
                    await _gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (_snapshot is null) return;
                        SetStatus(RuntimeState.Recovering, "Восстанавливаем " + name + "…");
                        await Task.Delay(TimeSpan.FromSeconds(backoff[attempts[component]++]), token).ConfigureAwait(false);
                        await RestartComponentAsync(component == 0, token).ConfigureAwait(false);
                        misses[component] = 0;
                        SetStatus(RuntimeState.Ready, name + " восстановлен.");
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    { SetStatus(RuntimeState.Degraded, name + " временно недоступен (" + ex.GetType().Name + ")."); }
                    finally { _gate.Release(); }
                }
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RestartComponentAsync(bool torr, CancellationToken token)
    {
        if (_snapshot is null) return;
        var snapshot = _snapshot;
        if (torr)
        {
            await StopOneAsync(_torr, _torrJob).ConfigureAwait(false);
            _torr = null; _torrJob?.Dispose(); _torrJob = null;
            _torr = StartChild(_paths.TorrServerExecutable, _paths.ComponentsRoot,
                ["--ip", "127.0.0.1", "--port", _torrPort.ToString(), "--path", _paths.TorrServerData,
                 "--torrentsdir", Path.Combine(_paths.TorrServerData, "torrents"), "--httpauth"], "torrserver.stdout.log");
            _torrJob = ChildJob.TryAssign(_torr);
            await WaitForTorrServerAsync(snapshot.TorrServerBaseUri, snapshot.TorrServerUser, snapshot.TorrServerPassword, _torr, token).ConfigureAwait(false);
        }
        else
        {
            await StopOneAsync(_jackett, _jackettJob).ConfigureAwait(false);
            _jackett = null; _jackettJob?.Dispose(); _jackettJob = null;
            await PrepareJackettConfigAsync(token).ConfigureAwait(false);
            _jackett = StartChild(_paths.JackettExecutable, Path.GetDirectoryName(_paths.JackettExecutable)!,
                ["--ListenPrivate", "--NoUpdates", "--DataFolder", _paths.JackettData], "jackett.stdout.log");
            _jackettJob = ChildJob.TryAssign(_jackett);
            var key = await WaitForJackettAsync(snapshot.JackettBaseUri, _jackett, token).ConfigureAwait(false);
            _snapshot = snapshot with { JackettApiKey = key };
        }
    }

    private async Task StopCoreAsync()
    {
        _snapshot = null;
        await StopMonitorAsync().ConfigureAwait(false);
        await StopChildrenAsync().ConfigureAwait(false);
    }

    private async Task StopMonitorAsync()
    {
        _monitorCancellation?.Cancel();
        if (_monitorTask is not null)
        {
            try { await _monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        _monitorTask = null;
    }

    private async Task StopChildrenAsync()
    {
        await StopOneAsync(_torr, _torrJob).ConfigureAwait(false);
        _torr = null; _torrJob?.Dispose(); _torrJob = null;
        await StopOneAsync(_jackett, _jackettJob).ConfigureAwait(false);
        _jackett = null; _jackettJob?.Dispose(); _jackettJob = null;
    }

    private static async Task StopOneAsync(Process? process, ChildJob? job)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                if (job is not null) job.Kill();
                else process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TimeoutException) { try { process.Kill(entireProcessTree: true); } catch { } }
        finally { process.Dispose(); }
    }

    private async Task<(string User, string Password)> LoadOrCreateTorrCredentialsAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_paths.TorrServerData);
        var authFile = Path.Combine(_paths.TorrServerData, "accs.db");
        if (File.Exists(authFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(authFile, token).ConfigureAwait(false));
                var prop = doc.RootElement.EnumerateObject().FirstOrDefault();
                if (prop.Name is { Length: > 0 } && prop.Value.GetString() is { Length: > 0 } password)
                    return (prop.Name, password);
            }
            catch (JsonException) { }
        }
        var user = "lw" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        var pass = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', 'x').Replace('/', 'y');
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { [user] = pass });
        var temp = authFile + ".new";
        await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
        File.Move(temp, authFile, overwrite: true);
        return (user, pass);
    }

    private async Task PrepareJackettConfigAsync(CancellationToken token)
    {
        var configPath = Path.Combine(_paths.JackettData, "ServerConfig.json");
        JsonObject config;
        if (File.Exists(configPath))
        {
            try
            {
                config = JsonNode.Parse(await File.ReadAllTextAsync(configPath, token).ConfigureAwait(false)) as JsonObject
                    ?? throw new InvalidOperationException("Jackett configuration has an unexpected format.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Jackett configuration is unreadable; it was preserved.", ex);
            }
        }
        else config = new JsonObject();

        SetJsonProperty(config, "Port", JsonValue.Create(_jackettPort));
        SetJsonProperty(config, "AllowExternal", JsonValue.Create(false));
        SetJsonProperty(config, "LocalBindAddress", JsonValue.Create("127.0.0.1"));
        SetJsonProperty(config, "AllowCORS", JsonValue.Create(false));
        var tempPath = configPath + ".new";
        await File.WriteAllTextAsync(tempPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token).ConfigureAwait(false);
        File.Move(tempPath, configPath, overwrite: true);
    }

    private static void SetJsonProperty(JsonObject config, string name, JsonNode? value)
    {
        var oldName = config.Select(pair => pair.Key).FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));
        config[oldName ?? name] = value;
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var item in element.EnumerateObject())
                if (item.Name.Equals(property, StringComparison.OrdinalIgnoreCase) && item.Value.ValueKind == JsonValueKind.String)
                { value = item.Value.GetString() ?? string.Empty; return true; }
        value = string.Empty; return false;
    }

    private static int GetFreeLoopbackPort(int exclude = 0)
    {
        for (var i = 0; i < 8; i++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != exclude) return port;
        }
        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private static bool IsRunning(Process? process)
    {
        try { return process is { HasExited: false }; }
        catch (InvalidOperationException) { return false; }
    }

    private void BoundJackettLog()
    {
        var path = Path.Combine(_paths.JackettData, "log.txt");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (stream.Length <= MaxJackettLogBytes) return;
            var keep = (int)(MaxJackettLogBytes / 2);
            var tail = new byte[keep];
            stream.Seek(-keep, SeekOrigin.End);
            _ = stream.Read(tail, 0, tail.Length);
            var content = Redact(Encoding.UTF8.GetString(tail));
            stream.Position = 0;
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.WriteLine("[earlier Jackett log data removed at size limit]");
            writer.Write(content);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void EnsureExecutable(string path, string name)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Bundled {name} component is missing.", path);
    }

    private void SetStatus(RuntimeState state, string message)
    {
        try { StatusChanged?.Invoke(new RuntimeStatus(state, message)); } catch { /* UI observers must not break lifecycle. */ }
    }

    /// <summary>Windows Job Object makes child cleanup reliable if the host process crashes.</summary>
    private sealed class ChildJob : IDisposable
    {
        private IntPtr _handle;
        private ChildJob(IntPtr handle) => _handle = handle;
        public static ChildJob? TryAssign(Process process)
        {
            if (!OperatingSystem.IsWindows()) return null;
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return null;
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x00002000 } // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            };
            var size = System.Runtime.InteropServices.Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(handle, 9, ptr, (uint)size) || !AssignProcessToJobObject(handle, process.Handle))
                { CloseHandle(handle); return null; }
                return new ChildJob(handle);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
        }
        public void Kill() { if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1); }
        public void Dispose() { if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; } }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
