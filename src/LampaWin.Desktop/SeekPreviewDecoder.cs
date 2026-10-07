using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace LampaWin.Desktop;

/// <summary>A paused, reusable low-resolution decoder. All native work stays off the UI thread.</summary>
internal sealed class SeekPreviewDecoder : IAsyncDisposable
{
    private const int Width = 320, Height = 180, Stride = Width * 4;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly object _pixelsLock = new();
    private LibVLC? _engine;
    private MediaPlayer? _player;
    private Media? _media;
    private Uri? _source;
    private nint _pixels;
    private TaskCompletionSource<byte[]>? _frame;
    private CancellationTokenSource? _request;
    private long _target;
    private int _skipFrames;
    private int _disposed;
    private bool _hasFrame;
    private MediaPlayer.LibVLCVideoLockCb? _lock;
    private MediaPlayer.LibVLCVideoUnlockCb? _unlock;
    private MediaPlayer.LibVLCVideoDisplayCb? _display;

    public void Cancel()
    {
        lock (_sync) _request?.Cancel();
    }

    public Task<BitmapSource?> CaptureAsync(Uri source, long target, CancellationToken token) => Task.Run(async () =>
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return null;
            lock (_sync) _request = request;
            request.Token.ThrowIfCancellationRequested();
            EnsureDecoder();
            var fresh = _source != source || !_hasFrame || _player!.State is not (VLCState.Playing or VLCState.Paused);
            if (fresh)
            {
                _hasFrame = false;
                _player!.Stop();
                _media?.Dispose();
                _media = new Media(_engine!, source);
                _media.AddOption(":no-audio");
                _media.AddOption(":no-video-title-show");
                _media.AddOption(":network-caching=150");
                _media.AddOption(":start-time=" + (target / 1000d).ToString(System.Globalization.CultureInfo.InvariantCulture));
                _source = source;
            }
            var frame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) { _target = target; _skipFrames = fresh ? 0 : 2; _frame = frame; }
            if (fresh)
            {
                if (!_player!.Play(_media!)) return null;
            }
            else
            {
                _player!.Time = target;
                _player.SetPause(false);
            }
            var bytes = await frame.Task.WaitAsync(TimeSpan.FromSeconds(6), request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            _hasFrame = true;
            var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, bytes, Stride);
            bitmap.Freeze();
            return (BitmapSource?)bitmap;
        }
        catch (TimeoutException) { return null; }
        catch (InvalidOperationException) { return null; }
        finally
        {
            lock (_sync) { _frame = null; _request = null; }
            // Keep the media/decoder open; the next hover only seeks, not opens a new stream.
            _player?.SetPause(true);
            _gate.Release();
        }
    }, token);

    private void EnsureDecoder()
    {
        if (_engine is not null) return;
        _engine = new LibVLC("--no-audio", "--avcodec-hw=none");
        _player = new MediaPlayer(_engine) { Mute = true };
        _pixels = Marshal.AllocHGlobal(Stride * Height);
        _lock = (_, planes) => { Monitor.Enter(_pixelsLock); Marshal.WriteIntPtr(planes, _pixels); return nint.Zero; };
        _unlock = (_, _, _) => Monitor.Exit(_pixelsLock);
        _display = (_, _) =>
        {
            lock (_sync)
            {
                if (_frame is null || _frame.Task.IsCompleted || Math.Abs(_player.Time - _target) > 1000) return;
                if (_skipFrames-- > 0) return;
                var bytes = new byte[Stride * Height];
                lock (_pixelsLock) Marshal.Copy(_pixels, bytes, 0, bytes.Length);
                _frame.TrySetResult(bytes);
            }
        };
        _player.SetVideoFormat("RV32", Width, Height, Stride);
        _player.SetVideoCallbacks(_lock, _unlock, _display);
        _player.EncounteredError += (_, _) =>
        {
            lock (_sync) _frame?.TrySetException(new InvalidOperationException("Preview source could not be opened."));
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancel();
        await Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _player?.Stop();
                _player?.Dispose();
                _media?.Dispose();
                _engine?.Dispose();
                if (_pixels != nint.Zero) Marshal.FreeHGlobal(_pixels);
                GC.KeepAlive(_lock); GC.KeepAlive(_unlock); GC.KeepAlive(_display);
            }
            finally { _gate.Release(); }
        }).ConfigureAwait(false);
    }
}
