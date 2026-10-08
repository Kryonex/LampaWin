using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using System.Windows.Threading;

namespace LampaWin.Desktop;

/// <summary>Owns LibVLC resources and marshals its native callbacks onto the WPF dispatcher.</summary>
public sealed class PlayerController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private Media? _media;
    private Uri? _currentUrl;
    private string? _sessionId;
    private bool _disposed;
    private PlaybackState _state = PlaybackState.Stopped;
    private volatile bool _buffering;
    private long? _pendingStartMilliseconds;
    private readonly ConcurrentDictionary<string, byte> _reportedLogCategories = new(StringComparer.Ordinal);
    private readonly SeekPreviewDecoder _previewDecoder = new();
    public bool PreviewEnabled { get; set; } = true;
    private string _outputDevice = string.Empty;
    private readonly AudioDeviceMonitor? _audioDevices;
    private readonly DispatcherTimer _audioRecoveryTimer;
    private int _volume = 80;

    public PlayerController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
        _player = new MediaPlayer(_libVlc);
        _audioRecoveryTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _audioRecoveryTimer!.Stop();
            RestoreAudioOutput();
        }, dispatcher);
        _audioRecoveryTimer.Stop();
        try
        {
            _audioDevices = new AudioDeviceMonitor(dispatcher);
            _audioDevices.OutputChanged += QueueAudioRecovery;
        }
        catch (System.Runtime.InteropServices.COMException) { ReportDiagnostic("audio-device-monitor-unavailable", "Warning"); }
        _libVlc.Log += OnLibVlcLog;
        _player.Playing += (_, _) =>
        {
            _state = PlaybackState.Playing; ReportDiagnostic("native-playing", "Info"); Emit(_state);
            _dispatcher.BeginInvoke(() => { ApplyPendingStart(); _player.Volume = _volume; if (_outputDevice.Length > 0) RestoreAudioOutput(); });
        };
        _player.Paused += (_, _) => { _state = PlaybackState.Paused; ReportDiagnostic("native-paused", "Info"); Emit(_state); };
        _player.Stopped += (_, _) => { _state = PlaybackState.Stopped; ReportDiagnostic("native-stopped", "Info"); Emit(_state); };
        _player.EndReached += (_, _) => { ReportDiagnostic("native-ended", "Info"); _dispatcher.BeginInvoke(FinishNaturally); };
        _player.EncounteredError += (_, _) => { _state = PlaybackState.Error; ReportDiagnostic("native-encountered-error", "Error"); Emit(_state); };
        _player.Buffering += (_, progress) => { _buffering = progress.Cache < 100; Emit(_state); };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            // Some streams reach VLCState.Ended without raising EndReached.
            // Complete the session so the player closes and returns to Lampa.
            if (_player.State == VLCState.Ended)
            {
                FinishNaturally();
                return;
            }
            if (_state is PlaybackState.Opening or PlaybackState.Playing or PlaybackState.Paused)
            {
                _state = _player.State switch
                {
                    VLCState.Playing => PlaybackState.Playing,
                    VLCState.Paused => PlaybackState.Paused,
                    VLCState.Ended => PlaybackState.Ended,
                    VLCState.Error => PlaybackState.Error,
                    VLCState.Stopped => PlaybackState.Stopped,
                    _ => PlaybackState.Opening
                };
                Emit(_state);
                ApplyPendingStart();
            }
        }, dispatcher);
        _timer.Start();
    }

    public MediaPlayer MediaPlayer => _player;
    public event Action<PlaybackProgress>? ProgressChanged;
    public event Action<string>? PlaybackEnded;
    public event Action<string>? PlaybackClosed;
    public event Action<PlaybackDiagnostic>? DiagnosticChanged;

    public void Play(MediaRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!request.Url.IsAbsoluteUri || request.Url.Scheme is not ("http" or "https" or "file"))
            throw new ArgumentException("Поддерживаются только HTTP, HTTPS и локальные файлы.", nameof(request));
        Stop();
        _reportedLogCategories.Clear();
        _sessionId = request.SessionId;
        _currentUrl = request.Url;
        _state = PlaybackState.Opening;
        _buffering = true;
        ReportDiagnostic("open-requested", "Info");
        _pendingStartMilliseconds = request.StartSeconds > 0 && double.IsFinite(request.StartSeconds)
            ? (long)(Math.Clamp(request.StartSeconds, 0, int.MaxValue / 1000d) * 1000)
            : null;
        _media = new Media(_libVlc, request.Url);
        if (!_player.Play(_media))
        {
            _state = PlaybackState.Error;
            ReportDiagnostic("native-play-rejected", "Error");
            Emit(_state);
            throw new InvalidOperationException("LibVLC не смог открыть этот источник.");
        }
        ApplyPendingStart();
    }

    public void TogglePause()
    {
        if (_player.IsPlaying) _player.Pause(); else if (_player.Media is not null) _player.SetPause(false);
    }

    public void SeekBy(TimeSpan amount)
    {
        if (_player.Length <= 0) return;
        _player.Time = Math.Clamp(_player.Time + (long)amount.TotalMilliseconds, 0, _player.Length);
        Emit(_player.IsPlaying ? PlaybackState.Playing : PlaybackState.Paused);
    }

    public void SeekTo(double fraction)
    {
        if (_player.Length <= 0 || !double.IsFinite(fraction)) return;
        _player.Time = (long)(_player.Length * Math.Clamp(fraction, 0, 1));
    }

    /// <summary>Captures a throttled, off-screen frame without moving the active player.</summary>
    public Task<BitmapSource?> CapturePreviewAsync(double fraction, CancellationToken cancellationToken)
    {
        var source = _currentUrl;
        if (_disposed || !PreviewEnabled || _buffering && _player.State != VLCState.Paused || source is null || !double.IsFinite(fraction)) return Task.FromResult<BitmapSource?>(null);
        var target = (long)Math.Max(0, _player.Length * Math.Clamp(fraction, 0, .999));
        return _previewDecoder.CaptureAsync(source, target, cancellationToken);
    }

    public void SetOutputDevice(string device)
    {
        _outputDevice = device;
        RestoreAudioOutput();
    }
    public IReadOnlyList<(string Id, string Name)> OutputDevices => _player.AudioOutputDeviceEnum
        .Select(device => (device.DeviceIdentifier, device.Description)).ToArray();
    public void SetAudioDelay(double milliseconds) => _player.SetAudioDelay((long)(Math.Clamp(milliseconds, -10000, 10000) * 1000));
    public void SetSubtitleDelay(double milliseconds) => _player.SetSpuDelay((long)(Math.Clamp(milliseconds, -10000, 10000) * 1000));
    public void SetAspectRatio(string? ratio) => _player.AspectRatio = ratio;
    public bool AddSubtitle(string path) => _player.AddSlave(MediaSlaveType.Subtitle, new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri, true);

    public void SetVolume(double value)
    {
        _volume = (int)Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);
        _player.Volume = _volume;
    }

    private void QueueAudioRecovery()
    {
        if (_disposed || _sessionId is null) return;
        _audioRecoveryTimer.Stop();
        _audioRecoveryTimer.Start();
    }

    public void RestoreAudioOutput()
    {
        if (_disposed || _sessionId is null || _player.Media is null) return;
        _audioRecoveryTimer.Stop();
        if (_audioDevices is { DefaultOutputId: null })
        { ReportDiagnostic("audio-output-unavailable", "Warning"); return; }
        // LibVLC 3 requires an empty device ID to reacquire the system default.
        // A null device ID is ignored. Switching the output module would require
        // restarting the movie; this restarts only the active audio output.
        _player.SetOutputDevice(_outputDevice, null);
        _player.Volume = _volume;
        _player.Mute = false; // UI mute is represented by volume=0, which stays zero.
        ReportDiagnostic("audio-output-rebound", "Info");
    }
    public void SetAudioTrack(int id) => _player.SetAudioTrack(id);
    public int? FindTrack(string language, bool subtitle)
    {
        if (language.Length == 0) return null;
        return _media?.Tracks.Where(track => track.TrackType == (subtitle ? TrackType.Text : TrackType.Audio)
            && (track.Language == language || language == "rus" && track.Language == "ru" || language == "eng" && track.Language == "en"))
            .Select(track => (int?)track.Id).FirstOrDefault();
    }
    public void SetSubtitleTrack(int id) => _player.SetSpu(id);
    public IReadOnlyList<TrackDescription> AudioTracks => _player.AudioTrackDescription?.ToArray() ?? [];
    public IReadOnlyList<TrackDescription> SubtitleTracks => _player.SpuDescription?.ToArray() ?? [];

    public void Stop()
    {
        _audioRecoveryTimer.Stop();
        _previewDecoder.Cancel();
        var session = _sessionId;
        if (session is not null) EmitNow(PlaybackState.Stopped);
        _sessionId = null;
        _state = PlaybackState.Stopped;
        _buffering = false;
        _pendingStartMilliseconds = null;
        _currentUrl = null;
        if (_player.Media is not null) _player.Stop();
        _media?.Dispose();
        _media = null;
        if (session is not null) PlaybackClosed?.Invoke(session);
    }

    private void Emit(PlaybackState state)
    {
        var session = _sessionId;
        if (session is null) return;
        var current = new PlaybackProgress(session, Math.Max(0, _player.Time / 1000d), Math.Max(0, _player.Length / 1000d), state, _buffering);
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && _sessionId == current.SessionId) ProgressChanged?.Invoke(current);
        }, DispatcherPriority.Background);
    }

    private void EmitNow(PlaybackState state)
    {
        if (_sessionId is { } session)
            ProgressChanged?.Invoke(new PlaybackProgress(session, Math.Max(0, _player.Time / 1000d), Math.Max(0, _player.Length / 1000d), state));
    }

    private void FinishNaturally()
    {
        var session = _sessionId;
        if (session is null || _disposed) return;
        _state = PlaybackState.Ended;
        EmitNow(_state);
        _sessionId = null;
        _pendingStartMilliseconds = null;
        _currentUrl = null;
        _media?.Dispose();
        _media = null;
        PlaybackEnded?.Invoke(session);
    }

    private void ApplyPendingStart()
    {
        if (_disposed || _pendingStartMilliseconds is not { } start || !_player.IsPlaying) return;
        _player.Time = start;
        _pendingStartMilliseconds = null;
    }

    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        if ((int)e.Level < (int)LogLevel.Warning) return;
        var text = e.Message.ToLowerInvariant();
        var category = text.Contains("connection refused") || text.Contains("failed to connect") || text.Contains("network is unreachable")
            ? "source-connection-failed"
            : text.Contains("timed out") || text.Contains("timeout")
                ? "source-timeout"
                : text.Contains("401") || text.Contains("403") || text.Contains("unauthorized") || text.Contains("forbidden")
                    ? "source-auth-rejected"
                    : text.Contains("404") || text.Contains("not found")
                        ? "source-not-found"
                        : text.Contains("decoder") || text.Contains("demux") || text.Contains("avcodec")
                            ? "stream-decode-failed"
                            : text.Contains("access module") || text.Contains("unable to open") || text.Contains("failed to open")
                                ? "source-open-failed"
                                : "native-warning-or-error";
        ReportDiagnostic(category, e.Level.ToString());
    }

    private void ReportDiagnostic(string category, string severity)
    {
        if (_disposed) return;
        if (!_reportedLogCategories.TryAdd(category, 0)) return;
        var diagnostic = new PlaybackDiagnostic(category, _player.State.ToString(), severity);
        if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(() => DiagnosticChanged?.Invoke(diagnostic), DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _timer.Stop();
        if (_audioDevices is not null)
        {
            _audioDevices.OutputChanged -= QueueAudioRecovery;
            _audioDevices.Dispose();
        }
        _libVlc.Log -= OnLibVlcLog;
        _player.Dispose();
        _libVlc.Dispose();
        _ = _previewDecoder.DisposeAsync().AsTask();
        GC.SuppressFinalize(this);
    }
}
