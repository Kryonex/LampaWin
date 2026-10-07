using LampaWin.Core;
using LampaWin.Core.Runtime;
using Microsoft.Web.WebView2.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace LampaWin.Desktop;

public static class DesktopBootstrap
{
    private static Session? _session;
    private static Mutex? _instance;
    public static async Task RunAsync()
    {
        var arguments = Environment.GetCommandLineArgs();
        var smoke = arguments.Contains("--self-test", StringComparer.Ordinal);
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _instance = new Mutex(true, "Local\\LampaWin-" + user + (smoke ? "-test-" + Environment.ProcessId : ""), out var created);
        if (!created)
        {
            MessageBox.Show("LampaWin уже запущен. Откройте существующее окно приложения.", "LampaWin");
            Application.Current.Shutdown();
            return;
        }
        var install = ArgumentValue(arguments, "--install-root") ?? AppContext.BaseDirectory;
        var data = ArgumentValue(arguments, "--data-root");
        var paths = new AppPaths(install, data);
        paths.EnsureDirectories();
        var window = new MainWindow();
        Application.Current.MainWindow = window;
        if (smoke && !arguments.Contains("--show-window", StringComparer.Ordinal))
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000; window.Top = -30000;
        }
        _session = new Session(paths, window);
        window.Show();
        if (smoke)
        {
            var report = ArgumentValue(arguments, "--report") ?? Path.Combine(paths.DataRoot, "self-test.json");
            var fixture = ArgumentValue(arguments, "--fixture");
            var torrent = ArgumentValue(arguments, "--torrent-url");
            await _session.RunSelfTestAsync(report, fixture, torrent);
        }
        else await _session.InitializeAsync();
    }
    private static string? ArgumentValue(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    private sealed class Session
    {
        private readonly AppPaths _paths;
        private readonly MainWindow _window;
        private readonly LocalRuntime _runtime;
        private readonly ProfileStore _profile;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _initializeGate = new(1);
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private LocalGateway? _gateway;
        private bool _closing;
        private bool _browserConfigured;
        private string? _documentScriptId;
        private PlaybackProgress? _lastProgress;
        private readonly List<string> _errors = [];
        private TaskCompletionSource<PlaybackProgress>? _testPlayback;
        private readonly List<string> _testStates = [];
        private readonly List<PlaybackDiagnostic> _testDiagnostics = [];
        private bool _testFailed;
        public Session(AppPaths paths, MainWindow window)
        {
            _paths = paths; _window = window;
            _runtime = new LocalRuntime(paths);
            _profile = new ProfileStore(paths);
            _window.SetLogsPath(paths.LogsDirectory);
            _window.RecoverRequested += async () => await InitializeAsync(recover: true);
            _window.PlaybackChanged += OnProgress;
            _window.PlaybackDiagnosticChanged += diagnostic =>
            {
                if (_testDiagnostics.Count < 100) _testDiagnostics.Add(diagnostic);
                if (diagnostic.Category is "source-connection-failed" or "source-timeout" or "source-auth-rejected" or "source-not-found" or "source-open-failed" or "native-encountered-error")
                    _window.SetStatus("Не удалось открыть видео. Проверьте доступность раздачи или выберите другой источник.", false);
            };
            _window.PlaybackClosed += OnPlaybackClosed;
            _window.Closing += OnClosing;
            _runtime.StatusChanged += status => _window.Dispatcher.BeginInvoke(() =>
            {
                if (_closing) return;
                if (status.State == RuntimeState.Ready && _runtime.Snapshot is { } snapshot) _gateway?.UpdateRuntime(snapshot);
                _window.SetStatus(status.Message, status.State is RuntimeState.Starting or RuntimeState.Recovering,
                    status.State == RuntimeState.Failed);
            });
        }

        public async Task InitializeAsync(bool recover = false)
        {
            await _initializeGate.WaitAsync(_lifetime.Token);
            try
            {
                _window.SetStatus("Запускаем локальные компоненты…", true);
                var snapshot = recover ? await _runtime.RecoverAsync(_lifetime.Token) : await _runtime.StartAsync(_lifetime.Token);
                _window.SetSources(snapshot.Indexers, snapshot.Warnings);
                if (_gateway is null)
                {
                    _gateway = new LocalGateway(_paths, snapshot);
                    await _gateway.StartAsync(_lifetime.Token);
                }
                else _gateway.UpdateRuntime(snapshot);
                _window.SetStatus("Открываем Lampa…", true);
                if (!_browserConfigured)
                {
                    await _window.InitializeBrowserAsync(_paths.BrowserData, _gateway.LoginUri, async browser =>
                    {
                        browser.Settings.AreHostObjectsAllowed = false;
                        browser.Settings.AreDevToolsEnabled = false;
                        browser.Settings.IsWebMessageEnabled = true;
                        browser.WebMessageReceived += OnMessage;
                        browser.PermissionRequested += (_, request) => request.State = CoreWebView2PermissionState.Deny;
                        browser.DownloadStarting += (_, request) => request.Cancel = true;
                        browser.NewWindowRequested += (_, request) =>
                        {
                            request.Handled = true;
                            if (request.IsUserInitiated && Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                                catch (Win32Exception ex) { RecordError("external-browser", ex); }
                        };
                        browser.NavigationStarting += (_, request) =>
                        {
                            if (_gateway is null || !BridgeProtocol.IsTrustedSource(request.Uri, _gateway.Origin)) request.Cancel = true;
                        };
                        browser.ProcessFailed += (_, _) => _window.SetStatus("Интерфейс остановился. Нажмите «Восстановить».", false, true);
                        browser.NavigationCompleted += (_, result) =>
                        {
                            if (!result.IsSuccess) _window.SetStatus("Не удалось открыть интерфейс. Нажмите «Восстановить».", false, true);
                        };
                        _documentScriptId = await browser.AddScriptToExecuteOnDocumentCreatedAsync(BridgeProtocol.DocumentScript(_gateway.Origin, _profile.Load()));
                    });
                    _browserConfigured = true;
                }
                else
                {
                    await SaveCurrentProfileAsync();
                    if (_documentScriptId is not null) _window.Browser.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_documentScriptId);
                    _documentScriptId = await _window.Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BridgeProtocol.DocumentScript(_gateway.Origin, _profile.Load()));
                    _window.Browser.CoreWebView2.Navigate(_gateway.Origin.AbsoluteUri);
                }
            }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception ex)
            {
                RecordError("startup", ex);
                _window.SetStatus("Не удалось подготовить приложение. Проверьте свободное место и нажмите «Восстановить».", false, true);
                _ready.TrySetException(new InvalidOperationException("Application startup failed: " + ex.GetType().Name));
            }
            finally { _initializeGate.Release(); }
        }

        private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs message)
        {
            if (_closing || _gateway is null || !BridgeProtocol.IsTrustedSource(message.Source, _gateway.Origin)) return;
            try
            {
                if (message.WebMessageAsJson.Length > 20 * 1024 * 1024) return;
                using var document = JsonDocument.Parse(message.WebMessageAsJson);
                var root = document.RootElement;
                if (root.GetProperty("version").GetInt32() != BridgeProtocol.Version) return;
                var payload = root.GetProperty("payload");
                switch (root.GetProperty("type").GetString())
                {
                    case "ready":
                        _window.MarkBrowserReady();
                        _window.SetStatus("Готово к просмотру", false);
                        _ready.TrySetResult();
                        break;
                    case "profile":
                        var state = payload.Deserialize<Dictionary<string, string>>();
                        if (state is not null) await _profile.SaveAsync(state, _lifetime.Token);
                        break;
                    case "play":
                        var url = payload.GetProperty("url").GetString() ?? "";
                        var id = payload.GetProperty("sessionId").GetString() ?? "";
                        var title = payload.GetProperty("title").GetString() ?? "Просмотр";
                        var start = payload.GetProperty("startSeconds").GetDouble();
                        if (!Guid.TryParse(id, out _) || title.Length > 512 || !double.IsFinite(start) || start < 0 ||
                            !_gateway.TryCreateMediaUri(url, out var media))
                        {
                            _window.SetStatus("Этот адрес видео не поддерживается встроенным просмотром.", false);
                            break;
                        }
                        if (_testStates.Count < 100) _testStates.Add("native-play-request");
                        _window.Play(new MediaRequest(media, title, Math.Min(start, 864000), id));
                        break;
                    case "error":
                        if (_errors.Count < 100) _errors.Add("bridge-error");
                        _window.SetStatus("Не удалось связать Lampa с локальными компонентами. Нажмите «Восстановить».", false, true);
                        break;
                }
            }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception ex) { RecordError("bridge", ex); }
        }

        private void OnProgress(PlaybackProgress progress)
        {
            _lastProgress = progress;
            if (_testPlayback is not null && (_testStates.Count == 0 || _testStates[^1] != progress.State.ToString()))
                _testStates.Add(progress.State.ToString());
            if (_testPlayback is not null && progress.State == PlaybackState.Playing && progress.PositionSeconds >= 1)
                _testPlayback.TrySetResult(progress);
            Send("progress", ProgressPayload(progress));
        }
        private static object ProgressPayload(PlaybackProgress progress) => new
        {
            sessionId = progress.SessionId, timeSeconds = progress.PositionSeconds,
            durationSeconds = progress.DurationSeconds, state = progress.State.ToString().ToLowerInvariant()
        };
        private void OnPlaybackClosed(string session)
        {
            var last = _lastProgress?.SessionId == session ? _lastProgress : null;
            Send("closed", new { sessionId = session, ended = last?.State == PlaybackState.Ended,
                progress = last is null ? null : ProgressPayload(last) });
        }
        private void Send(string type, object payload)
        {
            if (_window.Browser.CoreWebView2 is not null)
                _window.Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { version = BridgeProtocol.Version, type, payload }));
        }
        private async Task SaveCurrentProfileAsync()
        {
            if (_window.Browser.CoreWebView2 is null || _gateway is null ||
                !BridgeProtocol.IsTrustedSource(_window.Browser.Source?.AbsoluteUri, _gateway.Origin)) return;
            var json = await _window.Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify(Object.fromEntries(Object.keys(localStorage).map(k=>[k,localStorage.getItem(k)])))");
            var value = JsonSerializer.Deserialize<string>(json);
            if (value is not null && JsonSerializer.Deserialize<Dictionary<string, string>>(value) is { } state)
                await _profile.SaveAsync(state);
        }
        private async void OnClosing(object? sender, CancelEventArgs args)
        {
            if (_closing) return;
            args.Cancel = true;
            _window.StopPlayback();
            _closing = true;
            _window.IsEnabled = false;
            _lifetime.Cancel();
            try { await SaveCurrentProfileAsync(); } catch (Exception ex) { RecordError("profile-close", ex); }
            await _initializeGate.WaitAsync();
            try
            {
                if (_gateway is not null) await _gateway.DisposeAsync();
                await _runtime.DisposeAsync();
            }
            catch (Exception ex) { RecordError("shutdown", ex); }
            finally
            {
                _initializeGate.Release();
                // Always post the final close: cleanup may complete synchronously while WPF
                // is still inside the first Closing event, where Close() is not legal.
                _ = _window.Dispatcher.BeginInvoke(() =>
                {
                    _window.Closing -= OnClosing;
                    _window.Close();
                    if (_testFailed) Application.Current.Shutdown(1);
                    _instance?.ReleaseMutex(); _instance?.Dispose(); _instance = null;
                });
            }
        }
        private void RecordError(string category, Exception error)
        {
            var entry = DateTimeOffset.UtcNow.ToString("O") + " " + category + " " + error.GetType().Name + Environment.NewLine + error.StackTrace;
            if (_errors.Count < 100) _errors.Add(entry);
            try
            {
                var file = Path.Combine(_paths.LogsDirectory, "application.log");
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Move(file, file + ".previous", true);
                File.AppendAllText(file, entry + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        public async Task RunSelfTestAsync(string reportPath, string? fixture, string? torrentUrl)
        {
            var checks = new Dictionary<string, object>();
            async Task Checkpoint(string stage)
            {
                checks["stage"] = stage;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            }
            try
            {
                await Checkpoint("initialize");
                await InitializeAsync();
                if (_window.Browser.CoreWebView2 is null)
                {
                    await _ready.Task;
                    throw new InvalidOperationException("Browser initialization did not complete.");
                }
                await Checkpoint("wait-for-lampa");
                await Task.Delay(5000);
                checks["earlyBrowserDiagnostic"] = JsonSerializer.Deserialize<string>(await _window.Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({language:localStorage.getItem('language'),appready:window.appready,errors:window.__lwErrors,viewport:[innerWidth,innerHeight],cards:document.querySelectorAll('.card').length,text:document.body.innerText.slice(0,900)})")) ?? "";
                await Checkpoint("lampa-diagnostic");
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(90));
                checks["lampaBridgeReady"] = true;
                checks["sources"] = _runtime.Snapshot!.Indexers;
                var config = await _window.Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({ parser:Lampa.Storage.get('parser_torrent_type'),torrserver:Lampa.Storage.get('torrserver_url'),jackett:Lampa.Storage.get('jackett_url'),bridge:window.__lampawinOrigin===location.origin,proxy:Lampa.Storage.field('proxy_tmdb'),navigation:Lampa.Storage.field('navigation_type'),catalogComponent:Lampa.Activity.active().component})");
                checks["browserConfiguration"] = JsonSerializer.Deserialize<string>(config) ?? "";
                await _window.Browser.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('lampawin-test-profile','preserved')");
                await SaveCurrentProfileAsync();
                checks["profileEncryptedRoundtrip"] = _profile.Load().GetValueOrDefault("lampawin-test-profile") == "preserved";
                await Checkpoint("lampa-ready");
                await Task.Delay(10000);
                var catalog = await _window.Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({cards:document.querySelectorAll('.card,.card-new,.card--new').length,text:document.body.innerText.slice(0,1000),errors:window.__lwErrors})");
                checks["catalogDiagnostic"] = JsonSerializer.Deserialize<string>(catalog) ?? "";
                if (torrentUrl is not null)
                {
                    var snapshot = _runtime.Snapshot!;
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(snapshot.TorrServerUser + ":" + snapshot.TorrServerPassword)));
                    using var added = await client.PostAsJsonAsync(new Uri(snapshot.TorrServerBaseUri, "torrents"), new { action = "add", link = torrentUrl, title = "LampaWin integration fixture", save_to_db = false });
                    added.EnsureSuccessStatusCode();
                    using var metadata = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
                    var hash = metadata.RootElement.GetProperty("hash").GetString()!;
                    checks["realTorrentAdded"] = hash.Length == 40;
                    var url = new Uri(_gateway!.Origin, "torrserver/stream/fixture.mp4?link=" + hash + "&index=1&play").AbsoluteUri;
                    _testPlayback = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var parameters = JsonSerializer.Serialize(new { url, title = "Проверка торрента", torrent_hash = hash });
                    await _window.Browser.CoreWebView2.ExecuteScriptAsync("(()=>{const data=" + parameters + ";data.timeline={time:0,handler:(percent,time,duration)=>localStorage.setItem('lampawin-native-progress',JSON.stringify({percent,time,duration}))};Lampa.Player.play(data);})()");
                    var progress = await _testPlayback.Task.WaitAsync(TimeSpan.FromSeconds(45));
                    checks["realTorrentVlcPlaybackSeconds"] = progress.PositionSeconds;
                    checks["realTorrentDurationSeconds"] = progress.DurationSeconds;
                    await Task.Delay(600);
                    var saved = await _window.Browser.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('lampawin-native-progress')");
                    checks["nativeTimelineReturnedToLampa"] = JsonSerializer.Deserialize<string>(saved)?.Contains("duration") == true;
                }
                else if (fixture is not null)
                {
                    _testPlayback = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var fixtureUri = new Uri(Path.GetFullPath(fixture));
                    _window.Play(new MediaRequest(fixtureUri, "Проверка встроенного VLC", 0, Guid.NewGuid().ToString()));
                    var progress = await _testPlayback.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    checks["libVlcPlaybackSeconds"] = progress.PositionSeconds;
                    checks["libVlcDurationSeconds"] = progress.DurationSeconds;
                }
                checks["success"] = _errors.Count == 0;
                if (checks.Values.Any(value => value is bool passed && !passed)) checks["success"] = false;
            }
            catch (Exception ex)
            {
                checks["success"] = false;
                checks["failure"] = ex.GetType().Name + ": " + ex.Message;
                if (_window.Browser.CoreWebView2 is not null)
                {
                    var diagnostic = await _window.Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({path:location.pathname,origin:location.origin,bridgeOrigin:window.__lampawinOrigin,appready:window.appready,showApp:window.show_app,lampa:!!window.Lampa,text:document.body.innerText.slice(0,1600),scripts:Array.from(document.scripts).map(s=>s.src.split('?')[0])})");
                    checks["browserDiagnostic"] = JsonSerializer.Deserialize<string>(diagnostic) ?? "";
                }
            }
            checks["errors"] = _errors.ToArray();
            checks["playbackStates"] = _testStates.ToArray();
            checks["playbackDiagnostics"] = _testDiagnostics.ToArray();
            _testFailed = checks["success"] is not true;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            _window.Close();
        }
    }
}
