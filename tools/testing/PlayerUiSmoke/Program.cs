using System.IO;
using System.Reflection;
using System.Xml.Linq;
using System.Windows.Markup;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using LampaWin.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new SmokeApp(args);
        return app.Run();
    }
}

// Reuses actual application resources and MainWindow without starting the catalog or servers.
internal sealed class SmokeApp(string[] args) : Application
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    private Uri FixtureUri => Uri.TryCreate(args[0], UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
        ? url : new Uri(Path.GetFullPath(args[0]));
    private readonly List<string> _checks = [];
    private MainWindow _window = null!;
    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _checks.Add(name);
    }
    private T Element<T>(string name) where T : class => (T)_window.FindName(name);
    private void Call(string method, params object[] values) => typeof(MainWindow)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, values);
    private void Click(string name) => Element<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var report = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        try
        {
            await RunUpdateConfirmationAsync(Path.GetDirectoryName(report)!);
            if (args.Contains("--update-confirmation"))
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new { success = true, checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
            var source = XDocument.Load(Path.Combine(root, "src/LampaWin.Desktop/App.xaml"));
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources = new XElement(ns + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                source.Root!.Element(ns + "Application.Resources")!.Elements());
            Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            _window = new MainWindow { ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
            MainWindow = _window;
            _window.Show();
            if (args.Contains("--food-write") || args.Contains("--food-read") || args.Contains("--food-sites"))
            {
                await RunFoodProfileAsync(report);
                _window.Close(); Shutdown(0); return;
            }
            if (args.Contains("--preview-speed"))
            {
                await RunPreviewSpeedAsync(report);
                _window.Close();
                Shutdown(0);
                return;
            }
            if (args.Contains("--delayed-stream"))
            {
                await RunDelayedStreamAsync(report);
                _window.Close();
                Shutdown(0);
                return;
            }
            if (args.Contains("--external-video"))
            {
                await RunExternalVideoAsync(report);
                _window.Close();
                Shutdown(0);
                return;
            }
            var played = new TaskCompletionSource();
            _window.PlaybackChanged += p => { if (p.State == PlaybackState.Playing && p.PositionSeconds > .5) played.TrySetResult(); };
            _window.Play(new MediaRequest(FixtureUri, "Тестовый фильм · Встроенный VLC", 0, Guid.NewGuid().ToString()));
            await played.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var overlay = Element<Grid>("PlayerOverlay");
            var foregroundWindow = Window.GetWindow(overlay)!;
            Check(Window.GetWindow(overlay) is { } host && host != _window, "controls hosted above native VLC in dedicated foreground window");
            var bottom = Element<Border>("PlayerBottomBar");
            Click("FoodButton");
            await Task.Delay(150);
            var foodHost = Element<Border>("FoodPanelHost");
            Check(foodHost.IsVisible && foodHost.ActualWidth >= 360 && foodHost.ActualWidth <= 460 && Window.GetWindow(foodHost) == foregroundWindow,
                "food chooser is a compact right-side panel inside actual video foreground, not a separate window");
            Check(Element<Button>("FoodButton").TranslatePoint(new Point(), overlay).X < Element<Button>("PlayerSettingsButton").TranslatePoint(new Point(), overlay).X,
                "burger button is left of player settings");
            var foodBitmap = new RenderTargetBitmap((int)foodHost.ActualWidth, (int)foodHost.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var foodVisual = new DrawingVisual();
            using (var drawing = foodVisual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(foodHost), null, new Rect(0, 0, foodHost.ActualWidth, foodHost.ActualHeight));
            foodBitmap.Render(foodVisual);
            var foodPng = new PngBitmapEncoder(); foodPng.Frames.Add(BitmapFrame.Create(foodBitmap));
            using (var imageFile = File.Create(Path.ChangeExtension(report, ".food.png"))) foodPng.Save(imageFile);
            Call("HidePlayerControls");
            await Task.Delay(250);
            Check(bottom.Opacity > .99, "food panel keeps player controls accessible");
            var foodPanel = (FoodPanel)foodHost.Child;
            var expandButton = (Button)typeof(FoodPanel).GetField("_expand", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(foodPanel)!;
            expandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            Check(foodHost.ActualWidth > 460 && foodHost.ActualWidth <= overlay.ActualWidth - foodHost.Margin.Right
                && Window.GetWindow(foodHost) == foregroundWindow, "food panel expands within video without creating another window");
            expandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            Check(foodHost.ActualWidth >= 360 && foodHost.ActualWidth <= 460, "food panel returns to compact width");
            Click("FoodButton");
            var actualPlayer = (PlayerController)typeof(MainWindow).GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_window)!;
            Check(!foodHost.IsVisible && actualPlayer.MediaPlayer.IsPlaying, "closing food panel leaves film playing");
            Check(bottom.IsVisible && bottom.ActualWidth > 500, "overlay controls laid out over playing video");
            Check(overlay.InputHitTest(new Point(overlay.ActualWidth / 2, overlay.ActualHeight / 2)) == Element<Border>("VideoClickSurface"), "video surface receives mouse input above native HWND");
            Call("HidePlayerControls");
            await Task.Delay(250);
            Check(bottom.Opacity < .01 && !bottom.IsHitTestVisible, "controls fade out while playing");
            Call("ShowPlayerControls");
            await Task.Delay(250);
            Check(bottom.Opacity > .99 && bottom.IsHitTestVisible, "controls reappear and accept input");
            Click("PlayPauseButton");
            await Task.Delay(250);
            Call("HidePlayerControls");
            await Task.Delay(250);
            Check(bottom.Opacity > .99, "paused playback keeps controls visible");
            Call("PositionSlider_MouseDown", Element<Slider>("PositionSlider"), new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Call("SetSeekPointerPosition", Element<Slider>("PositionSlider").ActualWidth * .75);
            Check(Element<Slider>("PositionSlider").Value > 740 && Element<Slider>("PositionSlider").Value < 760,
                "pointer click computes the new slider position before the seek commits");
            Element<Slider>("PositionSlider").Value = 500;
            Call("CommitSeek");
            Check(Math.Abs(Element<Slider>("PositionSlider").Value - 500) < .1
                && Element<StackPanel>("SeekBufferingPanel").IsVisible,
                "seek commits the requested position immediately and shows buffering separately");
            await Task.Delay(300);
            Check(Element<TextBlock>("TimeLabel").Text.Contains("00:06"), "timeline drag seeks native player to midpoint");
            var session = "stalled-ui-fixture";
            Call("SeekToFraction", .5d);
            typeof(MainWindow).GetField("_pendingSeekSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, 6d);
            typeof(MainWindow).GetField("_pendingSeekStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, DateTimeOffset.UtcNow.AddSeconds(-25));
            Call("OnPlaybackChanged", new PlaybackProgress(session, 0, 12, PlaybackState.Opening, true));
            Check(Math.Abs(Element<Slider>("PositionSlider").Value - 500) < .1
                && Element<TextBlock>("SeekBufferingText").Text.Contains("ещё"),
                "stalled progress after 25 seconds keeps requested position and reports continued loading");
            Call("OnPlaybackChanged", new PlaybackProgress(session, 6, 12, PlaybackState.Playing, true));
            Check(Element<StackPanel>("SeekBufferingPanel").IsVisible,
                "native target timestamp while still buffering does not report a completed seek");
            Call("OnPlaybackChanged", new PlaybackProgress(session, 6, 12, PlaybackState.Playing));
            Check(Element<StackPanel>("SeekBufferingPanel").IsVisible,
                "a single optimistic VLC timestamp cannot acknowledge a seek");
            Call("OnPlaybackChanged", new PlaybackProgress(session, 0, 12, PlaybackState.Playing));
            Check(Math.Abs(Element<Slider>("PositionSlider").Value - 500) < .1,
                "old progress after an optimistic target timestamp cannot return the slider to its origin");
            Call("OnPlaybackChanged", new PlaybackProgress(session, 6.3, 12, PlaybackState.Playing));
            typeof(MainWindow).GetField("_seekFirstReadyAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_window, (DateTimeOffset?)DateTimeOffset.UtcNow.AddSeconds(-1));
            Call("OnPlaybackChanged", new PlaybackProgress(session, 6.4, 12, PlaybackState.Playing));
            Check(!Element<StackPanel>("SeekBufferingPanel").IsVisible,
                "seek buffering clears only after stable advancing progress at the target");
            PlaybackProgress? forwarded = null;
            Action<PlaybackProgress> captureForwarded = progress => forwarded = progress;
            _window.PlaybackChanged += captureForwarded;
            Call("OnPlaybackChanged", new PlaybackProgress(session, 0, 12, PlaybackState.Playing));
            Check(Element<Slider>("PositionSlider").Value >= 500,
                "queued pre-seek timestamps remain fenced out after acknowledgement");
            Check(forwarded is { PositionSeconds: >= 6, IsBuffering: true },
                "stale timestamps cannot roll back the progress forwarded to Lampa history");
            _window.PlaybackChanged -= captureForwarded;
            Call("SeekToFraction", .1d);
            Call("OnPlaybackChanged", new PlaybackProgress(session, 1.5, 12, PlaybackState.Playing));
            typeof(MainWindow).GetField("_seekFirstReadyAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_window, (DateTimeOffset?)DateTimeOffset.UtcNow.AddSeconds(-1));
            Call("OnPlaybackChanged", new PlaybackProgress(session, 1.6, 12, PlaybackState.Playing));
            Call("OnPlaybackChanged", new PlaybackProgress(session, 6, 12, PlaybackState.Playing));
            Check(Element<Slider>("PositionSlider").Value < 200,
                "backward seeks also reject queued timestamps from the previous later position");
            Element<Border>("SeekPreview").Visibility = Visibility.Visible;
            typeof(MainWindow).GetField("_previewFraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, .5d);
            var previewTask = (Task)typeof(MainWindow).GetMethod("LoadSeekPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_window, new object[] { .5d })!;
            await previewTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Element<Image>("SeekPreviewImage").Source is not null,
                "timeline hover can render an independently captured target frame");
            Element<Border>("SeekPreview").Visibility = Visibility.Collapsed;
            var volume = Element<Slider>("VolumeSlider");
            volume.Value = 37;
            Click("MuteButton"); Check(volume.Value == 0, "mute button mutes");
            Click("MuteButton"); Check(volume.Value == 37, "unmute restores previous volume");
            var audioPosition = actualPlayer.MediaPlayer.Time;
            var audioState = actualPlayer.MediaPlayer.State;
            Click("RestoreAudioButton");
            await Task.Delay(200);
            Check(actualPlayer.MediaPlayer.Volume == 37 && actualPlayer.MediaPlayer.State == audioState
                && Math.Abs(actualPlayer.MediaPlayer.Time - audioPosition) < 1000,
                "restore audio button keeps volume, player state and timeline");
            Click("PlayerSettingsButton");
            Check(Element<Border>("PlayerSettingsPanel").IsVisible, "settings menu opens over native video");
            Element<ComboBox>("AudioTracks").IsDropDownOpen = true;
            await Task.Delay(150);
            Check(Element<ComboBox>("AudioTracks").IsDropDownOpen, "audio picker opens inside foreground overlay");
            Element<ComboBox>("AudioTracks").IsDropDownOpen = false;
            Check(!Equals(Element<ComboBox>("AudioTracks").SelectedItem?.ToString(), "Отключено"), "audio picker reflects active native audio track");
            foreach (var width in new[] { 1440d, 920d })
            {
                _window.Width = width;
                _window.UpdateLayout();
                await Task.Delay(200);
                var left = Element<Button>("PlayPauseButton").TranslatePoint(new Point(), overlay);
                var right = Element<Button>("FullscreenButton").TranslatePoint(new Point(44,44), overlay);
                Check(left.X >= 0 && right.X <= overlay.ActualWidth + 1, $"toolbar fits {width}px window");
                var bitmap = new RenderTargetBitmap((int)overlay.ActualWidth, (int)overlay.ActualHeight, 96,96,PixelFormats.Pbgra32);
                bitmap.Render(overlay);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(Path.GetDirectoryName(report)!, $"player-overlay-{width}.png"));
                encoder.Save(file);
            }
            Click("PlayerSettingsButton");
            _window.Hide();
            Click("FullscreenButton");
            Check(_window.WindowStyle == WindowStyle.None && _window.WindowState == WindowState.Normal && _window.Topmost,
                "fullscreen uses a topmost borderless monitor-sized window instead of the taskbar work area");
            var nativeHandle = new WindowInteropHelper(_window).Handle;
            var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            Check(GetMonitorInfo(MonitorFromWindow(nativeHandle, 2), ref monitorInfo)
                && GetWindowRect(nativeHandle, out var nativeBounds)
                && nativeBounds.Left == monitorInfo.Monitor.Left && nativeBounds.Top == monitorInfo.Monitor.Top
                && nativeBounds.Right == monitorInfo.Monitor.Right && nativeBounds.Bottom == monitorInfo.Monitor.Bottom,
                "fullscreen HWND covers exact physical monitor bounds including the taskbar area");
            Click("FullscreenButton");
            Check(_window.WindowStyle != WindowStyle.None && _window.WindowState == WindowState.Normal && !_window.Topmost,
                "fullscreen exit restores window state and topmost mode");
            _window.Left = -30000; _window.Top = -30000;
            _window.Show();
            Call("Home_Click", _window, new RoutedEventArgs());
            Check(!Element<Grid>("PlayerPage").IsVisible && Element<Border>("WindowFooter").IsVisible, "return to catalog restores layout");
            await Task.Delay(250);
            Check(!bottom.IsVisible && !foregroundWindow.IsVisible, "return to catalog hides actual VLC foreground window and controls");
            var ended = new TaskCompletionSource();
            _window.PlaybackClosed += _ => ended.TrySetResult();
            _window.Play(new MediaRequest(FixtureUri, "Завершение серии", 0, Guid.NewGuid().ToString()));
            await Task.Delay(1500);
            foregroundWindow = Window.GetWindow(overlay)!;
            Call("PositionSlider_MouseDown", Element<Slider>("PositionSlider"), new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Element<Slider>("PositionSlider").Value = 950;
            Call("CommitSeek");
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(300);
            Check(!Element<Grid>("PlayerPage").IsVisible && Element<Grid>("BrowserPage").IsVisible, "natural completion returns to episode selection page");
            Check(!bottom.IsVisible && !foregroundWindow.IsVisible, "natural completion hides actual VLC foreground window and controls");
            var offerSession = Guid.NewGuid().ToString();
            var offeredEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _window.PlaybackClosed += sessionId => { if (sessionId == offerSession) offeredEnd.TrySetResult(); };
            _window.Play(new MediaRequest(FixtureUri, "Серия с продолжением", 0, offerSession));
            _window.SetNextEpisode(offerSession, "Серия 2");
            await Task.Delay(700);
            actualPlayer.SeekTo(.95);
            await offeredEnd.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(100);
            Check(Element<Border>("NextEpisodePanel").IsVisible && Element<TextBlock>("NextEpisodeText").Text.Contains("Серия 2"), "next episode offer is visible in the actual VLC foreground");
            string? requestedEpisode = null;
            _window.NextEpisodeRequested += sessionId => requestedEpisode = sessionId;
            Click("NextEpisodeButton");
            Check(requestedEpisode == offerSession && !Element<Border>("NextEpisodePanel").IsVisible, "next button sends exactly the completed session and dismisses offer");
            actualPlayer.PreviewEnabled = false;
            Check(await actualPlayer.CapturePreviewAsync(.5, CancellationToken.None) is null, "disabled preview opens no background decoder");
            _window.PrepareClose();
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = true, checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
            _window.Close(); Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = false, failure = ex.ToString(), checks = _checks,
                audioChoices = _window is null ? null : Element<ComboBox>("AudioTracks").Items.Cast<object>().Select(x => x.ToString()).ToArray(),
                settingsBounds = _window is null ? null : new[] { Element<Border>("PlayerSettingsPanel").ActualWidth, Element<Border>("PlayerSettingsPanel").ActualHeight },
                overlayBounds = _window is null ? null : new[] { Element<Grid>("PlayerOverlay").ActualWidth, Element<Grid>("PlayerOverlay").ActualHeight }
            }, new JsonSerializerOptions { WriteIndented = true }));
            _window?.Close(); Shutdown(1);
        }
    }

    private async Task RunUpdateConfirmationAsync(string reportDirectory)
    {
        var updateType = typeof(MainWindow).Assembly.GetType("LampaWin.Desktop.DesktopUpdater")!;
        var confirm = updateType.GetMethod("ConfirmSuccessfulStartupAsync", BindingFlags.NonPublic | BindingFlags.Static, [typeof(string)])!;
        var current = typeof(MainWindow).Assembly.GetName().Version!;
        var versions = new[] { $"{current.Major}.{current.Minor}.{current.Build}", current.ToString(), new Version(current.Major, current.Minor, current.Build, Math.Max(current.Revision, 0) + 1).ToString() };
        for (var index = 0; index < versions.Length; index++)
        {
            var scratch = Path.GetFullPath(Path.Combine(reportDirectory, "update-confirmation-" + Guid.NewGuid().ToString("N")));
            if (!scratch.StartsWith(Path.GetFullPath(reportDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Update check path escaped report directory.");
            var run = "run-" + Guid.NewGuid().ToString("N");
            var backup = Path.Combine(scratch, run, "backup");
            Directory.CreateDirectory(backup);
            await File.WriteAllTextAsync(Path.Combine(backup, "previous-program.txt"), "previous installation");
            var result = Path.Combine(scratch, "result.json");
            await File.WriteAllTextAsync(result, JsonSerializer.Serialize(new { status = "installed", version = versions[index], run }));
            try
            {
                await (Task)confirm.Invoke(null, [scratch])!;
                if (index < 2)
                {
                    using var completed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scratch, "last-result.json")));
                    Check(!Directory.Exists(backup) && !File.Exists(result) && completed.RootElement.GetProperty("status").GetString() == "success", index == 0
                        ? "three-part release confirms four-part assembly version and removes update backup"
                        : "four-part release confirms startup and removes update backup");
                }
                else Check(Directory.Exists(backup) && File.Exists(result), "different release revision preserves pending result and update backup");
            }
            finally { Directory.Delete(scratch, true); }
        }
    }

    private async Task RunDelayedStreamAsync(string report)
    {
        await using var source = await DelayedStreamFixture.StartAsync(Path.GetFullPath(args[0]));
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaybackProgress? latest = null;
        _window.PlaybackChanged += update =>
        {
            latest = update;
            if (update.State == PlaybackState.Playing && update.PositionSeconds > .5) playing.TrySetResult();
        };
        _window.Play(new MediaRequest(source.Url, "Проверка задержанного участка", 0, "delayed-stream"));
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(latest!.DurationSeconds > 100, "real delayed HTTP stream opens with a long seekable timeline");
        Call("SeekToFraction", .5d);
        Check(Math.Abs(Element<Slider>("PositionSlider").Value - 500) < .1,
            "real stream seek moves slider immediately before requested data arrives");
        await Task.Delay(2000);
        Call("SeekToFraction", .75d);
        await Task.Delay(TimeSpan.FromSeconds(25));
        Check(source.BlockedRequests > 0, "HTTP fixture actually held video bytes behind a closed gate");
        Check(Math.Abs(Element<Slider>("PositionSlider").Value - 750) < .1
            && Element<StackPanel>("SeekBufferingPanel").IsVisible
            && Element<TextBlock>("SeekBufferingText").Text.Contains("ещё"),
            "latest seek target survives 25 seconds of real network buffering");
        source.Release();
        var ready = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < ready && (latest!.IsBuffering || latest.PositionSeconds < latest.DurationSeconds * .74))
            await Task.Delay(100);
        Check(latest!.PositionSeconds >= latest.DurationSeconds * .74 && !latest.IsBuffering,
            "real playback resumes at latest requested target after bytes are released");
        Check(!Element<StackPanel>("SeekBufferingPanel").IsVisible,
            "real network buffering indicator clears after target becomes playable");
        await using var previewSource = await DelayedStreamFixture.StartAsync(Path.GetFullPath(args[0]));
        _window.Play(new MediaRequest(previewSource.Url, "Отмена кадра-превью", 0, "cancel-preview"));
        var previewReady = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < previewReady && (latest!.SessionId != "cancel-preview" || latest.PositionSeconds < .5))
            await Task.Delay(100);
        Check(latest!.SessionId == "cancel-preview" && latest.PositionSeconds >= .5,
            "second controlled source starts before testing preview cancellation");
        Element<Border>("SeekPreview").Visibility = Visibility.Visible;
        typeof(MainWindow).GetField("_previewFraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, .75d);
        var previewTask = (Task)typeof(MainWindow).GetMethod("LoadSeekPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_window, new object[] { .75d })!;
        await Task.Delay(1000);
        Check(!previewTask.IsCompleted, "preview really waits for an unavailable HTTP video segment");
        _window.Play(new MediaRequest(new Uri(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[0])!, "fixture.mp4"))),
            "Следующий фильм", 0, "replacement-video"));
        await previewTask.WaitAsync(TimeSpan.FromSeconds(10));
        Check(Element<Image>("SeekPreviewImage").Source is null && !Element<Border>("SeekPreview").IsVisible,
            "switching films cancels pending preview and clears the previous image");
        var replacementReady = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < replacementReady && (latest!.SessionId != "replacement-video" || latest.PositionSeconds < .5))
            await Task.Delay(100);
        Check(latest!.SessionId == "replacement-video" && latest.PositionSeconds >= .5,
            "replacement video plays after cancellation of blocked preview");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = true,
            blockedRequests = source.BlockedRequests, checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task RunFoodProfileAsync(string report)
    {
        _window.Play(new MediaRequest(FixtureUri, "Food panel fixture", 0, Guid.NewGuid().ToString()));
        await Task.Delay(500);
        Click("PlayPauseButton");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
        var profile = Path.Combine(root, ".cache/food-profile-smoke/burgerking");
        var panel = new FoodPanel(Path.GetDirectoryName(profile)!, _ => throw new InvalidOperationException("No external browser expected"));
        var host = Element<Border>("FoodPanelHost"); host.Child = panel; host.Visibility = Visibility.Visible;
        if (args.Contains("--food-sites"))
        {
            var observations = new List<object>();
            foreach (var service in LampaWin.Core.FoodServices.All)
            {
                await panel.SelectServiceAsync(service).WaitAsync(TimeSpan.FromSeconds(25));
                var browsers = (Dictionary<string, Microsoft.Web.WebView2.Wpf.WebView2CompositionControl>)typeof(FoodPanel).GetField("_browsers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
                var site = browsers[service.Id].CoreWebView2;
                await Task.Delay(12000);
                var page = JsonSerializer.Deserialize<JsonElement>(await site.ExecuteScriptAsync("({title:document.title,ready:document.readyState,bodyLength:document.body?.innerText.length,inputs:document.querySelectorAll('input').length,width:innerWidth,screenWidth:screen.width,scrollWidth:document.documentElement.scrollWidth,viewport:document.querySelector('meta[name=viewport]')?.content,ua:navigator.userAgent,mobile:navigator.userAgentData?.mobile})"));
                Check(page.GetProperty("ready").GetString() == "complete" && page.GetProperty("bodyLength").GetInt32() > 1000,
                    $"{service.Title} loaded its real service interface");
                Check(page.GetProperty("scrollWidth").GetInt32() <= page.GetProperty("width").GetInt32() + 1,
                    $"{service.Title} fits the compact panel without document overflow");
                observations.Add(new { service = service.Title, page });
                File.WriteAllText(report, JsonSerializer.Serialize(new { observations }, new JsonSerializerOptions { WriteIndented = true }));
                using var screenshot = File.Create(Path.ChangeExtension(report, $".{service.Id}.png"));
                await site.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, screenshot).WaitAsync(TimeSpan.FromSeconds(15));
                foreach (var width in new[] { 360d, 460d, 900d })
                {
                    host.Width = width;
                    await Task.Delay(500);
                    var layout = JsonSerializer.Deserialize<JsonElement>(await site.ExecuteScriptAsync("({width:innerWidth,scrollWidth:document.documentElement.scrollWidth,screenWidth:screen.width})"));
                    Check(layout.GetProperty("scrollWidth").GetDouble() <= layout.GetProperty("width").GetDouble() + 1
                        && Math.Abs(layout.GetProperty("width").GetDouble() - browsers[service.Id].ActualWidth) <= 1,
                        $"{service.Title} fits after resize to {width}px");
                }
                host.Width = 460;
            }
            panel.Dispose();
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = true, observations, checks = _checks, authenticatedCheckoutTested = false }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        var sites = (Grid)typeof(FoodPanel).GetField("_sites", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
        sites.Visibility = Visibility.Visible;
        var view = new Microsoft.Web.WebView2.Wpf.WebView2CompositionControl(); sites.Children.Add(view);
        ((StackPanel)typeof(FoodPanel).GetField("_picker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!).Visibility = Visibility.Collapsed;
        var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, profile);
        await ((Task)typeof(FoodPanel).GetMethod("InitializeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, [view, environment, true])!).WaitAsync(TimeSpan.FromSeconds(25));
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("food-test.example", Path.Combine(root, "tools/testing/food-fixture"), Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);
        var loaded = new TaskCompletionSource(); core.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); };
        core.Navigate("https://food-test.example/index.html");
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(1000);
        var webBitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var webVisual = new DrawingVisual();
        using (var drawing = webVisual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, view.ActualWidth, view.ActualHeight));
        webBitmap.Render(webVisual);
        var centerPixel = new byte[4];
        webBitmap.CopyPixels(new Int32Rect(webBitmap.PixelWidth / 2, webBitmap.PixelHeight / 2, 1, 1), centerPixel, 4, 0);
        Check(centerPixel[1] > 120 && centerPixel[2] < 80 && centerPixel[0] < 140, "web page pixels render inside transparent VLC foreground, not just an invisible DOM");
        foreach (var width in new[] { 360d, 460d, 900d, 360d })
        {
            host.Width = width;
            await Task.Delay(400);
            var layout = JsonSerializer.Deserialize<JsonElement>(await core.ExecuteScriptAsync("({width:innerWidth,screen:screen.width,scroll:document.documentElement.scrollWidth,mobile:navigator.userAgentData.mobile,input:document.querySelector('input').getBoundingClientRect().right})"));
            Check(Math.Abs(layout.GetProperty("width").GetDouble() - view.ActualWidth) <= 1
                && Math.Abs(layout.GetProperty("screen").GetDouble() - view.ActualWidth) <= 1
                && layout.GetProperty("scroll").GetDouble() <= layout.GetProperty("width").GetDouble() + 1
                && layout.GetProperty("input").GetDouble() <= layout.GetProperty("width").GetDouble()
                && layout.GetProperty("mobile").GetBoolean(), $"mobile viewport and address field fit after resize to {width}px without reload");
        }
        if (args.Contains("--food-write"))
        {
            await core.ExecuteScriptAsync("localStorage.setItem('food-smoke','persisted'); document.cookie='foodSmoke=persisted; max-age=86400; secure; samesite=lax; path=/';");
            Check(true, "dummy food session stored in isolated persistent profile");
        }
        else
        {
            var state = await core.ExecuteScriptAsync("JSON.stringify({storage:localStorage.getItem('food-smoke'),cookie:document.cookie})");
            Check(state.Contains("persisted") && state.Contains("foodSmoke="), "food cookie and localStorage survived a full process restart");
        }
        Check(!core.Settings.AreHostObjectsAllowed && !core.Settings.IsWebMessageEnabled && !core.Settings.IsPasswordAutosaveEnabled && core.IsMuted,
            "food browser has no catalog bridge, no password autosave, and no interfering audio");
        var blocked = new TaskCompletionSource(); core.NavigationStarting += (_, e) => { if (e.Uri.StartsWith("http://127.0.0.1")) blocked.TrySetResult(); };
        core.Navigate("http://127.0.0.1:8090/"); await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Check(core.Source.StartsWith("https://food-test.example"), "food browser blocked navigation to local TorrServer");
        await core.ExecuteScriptAsync("window.open('https://food-test.example/index.html','foodAuthTest')");
        var popupViews = (List<Microsoft.Web.WebView2.Wpf.WebView2CompositionControl>)typeof(FoodPanel).GetField("_popups", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
        for (var attempt = 0; attempt < 50 && (popupViews.Count == 0 || popupViews[0].CoreWebView2 is null); attempt++) await Task.Delay(100);
        Check(popupViews.Count == 1 && popupViews[0].CoreWebView2 is not null && Window.GetWindow(popupViews[0]) == Window.GetWindow(host), "auth popup remains inside the food panel");
        for (var attempt = 0; attempt < 50 && !popupViews[0].CoreWebView2.Source.StartsWith("https://food-test.example"); attempt++) await Task.Delay(100);
        // Virtual host mappings belong to a CoreWebView2, not its shared profile.
        popupViews[0].CoreWebView2.SetVirtualHostNameToFolderMapping("food-test.example", Path.Combine(root, "tools/testing/food-fixture"), Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);
        var popupLoaded = new TaskCompletionSource();
        popupViews[0].CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) popupLoaded.TrySetResult(); };
        popupViews[0].CoreWebView2.Navigate("https://food-test.example/index.html");
        await popupLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        var popupLayout = JsonSerializer.Deserialize<JsonElement>(await popupViews[0].CoreWebView2.ExecuteScriptAsync("({width:innerWidth,screen:screen.width,mobile:navigator.userAgentData.mobile})"));
        Check(Math.Abs(popupLayout.GetProperty("width").GetDouble() - popupViews[0].ActualWidth) <= 1
            && Math.Abs(popupLayout.GetProperty("screen").GetDouble() - popupViews[0].ActualWidth) <= 1,
            "embedded auth popup fits the compact viewport");
        Check(view.CoreWebView2 is not null, "original browser still alive while popup is open");
        await popupViews[0].CoreWebView2.ExecuteScriptAsync("window.close()");
        await Task.Delay(300);
        Check(popupViews.Count == 0 && view.Visibility == Visibility.Visible, "closing embedded auth popup restores its original service browser");
        core = view.CoreWebView2!;
        Check(core is not null && Window.GetWindow(view)!.IsVisible, "closing auth popup preserves the original browser and video foreground");
        await core!.ExecuteScriptAsync("window.geoResult='pending'; navigator.geolocation.getCurrentPosition(()=>window.geoResult='allowed',()=>window.geoResult='denied')");
        var permissionPrompt = (StackPanel)typeof(FoodPanel).GetField("_permissionPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
        for (var attempt = 0; attempt < 50 && permissionPrompt.Visibility != Visibility.Visible; attempt++) await Task.Delay(100);
        Check(permissionPrompt.IsVisible, "geolocation request appears inside WPF food panel without a native browser bubble");
        ((Button)((StackPanel)permissionPrompt.Children[1]).Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(300);
        Check(permissionPrompt.Visibility == Visibility.Collapsed && (await core.ExecuteScriptAsync("window.geoResult")).Contains("denied"),
            "declining geolocation completes the browser request and restores the page");
        view.Dispose(); panel.Dispose();
        File.WriteAllText(report, JsonSerializer.Serialize(new { success = true, checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task RunPreviewSpeedAsync(string report)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.PlaybackChanged += update => { if (update.State == PlaybackState.Playing && update.DurationSeconds > 0) ready.TrySetResult(); };
        _window.Play(new MediaRequest(FixtureUri, "Preview latency test", 0, "preview-speed"));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Click("PlayPauseButton");
        Element<Border>("SeekPreview").Visibility = Visibility.Visible;
        var timings = new List<double>();
        var images = new List<BitmapSource>();
        foreach (var fraction in new[] { .2d, .4d, .6d, .3d, .6d })
        {
            typeof(MainWindow).GetField("_previewFraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, fraction);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var task = (Task)typeof(MainWindow).GetMethod("LoadSeekPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_window, new object[] { fraction })!;
            await task.WaitAsync(TimeSpan.FromSeconds(10));
            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
            Check(Element<Image>("SeekPreviewImage").Source is BitmapSource, $"target {fraction:P0} returns a frame");
            images.Add((BitmapSource)Element<Image>("SeekPreviewImage").Source);
        }
        Check(timings.Skip(1).Take(3).All(milliseconds => milliseconds < 750), "warm uncached target frames appear within 750ms on the local fixture");
        Check(timings[^1] < 50 && ReferenceEquals(images[2], images[4]), "cached hover returns the exact image within 50ms without decoding");
        var hashes = images.Take(4).Select(bitmap =>
        {
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
        }).ToArray();
        Check(hashes.Distinct().Count() >= 3, "seeks produce distinct decoded frames rather than stale snapshots");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = true, milliseconds = timings, frameHashes = hashes, checks = _checks },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task RunExternalVideoAsync(string report)
    {
        PlaybackProgress? latest = null;
        _window.PlaybackChanged += update => latest = update;
        _window.Play(new MediaRequest(FixtureUri, "Внешний торрент — Big Buck Bunny", 0, "external-video"));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline && (latest is null || latest.State != PlaybackState.Playing || latest.PositionSeconds < 1))
            await Task.Delay(100);
        Check(latest is { State: PlaybackState.Playing, PositionSeconds: >= 1, DurationSeconds: > 60 },
            "actual external torrent opens and advances in native VLC");
        var duration = latest!.DurationSeconds;
        Call("SeekToFraction", .5d);
        Check(Math.Abs(Element<Slider>("PositionSlider").Value - 500) < .1,
            "external torrent seek updates slider immediately");
        deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline && (latest.IsBuffering || Math.Abs(latest.PositionSeconds - duration * .5) > 5))
            await Task.Delay(100);
        Check(!latest.IsBuffering && Math.Abs(latest.PositionSeconds - duration * .5) <= 5,
            "external torrent resumes at requested midpoint");
        Element<Border>("SeekPreview").Visibility = Visibility.Visible;
        typeof(MainWindow).GetField("_previewFraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, .5d);
        var preview = (Task)typeof(MainWindow).GetMethod("LoadSeekPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_window, new object[] { .5d })!;
        await preview.WaitAsync(TimeSpan.FromSeconds(12));
        Check(Element<Image>("SeekPreviewImage").Source is BitmapSource,
            "external torrent target frame appears above the timeline");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create((BitmapSource)Element<Image>("SeekPreviewImage").Source));
        using (var image = File.Create(Path.Combine(Path.GetDirectoryName(report)!, "external-torrent-preview.png"))) encoder.Save(image);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { success = true, durationSeconds = duration,
            checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
