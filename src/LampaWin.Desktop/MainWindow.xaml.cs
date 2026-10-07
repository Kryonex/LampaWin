using Microsoft.Web.WebView2.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LampaWin.Desktop;

public partial class MainWindow : Window
{
    private readonly PlayerController _player;
    private FoodPanel? _foodPanel;
    private bool _foodExpanded;
    private string _foodProfileRoot = Path.Combine(new LampaWin.Core.AppPaths(AppContext.BaseDirectory).DataRoot, "food-webview");
    private readonly ObservableCollection<string> _sources = [];
    private IReadOnlyList<string> _sourceWarnings = [];
    private string _currentStatus = "Ожидание запуска";
    private bool _browserReady;
    private bool _documentLoaded;
    private bool _seeking;
    private bool _fullscreen;
    private bool _topmost;
    private Rect _restoreBounds;
    private double _minimumWidth;
    private double _minimumHeight;
    private string _audioTrackKey = "";
    private string _subtitleTrackKey = "";
    private WindowStyle _windowStyle;
    private ResizeMode _resizeMode;
    private WindowState _windowState;
    private readonly DispatcherTimer _controlsTimer;
    private readonly DispatcherTimer _clickTimer;
    private readonly DispatcherTimer _previewTimer;
    private bool _controlsVisible = true;
    private bool _keyboardNavigation;
    private bool _updatingPosition;
    private double _volumeBeforeMute = 80;
    private Point? _lastPointer;
    private double? _pendingSeekSeconds;
    private DateTimeOffset _pendingSeekStarted;
    private double? _seekAnchorSeconds;
    private double _seekDisplaySeconds;
    private double _seekDurationSeconds;
    private int _seekReadySamples;
    private DateTimeOffset? _seekFirstReadyAt;
    private double _previewFraction;
    private CancellationTokenSource? _previewCancellation;
    private readonly Dictionary<int, BitmapSource> _previewCache = [];
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly nint HwndTopmost = new(-1);

