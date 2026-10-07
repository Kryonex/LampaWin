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
            var played = new TaskCompletionSource();
            _window.PlaybackChanged += p => { if (p.State == PlaybackState.Playing && p.PositionSeconds > .5) played.TrySetResult(); };
            _window.Play(new MediaRequest(new Uri(Path.GetFullPath(args[0])), "Тестовый фильм · Встроенный VLC", 0, Guid.NewGuid().ToString()));
            await played.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var overlay = Element<Grid>("PlayerOverlay");
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
            await Task.Delay(300);
            Check(Element<TextBlock>("TimeLabel").Text.Contains("00:06"), "timeline drag seeks native player to midpoint");
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
            Check(_window.WindowStyle == WindowStyle.None && _window.WindowState == WindowState.Maximized, "fullscreen switches owner window state");
            Click("FullscreenButton");
            Check(_window.WindowStyle != WindowStyle.None && _window.WindowState == WindowState.Normal, "fullscreen exit restores window state");
            _window.Left = -30000; _window.Top = -30000;
            _window.Show();
            Call("Home_Click", _window, new RoutedEventArgs());
            Check(!Element<Grid>("PlayerPage").IsVisible && Element<Border>("WindowFooter").IsVisible, "return to catalog restores layout");
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = true, checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
            _window.Close(); Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { success = false, failure = ex.ToString(), checks = _checks }, new JsonSerializerOptions { WriteIndented = true }));
            _window?.Close(); Shutdown(1);
        }
    }
}
