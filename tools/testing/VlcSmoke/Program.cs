using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using LampaWin.Desktop;
using LibVLCSharp.WPF;

namespace VlcSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => new SmokeApp(args).Run();
}

internal sealed class SmokeApp : Application
{
    private readonly string _fixturePath;
    private readonly string _reportPath;
    private readonly List<object> _events = [];
    private PlayerController? _player;
    private Window? _window;
    private DispatcherTimer? _timeout;
    private DispatcherTimer? _pauseHoldTimer;
    private int _phase;
    private bool _finished;
    private double _pausedPosition;
    private bool _pauseHeld;
    private bool _resumeAdvanced;
    private bool _resumePositionRestored;
    private bool _seekCompleted;

    public SmokeApp(string[] args)
    {
        _fixturePath = Value(args, "--fixture") ?? throw new ArgumentException("Required: --fixture <local-video-file>");
        _reportPath = Value(args, "--report") ?? throw new ArgumentException("Required: --report <json-path>");
        _fixturePath = Path.GetFullPath(_fixturePath);
        _reportPath = Path.GetFullPath(_reportPath);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var view = new VideoView();
            _window = new Window
            {
                Title = "LampaWin VLC smoke test",
                Width = 640,
                Height = 420,
                Content = view,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = -30000
            };
            _player = new PlayerController(Dispatcher);
            view.MediaPlayer = _player.MediaPlayer;
            _player.ProgressChanged += OnProgress;
            _player.DiagnosticChanged += d => _events.Add(new { diagnostic = d.Category, d.NativeState, d.Severity });
            _player.PlaybackEnded += _ => Finish(_phase == 3 && _seekCompleted, "natural-end");
            _window.Show();
            Dispatcher.BeginInvoke(() =>
            {
                try { _player.Play(new MediaRequest(new Uri(_fixturePath), "fixture", 4, Guid.NewGuid().ToString("N"))); }
                catch (Exception ex) { _events.Add(new { errorType = ex.GetType().Name }); Finish(false, "play-threw"); }
            }, DispatcherPriority.Loaded);
            _timeout = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.ApplicationIdle, (_, _) => Finish(false, "timeout"), Dispatcher);
            _timeout.Start();
        }
        catch (Exception ex)
        {
            WriteReport(false, "initialization-failed", ex.GetType().Name);
            Shutdown(2);
        }
    }

    private void OnProgress(PlaybackProgress progress)
    {
        _events.Add(new { state = progress.State.ToString(), progress.PositionSeconds, progress.DurationSeconds });
        if (progress.State == PlaybackState.Error)
        {
            Finish(false, "libvlc-error");
            return;
        }

        if (_phase == 0 && progress.State == PlaybackState.Playing && progress.PositionSeconds >= 4)
        {
            _resumePositionRestored = true;
            _phase = 1;
            _player!.TogglePause();
            return;
        }

        if (_phase == 1 && progress.State == PlaybackState.Paused)
        {
            _pausedPosition = progress.PositionSeconds;
            _phase = 2;
            _pauseHoldTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.ApplicationIdle, (_, _) =>
            {
                _pauseHoldTimer!.Stop();
                _pauseHeld = Math.Abs(_player!.MediaPlayer.Time / 1000d - _pausedPosition) <= 0.15;
                _events.Add(new { check = "pause-holds-position", passed = _pauseHeld });
                if (!_pauseHeld) { Finish(false, "pause-did-not-hold-position"); return; }
                _player.TogglePause();
            }, Dispatcher);
            _pauseHoldTimer.Start();
            return;
        }

        if (_phase == 2 && progress.State == PlaybackState.Playing && progress.PositionSeconds > _pausedPosition + 0.1)
        {
            _resumeAdvanced = true;
            _events.Add(new { check = "resume-advances-position", passed = true });
            _phase = 3;
            _player!.SeekTo(0.94);
        }

        if (_phase == 3 && progress.State == PlaybackState.Playing && progress.PositionSeconds >= progress.DurationSeconds * 0.90)
            _seekCompleted = true;
        if (progress.State == PlaybackState.Ended)
            _events.Add(new { check = "ended-progress", passed = true });
    }

    private void Finish(bool playbackEnded, string reason)
    {
        if (_finished) return;
        _finished = true;
        _timeout?.Stop();
        _pauseHoldTimer?.Stop();
        var passed = playbackEnded && _resumePositionRestored && _pauseHeld && _resumeAdvanced && _seekCompleted;
        WriteReport(passed, reason, null);
        _player?.Dispose();
        _window?.Close();
        Shutdown(passed ? 0 : 2);
    }

    private void WriteReport(bool success, string reason, string? failureType)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);
        var report = new
        {
            success,
            reason,
            failureType,
            startPositionRestored = _resumePositionRestored,
            pauseHeldPosition = _pauseHeld,
            resumeAdvancedPosition = _resumeAdvanced,
            seekReachedNearEnd = _seekCompleted,
            events = _events.TakeLast(40)
        };
        File.WriteAllText(_reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