    public MainWindow()
    {
        InitializeComponent();
        PlayerOverlay.SizeChanged += (_, _) => UpdateFoodPanelWidth();
        SourcesList.ItemsSource = _sources;
        _player = new PlayerController(Dispatcher);
        _player.SetVolume(VolumeSlider.Value);
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        VideoSurface.MediaPlayer = _player.MediaPlayer;
        _player.ProgressChanged += OnPlaybackChanged;
        _player.DiagnosticChanged += diagnostic => PlaybackDiagnosticChanged?.Invoke(diagnostic);
        _player.PlaybackClosed += session => PlaybackClosed?.Invoke(session);
        _player.PlaybackEnded += session =>
        {
            PlaybackClosed?.Invoke(session);
            if (PlayerPage.Visibility == Visibility.Visible) Home_Click(this, new RoutedEventArgs());
        };
        _windowStyle = WindowStyle;
        _resizeMode = ResizeMode;
        _windowState = WindowState;

        _controlsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _controlsTimer.Tick += (_, _) => HidePlayerControls();
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); TogglePlayback(); };
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
        _previewTimer.Tick += async (_, _) =>
        {
            _previewTimer.Stop();
            var requested = _previewFraction;
            await LoadSeekPreviewAsync(requested);
            var duration = _player.MediaPlayer.Length / 1000d;
            if (SeekPreview.Visibility == Visibility.Visible && duration > 0
                && PreviewBucket(duration, requested) != PreviewBucket(duration, _previewFraction)
                && !_previewCache.ContainsKey(PreviewBucket(duration, _previewFraction))) _previewTimer.Start();
        };
        Deactivated += (_, _) => { _clickTimer.Stop(); PlayerOverlay.Cursor = Cursors.Arrow; };
    }

    public Microsoft.Web.WebView2.Wpf.WebView2 Browser => BrowserView;
    public event Func<Task>? RecoverRequested;
    public event Action? OpenLogsRequested;
    public event Action? ExitRequested;
    public event Action<PlaybackProgress>? PlaybackChanged;
    public event Action<string>? PlaybackClosed;
    public event Action<PlaybackDiagnostic>? PlaybackDiagnosticChanged;

    public async Task InitializeBrowserAsync(string profilePath, Uri initialUri,
        Func<CoreWebView2, Task>? configureBeforeNavigation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        _foodProfileRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profilePath))!, "food-webview");
        ArgumentNullException.ThrowIfNull(initialUri);
        if (!initialUri.IsAbsoluteUri || initialUri.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Каталог должен открываться по локальному HTTP-адресу.", nameof(initialUri));
        Directory.CreateDirectory(profilePath);
        var environment = await CoreWebView2Environment.CreateAsync(null, profilePath);
        await BrowserView.EnsureCoreWebView2Async(environment);
        BrowserView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        BrowserView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        BrowserView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        BrowserView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        BrowserView.CoreWebView2.NavigationStarting += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _documentLoaded = false;
            _browserReady = false;
        });
        BrowserView.CoreWebView2.NavigationCompleted += (_, args) => Dispatcher.BeginInvoke(() =>
        {
            _documentLoaded = args.IsSuccess;
            if (_documentLoaded && _browserReady && ErrorOverlay.Visibility != Visibility.Visible)
                StartupOverlay.Visibility = Visibility.Collapsed;
        });
        if (configureBeforeNavigation is not null)
            await configureBeforeNavigation(BrowserView.CoreWebView2);
        BrowserView.CoreWebView2.Navigate(initialUri.AbsoluteUri);
    }

    /// <summary>Call only after the local application bridge reports that the initial catalog is ready.</summary>
    public void MarkBrowserReady()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _browserReady = true;
            if (_documentLoaded && ErrorOverlay.Visibility != Visibility.Visible)
                StartupOverlay.Visibility = Visibility.Collapsed;
        });
    }

    public void SetStatus(string message, bool busy, bool failed = false)
    {
        Dispatcher.BeginInvoke(() =>
        {
            FooterStatus.Text = message;
            _currentStatus = message;
            RenderSettingsStatus();
            StartupMessage.Text = message;
            StartupProgress.IsIndeterminate = busy;
            if (failed)
            {
                StartupOverlay.Visibility = Visibility.Collapsed;
                ErrorMessage.Text = message;
                ErrorOverlay.Visibility = Visibility.Visible;
            }
            else if (busy && BrowserPage.Visibility == Visibility.Visible)
            {
                ErrorOverlay.Visibility = Visibility.Collapsed;
                StartupOverlay.Visibility = Visibility.Visible;
            }
            else if (!busy)
            {
                ErrorOverlay.Visibility = Visibility.Collapsed;
                if (_browserReady && _documentLoaded) StartupOverlay.Visibility = Visibility.Collapsed;
            }
        });
    }

    public void SetSources(IReadOnlyList<string> sources, IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Dispatcher.BeginInvoke(() =>
        {
            _sources.Clear();
            foreach (var source in sources.Where(s => !string.IsNullOrWhiteSpace(s))) _sources.Add(source);
            _sourceWarnings = warnings.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            SourcesEmpty.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderSettingsStatus();
        });
    }

    private void RenderSettingsStatus()
    {
        SettingsStatus.Text = _sourceWarnings.Count == 0
            ? _currentStatus
            : $"{_currentStatus}\n\nПредупреждения:\n• {string.Join("\n• ", _sourceWarnings)}";
    }

    public void SetLogsPath(string path)
    {
        LogPathText.Text = string.IsNullOrWhiteSpace(path) ? "Путь к журналам недоступен." : path;
    }

    public void Play(MediaRequest request)
    {
        Dispatcher.Invoke(() =>
        {
            _previewTimer.Stop();
            _previewCancellation?.Cancel();
            UpdatePosition(0);
            TimeLabel.Text = "00:00 / 00:00";
            MediaTitle.Text = string.IsNullOrWhiteSpace(request.Title) ? "Воспроизведение" : request.Title;
            BrowserPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            PlayerPage.Visibility = Visibility.Visible;
            PlayerOverlay.Visibility = Visibility.Visible;
            VideoSurface.Visibility = Visibility.Visible;
            PlayPauseButton.Content = "\uE769";
            PlayerSettingsPanel.Visibility = Visibility.Collapsed;
            _lastPointer = null;
            _pendingSeekSeconds = null;
            _seekAnchorSeconds = null;
            _seekReadySamples = 0;
            _seekFirstReadyAt = null;
            SeekBufferingPanel.Visibility = Visibility.Collapsed;
            SeekPreview.Visibility = Visibility.Collapsed;
            SeekPreviewImage.Source = null;
            _previewCache.Clear();

            // В режиме плеера скрываем нижний статус-бар окна и убираем внешние отступы, чтобы видео занимало всё окно
            WindowFooter.Visibility = Visibility.Collapsed;
            MainContentGrid.Margin = new Thickness(0);
            Grid.SetRowSpan(MainContentGrid, 2);

            PlayerPage.UpdateLayout();
            if (Window.GetWindow(PlayerOverlay) is { } foreground && foreground != this && !foreground.IsVisible)
                foreground.Show();
            _player.Play(request);
            ShowPlayerControls();
            if (IsActive) PlayerOverlay.Focus();
            RefreshTracks();
        });
    }

    public void StopPlayback()
    {
        if (Dispatcher.CheckAccess()) _player.Stop();
        else Dispatcher.Invoke(_player.Stop);
    }

    private void OnPlaybackChanged(PlaybackProgress update)
    {
        if (update.State is PlaybackState.Playing) RefreshTracksIfChanged();
        var duration = update.DurationSeconds;
        if (duration <= 0 && _seekAnchorSeconds is not null) duration = _seekDurationSeconds;
        var displayedPosition = update.PositionSeconds;
        var terminal = update.State is PlaybackState.Error or PlaybackState.Ended or PlaybackState.Stopped;
        var elapsed = Math.Max(0, (DateTimeOffset.UtcNow - _pendingSeekStarted).TotalSeconds);
        // VLC can briefly publish the requested timestamp, then deliver queued timestamps
        // from before the seek. Keep a timeline fence even after seek acknowledgement.
        var staleAfterSeek = !terminal && _seekAnchorSeconds is { } anchor
            && (update.PositionSeconds < anchor - 2 || update.PositionSeconds > anchor + elapsed + 3);
        if (_pendingSeekSeconds is { } target)
        {
            var ready = !staleAfterSeek && !update.IsBuffering && update.State is PlaybackState.Playing or PlaybackState.Paused
                && Math.Abs(update.PositionSeconds - target) <= Math.Max(2, elapsed + 1);
            if (ready)
            {
                _seekFirstReadyAt ??= DateTimeOffset.UtcNow;
                _seekReadySamples++;
            }
            else { _seekFirstReadyAt = null; _seekReadySamples = 0; }
            var stable = _seekReadySamples >= 2 && _seekFirstReadyAt is { } first
                && DateTimeOffset.UtcNow - first >= TimeSpan.FromMilliseconds(400)
                && (update.State == PlaybackState.Paused || update.PositionSeconds >= target + .2);
            if (terminal || stable)
            {
                _pendingSeekSeconds = null;
                SeekBufferingPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                displayedPosition = target;
                SeekBufferingText.Text = DateTimeOffset.UtcNow - _pendingSeekStarted > TimeSpan.FromSeconds(20)
                    ? "Участок ещё загружается…" : "Подгружаем…";
                SeekBufferingPanel.Visibility = Visibility.Visible;
            }
        }
        if (terminal) _seekAnchorSeconds = null;
        else if (staleAfterSeek) displayedPosition = _pendingSeekSeconds ?? _seekDisplaySeconds;
        if (_seekAnchorSeconds is not null) _seekDisplaySeconds = displayedPosition;
        if (_pendingSeekSeconds is null)
        {
            SeekBufferingPanel.Visibility = update.IsBuffering && update.State is not (PlaybackState.Stopped or PlaybackState.Ended or PlaybackState.Error)
                ? Visibility.Visible : Visibility.Collapsed;
            SeekBufferingText.Text = "Подгружаем…";
        }
        if (!_seeking && duration > 0)
            UpdatePosition(Math.Clamp(displayedPosition / duration, 0, 1) * PositionSlider.Maximum);
        if (!_seeking) TimeLabel.Text = $"{FormatTime(displayedPosition)} / {FormatTime(duration)}";
        PlaybackChanged?.Invoke(update with { PositionSeconds = displayedPosition, IsBuffering = update.IsBuffering || _pendingSeekSeconds is not null || staleAfterSeek });
        PositionSlider.IsEnabled = duration > 0 && (_player.MediaPlayer.IsSeekable || _seekAnchorSeconds is not null);
        if (update.State is PlaybackState.Playing)
        {
            PlayPauseButton.Content = "\uE769";
            PlayPauseButton.ToolTip = "Пауза (Space / K)";
        }
        else if (update.State is PlaybackState.Paused or PlaybackState.Opening or PlaybackState.Error)
        {
            PlayPauseButton.Content = "\uE768";
            PlayPauseButton.ToolTip = "Продолжить (Space / K)";
            ShowPlayerControls();
        }
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(Math.Min(seconds, 359999));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    private void RefreshTracks()
    {
        var previousAudioId = (AudioTracks.SelectedItem as TrackChoice)?.Id;
        var previousSubtitleId = (SubtitleTracks.SelectedItem as TrackChoice)?.Id;
        AudioTracks.SelectionChanged -= AudioTracks_SelectionChanged;
        SubtitleTracks.SelectionChanged -= SubtitleTracks_SelectionChanged;
        AudioTracks.Items.Clear();
        SubtitleTracks.Items.Clear();
        foreach (var track in _player.AudioTracks)
            AudioTracks.Items.Add(new TrackChoice(track.Id == -1 ? "Отключено" : track.Name, track.Id));
        SubtitleTracks.Items.Add(new TrackChoice("Выкл.", -1));
        foreach (var track in _player.SubtitleTracks.Where(track => track.Id >= 0))
            SubtitleTracks.Items.Add(new TrackChoice(track.Name, track.Id));
        AudioTracks.DisplayMemberPath = nameof(TrackChoice.Name);
        SubtitleTracks.DisplayMemberPath = nameof(TrackChoice.Name);
        AudioTracks.SelectedItem = AudioTracks.Items.OfType<TrackChoice>().FirstOrDefault(x => x.Id == _player.MediaPlayer.AudioTrack)
            ?? AudioTracks.Items.OfType<TrackChoice>().FirstOrDefault(x => x.Id == previousAudioId)
            ?? AudioTracks.Items.OfType<TrackChoice>().FirstOrDefault();
        SubtitleTracks.SelectedItem = SubtitleTracks.Items.OfType<TrackChoice>().FirstOrDefault(x => x.Id == _player.MediaPlayer.Spu)
            ?? SubtitleTracks.Items.OfType<TrackChoice>().FirstOrDefault(x => x.Id == previousSubtitleId)
            ?? SubtitleTracks.Items.OfType<TrackChoice>().FirstOrDefault(x => x.Id == -1);
        _audioTrackKey = string.Join("|", _player.AudioTracks.Select(x => $"{x.Id}:{x.Name}"));
        _subtitleTrackKey = string.Join("|", _player.SubtitleTracks.Select(x => $"{x.Id}:{x.Name}"));
        AudioTracks.SelectionChanged += AudioTracks_SelectionChanged;
        SubtitleTracks.SelectionChanged += SubtitleTracks_SelectionChanged;
    }

    private void RefreshTracksIfChanged()
    {
        var audioKey = string.Join("|", _player.AudioTracks.Select(x => $"{x.Id}:{x.Name}"));
        var subtitleKey = string.Join("|", _player.SubtitleTracks.Select(x => $"{x.Id}:{x.Name}"));
        if (audioKey != _audioTrackKey || subtitleKey != _subtitleTrackKey) RefreshTracks();
    }

    private sealed record TrackChoice(string Name, int Id)
    {
        public override string ToString() => Name;
    }

    private void UpdatePosition(double value)
    {
        _updatingPosition = true;
        try { PositionSlider.Value = value; }
        finally { _updatingPosition = false; }
    }

    private void PositionSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _clickTimer.Stop();
        _seeking = true;
        SetSeekPointerPosition(e.GetPosition(PositionSlider).X);
        PositionSlider.Focus();
        PositionSlider.CaptureMouse();
        e.Handled = true;
        ShowPlayerControls();
    }
    private void PositionSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        SetSeekPointerPosition(e.GetPosition(PositionSlider).X);
        CommitSeek();
        PositionSlider.ReleaseMouseCapture();
        e.Handled = true;
    }
    private void SetSeekPointerPosition(double x)
    {
        // Own the pointer-to-value mapping instead of committing a value before WPF's
        // Track/Thumb has finished processing the same routed mouse event.
        var usableWidth = Math.Max(1, PositionSlider.ActualWidth - 12);
        UpdatePosition(Math.Clamp((x - 6) / usableWidth, 0, 1) * PositionSlider.Maximum);
    }
    private void PositionSlider_LostCapture(object sender, MouseEventArgs e) => CommitSeek();
    private void CommitSeek()
    {
        if (!_seeking) return;
        SeekToFraction(PositionSlider.Value / PositionSlider.Maximum);
        _seeking = false;
        if (PositionSlider.IsMouseCaptured) PositionSlider.ReleaseMouseCapture();
        ShowPlayerControls();
    }
    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking && _player.MediaPlayer.Length > 0)
            TimeLabel.Text = $"{FormatTime(_player.MediaPlayer.Length * PositionSlider.Value / PositionSlider.Maximum / 1000d)} / {FormatTime(_player.MediaPlayer.Length / 1000d)}";
        else if (!_updatingPosition && PositionSlider.IsKeyboardFocusWithin && _player.MediaPlayer.Length > 0)
            SeekToFraction(PositionSlider.Value / PositionSlider.Maximum);
    }
    private void PositionSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (_seeking && Mouse.LeftButton == MouseButtonState.Pressed)
            SetSeekPointerPosition(e.GetPosition(PositionSlider).X);
        var duration = _player.MediaPlayer.Length / 1000d;
        if (duration <= 0 || PositionSlider.ActualWidth <= 0) return;
        var x = Math.Clamp(e.GetPosition(PositionSlider).X, 0, PositionSlider.ActualWidth);
        _previewFraction = x / PositionSlider.ActualWidth;
        SeekPreviewText.Text = FormatTime(duration * _previewFraction);
        SeekPreview.Margin = new Thickness(Math.Clamp(x - 88, 24, Math.Max(24, PlayerOverlay.ActualWidth - 200)), 0, 0, 92);
        SeekPreview.Visibility = Visibility.Visible;
        var bucket = PreviewBucket(duration, _previewFraction);
        if (_previewCache.TryGetValue(bucket, out var cached))
        {
            SeekPreviewImage.Source = cached;
            SeekPreviewLoading.Visibility = Visibility.Collapsed;
        }
        else
        {
            SeekPreviewImage.Source = null;
            SeekPreviewLoading.Text = "Загрузка кадра…";
            SeekPreviewLoading.Visibility = Visibility.Visible;
            // Throttle rather than debounce: continuous pointer movement must not starve frames.
            if (!_previewTimer.IsEnabled) _previewTimer.Start();
        }
    }
    private void PositionSlider_MouseLeave(object sender, MouseEventArgs e)
    {
        _previewTimer.Stop();
        _previewCancellation?.Cancel();
        SeekPreview.Visibility = Visibility.Collapsed;
    }

    private static int PreviewBucket(double duration, double fraction) =>
        (int)Math.Round(duration * Math.Clamp(fraction, 0, 1) / 2d, MidpointRounding.AwayFromZero);

    private async Task LoadSeekPreviewAsync(double fraction)
    {
        var duration = _player.MediaPlayer.Length / 1000d;
        if (duration <= 0 || SeekPreview.Visibility != Visibility.Visible) return;
        var bucket = PreviewBucket(duration, fraction);
        if (_previewCache.TryGetValue(bucket, out var cached))
        {
            SeekPreviewImage.Source = cached;
            SeekPreviewLoading.Visibility = Visibility.Collapsed;
            return;
        }
        // Let one frame finish; coalesce pointer changes to the latest target instead
        // of cancelling the decoder every 65ms and never receiving any frame.
        if (_previewCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _previewCancellation = cancellation;
        try
        {
            var image = await _player.CapturePreviewAsync(Math.Clamp(bucket * 2d / duration, 0, 1), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (image is null)
            {
                SeekPreviewLoading.Text = "Кадр пока недоступен";
                return;
            }
            if (_previewCache.Count >= 128) _previewCache.Remove(_previewCache.Keys.First());
            _previewCache[bucket] = image;
            if (SeekPreview.Visibility == Visibility.Visible && PreviewBucket(duration, _previewFraction) == bucket)
            {
                SeekPreviewImage.Source = image;
                SeekPreviewLoading.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null; }
    }

    private void SeekToFraction(double fraction)
    {
        var duration = _player.MediaPlayer.Length / 1000d;
        if (duration <= 0) return;
        fraction = Math.Clamp(fraction, 0, 1);
        _pendingSeekSeconds = duration * fraction;
        _pendingSeekStarted = DateTimeOffset.UtcNow;
        _seekAnchorSeconds = _pendingSeekSeconds;
        _seekDisplaySeconds = _pendingSeekSeconds.Value;
        _seekDurationSeconds = duration;
        _seekReadySamples = 0;
        _seekFirstReadyAt = null;
        UpdatePosition(fraction * PositionSlider.Maximum);
        TimeLabel.Text = $"{FormatTime(_pendingSeekSeconds.Value)} / {FormatTime(duration)}";
        SeekBufferingPanel.Visibility = Visibility.Visible;
        SeekBufferingText.Text = "Подгружаем…";
        _player.SeekTo(fraction);
    }
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _player.SetVolume(e.NewValue);
        MuteButton.Content = e.NewValue > 0 ? "\uE767" : "\uE74F";
        MuteButton.ToolTip = e.NewValue > 0 ? "Выключить звук (M)" : "Включить звук (M)";
    }
    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    private void RestoreAudio_Click(object sender, RoutedEventArgs e) => _player.RestoreAudioOutput();
    private void ToggleMute()
    {
        if (VolumeSlider.Value > 0) { _volumeBeforeMute = VolumeSlider.Value; VolumeSlider.Value = 0; }
        else VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 80;
        ShowPlayerControls();
    }
    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayback();
    private void TogglePlayback()
    {
        if (PlayerPage.Visibility != Visibility.Visible) return;
        var pausing = _player.MediaPlayer.IsPlaying;
        _player.TogglePause();
        FeedbackGlyph.Text = pausing ? "\uE769" : "\uE768";
        PlaybackFeedback.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(650)));
        ShowPlayerControls();
    }
    private void Rewind_Click(object sender, RoutedEventArgs e) => SeekPlayback(-10);
    private void Forward_Click(object sender, RoutedEventArgs e) => SeekPlayback(10);
    private void SeekPlayback(int seconds)
    {
        var duration = _player.MediaPlayer.Length / 1000d;
        if (duration <= 0) return;
        var current = _pendingSeekSeconds ?? Math.Max(0, _player.MediaPlayer.Time / 1000d);
        SeekToFraction(Math.Clamp(current + seconds, 0, duration) / duration);
        ShowPlayerControls();
    }
    private void PlayerSettings_Click(object sender, RoutedEventArgs e)
    {
        FoodPanelHost.Visibility = Visibility.Collapsed;
        PlayerSettingsPanel.Visibility = PlayerSettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ShowPlayerControls();
    }
    private void AudioTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AudioTracks.SelectedItem is TrackChoice choice) _player.SetAudioTrack(choice.Id);
    }
    private void SubtitleTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubtitleTracks.SelectedItem is TrackChoice choice) _player.SetSubtitleTrack(choice.Id);
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        ResetPlayerOverlay();
        HideVideoSurface();
        _player.Stop();
        PlayerPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        BrowserPage.Visibility = Visibility.Visible;

        // Восстанавливаем статус-бар и отступы интерфейса каталога
        WindowFooter.Visibility = Visibility.Visible;
        MainContentGrid.Margin = new Thickness(18, 0, 18, 12);
        Grid.SetRowSpan(MainContentGrid, 1);

        if (!_browserReady) StartupOverlay.Visibility = Visibility.Visible;
        if (BrowserView.CoreWebView2 is null) return;
        BrowserView.Focus();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ResetPlayerOverlay();
        HideVideoSurface();
        _player.Stop();
        BrowserPage.Visibility = Visibility.Collapsed;
        PlayerPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;

        // Восстанавливаем статус-бар и отступы интерфейса настроек
        WindowFooter.Visibility = Visibility.Visible;
        MainContentGrid.Margin = new Thickness(18, 0, 18, 12);
        Grid.SetRowSpan(MainContentGrid, 1);
    }

    private void HideVideoSurface()
    {
        // VideoView reparents its content into a separate top-level window.
        // Collapsing PlayerPage alone does not hide that window's controls.
        PlayerOverlay.Visibility = Visibility.Collapsed;
        VideoSurface.Visibility = Visibility.Collapsed;
        if (Window.GetWindow(PlayerOverlay) is { } foreground && foreground != this)
            foreground.Hide();
    }

    private void Food_Click(object sender, RoutedEventArgs e)
    {
        if (_foodPanel is null)
        {
            _foodPanel = new FoodPanel(_foodProfileRoot, uri =>
            {
                if (_fullscreen) ToggleFullscreen();
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception) { FooterStatus.Text = "Не удалось открыть браузер."; }
            });
            _foodPanel.CloseRequested += () => { FoodPanelHost.Visibility = Visibility.Collapsed; PlayerOverlay.Focus(); };
            _foodPanel.ExpandRequested += () =>
            {
                _foodExpanded = !_foodExpanded;
                _foodPanel.SetExpanded(_foodExpanded);
                UpdateFoodPanelWidth();
            };
            FoodPanelHost.Child = _foodPanel;
        }
        PlayerSettingsPanel.Visibility = Visibility.Collapsed;
        UpdateFoodPanelWidth();
        FoodPanelHost.Visibility = FoodPanelHost.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ShowPlayerControls();
    }

    private void UpdateFoodPanelWidth()
    {
        var available = Math.Max(1, PlayerOverlay.ActualWidth - FoodPanelHost.Margin.Left - FoodPanelHost.Margin.Right);
        FoodPanelHost.Width = Math.Min(available, _foodExpanded
            ? Math.Clamp(PlayerOverlay.ActualWidth * 0.72, 640, 960)
            : Math.Clamp(PlayerOverlay.ActualWidth * 0.4, 360, 460));
    }

    private void Player_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(PlayerOverlay);
        if (_lastPointer == point) return;
        _lastPointer = point;
        _keyboardNavigation = false;
        if (PlayerPage.Visibility == Visibility.Visible) ShowPlayerControls();
    }

    private void Video_MouseDown(object sender, MouseButtonEventArgs e)
    {
        PlayerOverlay.Focus();
        ShowPlayerControls();
        if (PlayerSettingsPanel.Visibility == Visibility.Visible)
        {
            PlayerSettingsPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            _clickTimer.Stop();
            ToggleFullscreen();
        }
        else { _clickTimer.Stop(); _clickTimer.Start(); }
        e.Handled = true;
    }

    private void ShowPlayerControls()
    {
        if (PlayerPage.Visibility != Visibility.Visible) return;
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            AnimateControls(1);
        }
        PlayerOverlay.Cursor = Cursors.Arrow;
        _controlsTimer.Stop();
        _controlsTimer.Start();
    }

    private void HidePlayerControls()
    {
        if (PlayerPage.Visibility != Visibility.Visible) { _controlsTimer.Stop(); return; }
        if (!_player.MediaPlayer.IsPlaying || AudioTracks.IsDropDownOpen || SubtitleTracks.IsDropDownOpen || _seeking
            || FoodPanelHost.Visibility == Visibility.Visible || PlayerSettingsPanel.Visibility == Visibility.Visible || PlayerBottomBar.IsMouseOver || PlayerTopBar.IsMouseOver
            || (_keyboardNavigation && (PlayerBottomBar.IsKeyboardFocusWithin || PlayerTopBar.IsKeyboardFocusWithin))) return;
        _controlsTimer.Stop();
        _controlsVisible = false;
        AnimateControls(0);
        SeekPreview.Visibility = Visibility.Collapsed;
        PlayerOverlay.Cursor = Cursors.None;
    }

    private void AnimateControls(double opacity)
    {
        foreach (var bar in new[] { PlayerTopBar, PlayerBottomBar })
        {
            bar.IsHitTestVisible = opacity > 0;
            bar.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(180)));
        }
    }

    private void ResetPlayerOverlay()
    {
        _controlsTimer.Stop();
        _clickTimer.Stop();
        _seeking = false;
        _pendingSeekSeconds = null;
        _seekAnchorSeconds = null;
        _seekReadySamples = 0;
        _seekFirstReadyAt = null;
        SeekBufferingPanel.Visibility = Visibility.Collapsed;
        _previewTimer.Stop();
        _previewCancellation?.Cancel();
        _keyboardNavigation = false;
        PlayerOverlay.Cursor = Cursors.Arrow;
        PlayerSettingsPanel.Visibility = Visibility.Collapsed;
        FoodPanelHost.Visibility = Visibility.Collapsed;
        SeekPreview.Visibility = Visibility.Collapsed;
        _controlsVisible = true;
        AnimateControls(1);
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        ErrorOverlay.Visibility = Visibility.Collapsed;
        StartupOverlay.Visibility = Visibility.Visible;
        SetStatus("Восстанавливаем локальные сервисы…", true);
        try
        {
            if (RecoverRequested is not null)
                foreach (Func<Task> handler in RecoverRequested.GetInvocationList()) await handler();
        }
        catch (Exception ex) { SetStatus(ex.Message, false, true); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        OpenLogsRequested?.Invoke();
        var path = LogPathText.Text;
        if (Directory.Exists(path))
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
            catch (Win32Exception) { SetStatus("Не удалось открыть папку журналов.", false); }
        }
    }

    private void ServicesMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось определить границы монитора для полноэкранного режима.");
            _windowStyle = WindowStyle; _resizeMode = ResizeMode; _windowState = WindowState;
            _topmost = Topmost;
            _restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _minimumWidth = MinWidth;
            _minimumHeight = MinHeight;
            MinWidth = 0;
            MinHeight = 0;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            if (!SetWindowPos(handle, HwndTopmost, info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, SwpFrameChanged))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось развернуть окно на весь монитор.");
            _fullscreen = true;
            FullscreenButton.Content = "\uE73F";
            FullscreenButton.ToolTip = "Выйти из полного экрана (Esc / F)";
        }
        else
        {
            Topmost = _topmost;
            WindowState = WindowState.Normal;
            ResizeMode = _resizeMode;
            WindowStyle = _windowStyle;
            MinWidth = _minimumWidth;
            MinHeight = _minimumHeight;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            WindowState = _windowState;
            _fullscreen = false;
            FullscreenButton.Content = "\uE740";
            FullscreenButton.ToolTip = "На весь экран (F / F11)";
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (FoodPanelHost.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { FoodPanelHost.Visibility = Visibility.Collapsed; PlayerOverlay.Focus(); e.Handled = true; return; }
            if (FoodPanelHost.IsKeyboardFocusWithin) return;
        }
        if (PlayerPage.Visibility == Visibility.Visible)
        {
            ShowPlayerControls();
            if (e.Key == Key.Tab) { _keyboardNavigation = true; return; }
            if (AudioTracks.IsDropDownOpen || SubtitleTracks.IsDropDownOpen) return;
            if (e.Key == Key.Escape && PlayerSettingsPanel.Visibility == Visibility.Visible)
            { PlayerSettingsPanel.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        }
        if (e.Key == Key.F11 || (e.Key == Key.F && PlayerPage.Visibility == Visibility.Visible))
        { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _fullscreen) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.Escape && (PlayerPage.Visibility == Visibility.Visible || SettingsPage.Visibility == Visibility.Visible))
        { Home_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (PlayerPage.Visibility != Visibility.Visible || AudioTracks.IsKeyboardFocusWithin || SubtitleTracks.IsKeyboardFocusWithin) return;
        if (e.Key is Key.Space or Key.K) { TogglePlayback(); e.Handled = true; }
        else if (e.Key is Key.J or Key.Left) { SeekPlayback(-10); e.Handled = true; }
        else if (e.Key is Key.L or Key.Right) { SeekPlayback(10); e.Handled = true; }
        else if (e.Key == Key.M) { ToggleMute(); e.Handled = true; }
        else if (e.Key == Key.Up) { VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5); e.Handled = true; }
        else if (e.Key == Key.Down) { VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); e.Handled = true; }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _foodPanel?.Dispose();
        _controlsTimer.Stop();
        _clickTimer.Stop();
        _previewTimer.Stop();
        _previewCancellation?.Cancel();
        ExitRequested?.Invoke();
        VideoSurface.MediaPlayer = null;
        VideoSurface.Dispose();
        _player.Dispose();
    }
}
