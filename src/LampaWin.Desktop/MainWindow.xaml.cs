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
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LampaWin.Desktop;

public partial class MainWindow : Window
{
    private readonly PlayerController _player;
    private readonly ObservableCollection<string> _sources = [];
    private IReadOnlyList<string> _sourceWarnings = [];
    private string _currentStatus = "Ожидание запуска";
    private bool _browserReady;
    private bool _documentLoaded;
    private bool _seeking;
    private bool _fullscreen;
    private string _audioTrackKey = "";
    private string _subtitleTrackKey = "";
    private WindowStyle _windowStyle;
    private ResizeMode _resizeMode;
    private WindowState _windowState;
    private readonly DispatcherTimer _controlsTimer;
    private readonly DispatcherTimer _clickTimer;
    private bool _controlsVisible = true;
    private bool _keyboardNavigation;
    private bool _updatingPosition;
    private double _volumeBeforeMute = 80;
    private Point? _lastPointer;
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    public MainWindow()
    {
        InitializeComponent();
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
            UpdatePosition(0);
            TimeLabel.Text = "00:00 / 00:00";
            MediaTitle.Text = string.IsNullOrWhiteSpace(request.Title) ? "Воспроизведение" : request.Title;
            BrowserPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            PlayerPage.Visibility = Visibility.Visible;
            PlayPauseButton.Content = "\uE769";
            PlayerSettingsPanel.Visibility = Visibility.Collapsed;
            _lastPointer = null;

            // В режиме плеера скрываем нижний статус-бар окна и убираем внешние отступы, чтобы видео занимало всё окно
            WindowFooter.Visibility = Visibility.Collapsed;
            MainContentGrid.Margin = new Thickness(0);
            Grid.SetRowSpan(MainContentGrid, 2);

            PlayerPage.UpdateLayout();
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
        PlaybackChanged?.Invoke(update);
        if (update.State is PlaybackState.Playing) RefreshTracksIfChanged();
        var duration = update.DurationSeconds;
        if (!_seeking && duration > 0)
            UpdatePosition(Math.Clamp(update.PositionSeconds / duration, 0, 1) * PositionSlider.Maximum);
        TimeLabel.Text = $"{FormatTime(update.PositionSeconds)} / {FormatTime(duration)}";
        PositionSlider.IsEnabled = duration > 0 && _player.MediaPlayer.IsSeekable;
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
        ShowPlayerControls();
    }
    private void PositionSlider_MouseUp(object sender, MouseButtonEventArgs e) => CommitSeek();
    private void PositionSlider_LostCapture(object sender, MouseEventArgs e) => CommitSeek();
    private void CommitSeek()
    {
        if (!_seeking) return;
        _player.SeekTo(PositionSlider.Value / PositionSlider.Maximum);
        _seeking = false;
        ShowPlayerControls();
    }
    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking && _player.MediaPlayer.Length > 0)
            TimeLabel.Text = $"{FormatTime(_player.MediaPlayer.Length * PositionSlider.Value / PositionSlider.Maximum / 1000d)} / {FormatTime(_player.MediaPlayer.Length / 1000d)}";
        else if (!_updatingPosition && PositionSlider.IsKeyboardFocusWithin && _player.MediaPlayer.Length > 0)
            _player.SeekTo(PositionSlider.Value / PositionSlider.Maximum);
    }
    private void PositionSlider_MouseMove(object sender, MouseEventArgs e)
    {
        var duration = _player.MediaPlayer.Length / 1000d;
        if (duration <= 0 || PositionSlider.ActualWidth <= 0) return;
        var x = Math.Clamp(e.GetPosition(PositionSlider).X, 0, PositionSlider.ActualWidth);
        SeekPreviewText.Text = FormatTime(duration * x / PositionSlider.ActualWidth);
        SeekPreview.Margin = new Thickness(Math.Clamp(x + 4, 24, Math.Max(24, PlayerOverlay.ActualWidth - 88)), 0, 0, 92);
        SeekPreview.Visibility = Visibility.Visible;
    }
    private void PositionSlider_MouseLeave(object sender, MouseEventArgs e) => SeekPreview.Visibility = Visibility.Collapsed;
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _player.SetVolume(e.NewValue);
        MuteButton.Content = e.NewValue > 0 ? "\uE767" : "\uE74F";
        MuteButton.ToolTip = e.NewValue > 0 ? "Выключить звук (M)" : "Включить звук (M)";
    }
    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
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
        _player.SeekBy(TimeSpan.FromSeconds(seconds));
        ShowPlayerControls();
    }
    private void PlayerSettings_Click(object sender, RoutedEventArgs e)
    {
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
        _player.Stop();
        BrowserPage.Visibility = Visibility.Collapsed;
        PlayerPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;

        // Восстанавливаем статус-бар и отступы интерфейса настроек
        WindowFooter.Visibility = Visibility.Visible;
        MainContentGrid.Margin = new Thickness(18, 0, 18, 12);
        Grid.SetRowSpan(MainContentGrid, 1);
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
            || PlayerSettingsPanel.Visibility == Visibility.Visible || PlayerBottomBar.IsMouseOver || PlayerTopBar.IsMouseOver
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
        _keyboardNavigation = false;
        PlayerOverlay.Cursor = Cursors.Arrow;
        PlayerSettingsPanel.Visibility = Visibility.Collapsed;
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
            _windowStyle = WindowStyle; _resizeMode = ResizeMode; _windowState = WindowState;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
            _fullscreen = true;
            FullscreenButton.Content = "\uE73F";
            FullscreenButton.ToolTip = "Выйти из полного экрана (Esc / F)";
        }
        else
        {
            WindowState = _windowState; ResizeMode = _resizeMode; WindowStyle = _windowStyle;
            _fullscreen = false;
            FullscreenButton.Content = "\uE740";
            FullscreenButton.ToolTip = "На весь экран (F / F11)";
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
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
        _controlsTimer.Stop();
        _clickTimer.Stop();
        ExitRequested?.Invoke();
        VideoSurface.MediaPlayer = null;
        VideoSurface.Dispose();
        _player.Dispose();
    }
}
