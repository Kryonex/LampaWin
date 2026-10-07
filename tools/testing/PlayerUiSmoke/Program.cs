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
        var report = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        try
        {
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
            Call("PositionSlider_MouseDown", Element<Slider>("PositionSlider"), new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            Element<Slider>("PositionSlider").Value = 500;
            Call("CommitSeek");
            Check(Math.Abs(Element<Slider>("PositionSlider").Value - 500) < .1
                && Element<StackPanel>("SeekBufferingPanel").IsVisible,
                "seek commits the requested position immediately and shows buffering separately");
            await Task.Delay(300);
            Check(Element<TextBlock>("TimeLabel").Text.Contains("00:06"), "timeline drag seeks native player to midpoint");
            var session = "stalled-ui-fixture";
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
            Check(!Element<StackPanel>("SeekBufferingPanel").IsVisible,
                "seek buffering clears after target position becomes ready");
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
            Call("PositionSlider_MouseDown", Element<Slider>("PositionSlider"), new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            Element<Slider>("PositionSlider").Value = 950;
            Call("CommitSeek");
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(300);
            Check(!Element<Grid>("PlayerPage").IsVisible && Element<Grid>("BrowserPage").IsVisible, "natural completion returns to episode selection page");
            Check(!bottom.IsVisible && !foregroundWindow.IsVisible, "natural completion hides actual VLC foreground window and controls");
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = true, checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
            _window.Close(); Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = false, failure = ex.ToString(), checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
            _window?.Close(); Shutdown(1);
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
