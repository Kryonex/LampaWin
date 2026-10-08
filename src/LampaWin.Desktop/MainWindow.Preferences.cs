using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LampaWin.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LampaWin.Desktop;

public partial class MainWindow
{
    private AppPaths? _appPaths;
    private DesktopPreferences _preferences = new();
    private DispatcherTimer? _preferencesTimer;
    private bool _preferencesReady;
    private bool _preferredAudioApplied, _preferredSubtitleApplied;
    private MediaRequest? _lastRequest;
    private string? _nextTitle, _completedSession;
    private DispatcherTimer? _nextTimer;
    private int _nextSeconds;
    private DateTimeOffset _openedAt;
    private string _streamDetail = "";
    private PlaybackProgress? _lastUiProgress;
    public CancellationToken LifetimeToken { get; set; }
    public event Action<bool>? SearchModeChanged;
    public event Action<string>? NextEpisodeRequested;
    public event Action<string>? EpisodeDismissed;
    public event Action<string>? RetryPlaybackRequested;
    public Func<object>? DiagnosticsProvider { get; set; }
    public bool ShowAllSearchResults => _preferences.ShowAllSearchResults;
    private sealed record LanguageChoice(string Name, string Code)
    { public override string ToString() => Name; }
    private sealed record DeviceChoice(string Name, string Id)
    { public override string ToString() => Name; }

    public void InitializePreferences(AppPaths paths)
    {
        _appPaths = paths;
        _preferences = DesktopPreferences.Load(paths.SettingsFile);
        _preferencesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _preferencesTimer.Tick += (_, _) => { _preferencesTimer.Stop(); SavePreferences(); };
        PreviewEnabledCheck.IsChecked = _preferences.PreviewEnabled;
        AutoNextCheck.IsChecked = _preferences.AutoNextEpisode;
        ShowAllSearchCheck.IsChecked = _preferences.ShowAllSearchResults;
        var languages = new[] { new LanguageChoice("Как в источнике", ""), new LanguageChoice("Русский", "rus"), new LanguageChoice("Английский", "eng") };
        PreferredAudio.ItemsSource = languages;
        PreferredSubtitles.ItemsSource = new[] { new LanguageChoice("Выключены", "off") }.Concat(languages).ToArray();
        PreferredAudio.SelectedItem = languages.FirstOrDefault(x => x.Code == _preferences.AudioLanguage) ?? languages[0];
        PreferredSubtitles.SelectedItem = PreferredSubtitles.Items.OfType<LanguageChoice>().FirstOrDefault(x => x.Code == _preferences.SubtitleLanguage);
        _player.PreviewEnabled = _preferences.PreviewEnabled;
        _player.SetOutputDevice(_preferences.OutputDevice);
        _volumeBeforeMute = Math.Clamp(double.IsFinite(_preferences.VolumeBeforeMute) ? _preferences.VolumeBeforeMute : 80, 1, 100);
        VolumeSlider.Value = Math.Clamp(double.IsFinite(_preferences.Volume) ? _preferences.Volume : 80, 0, 100);
        Width = Math.Clamp(double.IsFinite(_preferences.WindowWidth) ? _preferences.WindowWidth : 1440, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Math.Clamp(double.IsFinite(_preferences.WindowHeight) ? _preferences.WindowHeight : 900, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        if (_preferences.WindowLeft is { } left && _preferences.WindowTop is { } top && double.IsFinite(left) && double.IsFinite(top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
        }
        if (_preferences.Maximized) WindowState = WindowState.Maximized;
        _preferencesReady = true;
    }

    private void Preferences_Changed(object sender, RoutedEventArgs e)
    {
        if (!_preferencesReady) return;
        _preferences.PreviewEnabled = PreviewEnabledCheck.IsChecked == true;
        _preferences.AutoNextEpisode = AutoNextCheck.IsChecked == true;
        _preferences.ShowAllSearchResults = ShowAllSearchCheck.IsChecked == true;
        _preferences.AudioLanguage = (PreferredAudio.SelectedItem as LanguageChoice)?.Code ?? "";
        _preferences.SubtitleLanguage = (PreferredSubtitles.SelectedItem as LanguageChoice)?.Code ?? "off";
        _player.PreviewEnabled = _preferences.PreviewEnabled;
        if (!_preferences.PreviewEnabled) { _previewCancellation?.Cancel(); SeekPreview.Visibility = Visibility.Collapsed; }
        _preferredAudioApplied = _preferredSubtitleApplied = false;
        ApplyPreferredTracks();
        SearchModeChanged?.Invoke(_preferences.ShowAllSearchResults);
        SchedulePreferencesSave();
    }

    private void SchedulePreferencesSave()
    {
        if (!_preferencesReady) return;
        _preferencesTimer!.Stop(); _preferencesTimer.Start();
    }

    private void SavePreferences()
    {
        if (!_preferencesReady || _appPaths is null) return;
        _preferences.Volume = VolumeSlider.Value;
        _preferences.VolumeBeforeMute = _volumeBeforeMute;
        try { _preferences.Save(_appPaths.SettingsFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FooterStatus.Text = "Не удалось сохранить настройки: проверьте место и доступ к папке данных."; }
    }

    private void SaveWindowPreferences()
    {
        _preferencesTimer?.Stop();
        if (!_preferencesReady) return;
        var bounds = _fullscreen ? _restoreBounds : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _preferences.WindowWidth = bounds.Width; _preferences.WindowHeight = bounds.Height;
        _preferences.WindowLeft = bounds.Left; _preferences.WindowTop = bounds.Top;
        _preferences.Maximized = (_fullscreen ? _windowState : WindowState) == WindowState.Maximized;
        SavePreferences();
    }

    private void ApplyPreferredTracks()
    {
        if (!_preferencesReady) return;
        if (!_preferredAudioApplied && _preferences.AudioLanguage.Length > 0)
        {
            var id = _player.FindTrack(_preferences.AudioLanguage, false);
            if (id is { } audio) { _player.SetAudioTrack(audio); AudioTracks.SelectedItem = AudioTracks.Items.OfType<TrackChoice>().FirstOrDefault(x => x.Id == audio); _preferredAudioApplied = true; }
        }
        if (!_preferredSubtitleApplied)
        {
            var id = _preferences.SubtitleLanguage == "off" ? -1 : _player.FindTrack(_preferences.SubtitleLanguage, true);
            if (id is { } subtitle) { _player.SetSubtitleTrack(subtitle); SubtitleTracks.SelectedItem = SubtitleTracks.Items.OfType<TrackChoice>().FirstOrDefault(x => x.Id == subtitle); _preferredSubtitleApplied = true; }
        }
    }

    public void SetNextEpisode(string session, string? title)
    {
        if (_lastRequest?.SessionId == session) _nextTitle = title;
    }

    private bool OfferNextEpisode(string session)
    {
        if (_lastRequest?.SessionId != session || string.IsNullOrWhiteSpace(_nextTitle)) return false;
        ResetPlayerOverlay();
        StreamStatusPanel.Visibility = Visibility.Collapsed;
        _completedSession = session;
        NextEpisodeText.Text = _nextTitle;
        NextEpisodePanel.Visibility = Visibility.Visible;
        NextEpisodeButton.Focus();
        if (_preferences.AutoNextEpisode)
        {
            _nextSeconds = 10;
            _nextTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _nextTimer.Tick -= NextTimer_Tick; _nextTimer.Tick += NextTimer_Tick;
            NextEpisodeText.Text = $"{_nextTitle}\nНачнётся через {_nextSeconds} с. Отмена возвращает в каталог.";
            _nextTimer.Start();
        }
        return true;
    }

    private void NextTimer_Tick(object? sender, EventArgs e)
    {
        NextEpisodeText.Text = $"{_nextTitle}\nНачнётся через {--_nextSeconds} с. Отмена возвращает в каталог.";
        if (_nextSeconds <= 0) NextEpisode_Click(this, new RoutedEventArgs());
    }
    private void NextEpisode_Click(object sender, RoutedEventArgs e)
    {
        var session = _completedSession;
        ResetEpisodeOffer();
        if (session is not null) NextEpisodeRequested?.Invoke(session);
    }
    private void ResetEpisodeOffer()
    {
        _nextTimer?.Stop(); _completedSession = null;
        if (NextEpisodePanel is not null) NextEpisodePanel.Visibility = Visibility.Collapsed;
    }
    private void RetryPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRequest is { } request) RetryPlaybackRequested?.Invoke(request.SessionId);
    }
    public void SetStreamDetail(string session, string detail)
    {
        if (_lastRequest?.SessionId != session) return;
        _streamDetail = detail;
        if (_lastUiProgress is { } progress) UpdateStreamStatus(progress);
    }
    private void UpdateStreamStatus(PlaybackProgress progress)
    {
        if (_lastRequest?.SessionId != progress.SessionId) return;
        _lastUiProgress = progress;
        var waiting = progress.State == PlaybackState.Opening || progress.IsBuffering;
        var failed = progress.State == PlaybackState.Error;
        StreamStatusPanel.Visibility = (failed || waiting && DateTimeOffset.UtcNow - _openedAt > TimeSpan.FromSeconds(3)) && NextEpisodePanel.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        if (StreamStatusPanel.Visibility == Visibility.Visible)
            StreamStatusText.Text = failed ? "Не удалось открыть видео. Можно повторить или выбрать другую раздачу.\n" + _streamDetail
                : (_streamDetail.Length > 0 ? _streamDetail : "Ожидаем данные источника… Можно повторить или выбрать другую раздачу.");
        if (!waiting) _openedAt = DateTimeOffset.UtcNow;
    }

    public void PrepareClose() => _foodPanel?.PrepareClose();
    public void RecreateBrowser()
    {
        _documentLoaded = _browserReady = false;
        var parent = (Border)BrowserView.Parent;
        BrowserView.Dispose();
        BrowserView = new Microsoft.Web.WebView2.Wpf.WebView2();
        parent.Child = BrowserView;
    }
    private void RefreshOutputDevices()
    {
        OutputDevices.SelectionChanged -= OutputDevices_SelectionChanged;
        var devices = new[] { new DeviceChoice("Устройство Windows по умолчанию", "") }
            .Concat(_player.OutputDevices.Select(x => new DeviceChoice(x.Name, x.Id))).ToArray();
        OutputDevices.ItemsSource = devices;
        OutputDevices.SelectedItem = devices.FirstOrDefault(x => x.Id == _preferences.OutputDevice) ?? devices[0];
        OutputDevices.SelectionChanged += OutputDevices_SelectionChanged;
    }
    private void OutputDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutputDevices.SelectedItem is not DeviceChoice device) return;
        _preferences.OutputDevice = device.Id; _player.SetOutputDevice(device.Id); SchedulePreferencesSave();
    }
    private void OpenSubtitle_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Субтитры|*.srt;*.ass;*.ssa;*.vtt;*.sub|Все файлы|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true && !_player.AddSubtitle(dialog.FileName)) SetStreamDetail(_lastRequest?.SessionId ?? "", "VLC не смог подключить выбранные субтитры.");
    }
    private void ApplyDelays_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(AudioDelayText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var audio) && double.IsFinite(audio)
            && double.TryParse(SubtitleDelayText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var subtitles) && double.IsFinite(subtitles))
        { _player.SetAudioDelay(audio); _player.SetSubtitleDelay(subtitles); }
        else MessageBox.Show(this, "Введите задержку числом в миллисекундах.", "Настройки видео");
    }
    private void AspectRatio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_player is null) return;
        _player.SetAspectRatio(AspectRatios.SelectedIndex <= 0 ? null : (AspectRatios.SelectedItem as ComboBoxItem)?.Content?.ToString());
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        try { if (await DesktopUpdater.CheckAndOfferAsync(LifetimeToken, manual: true)) Close(); }
        catch (OperationCanceledException) { }
    }
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "LampaWin-diagnostics.json", Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(DiagnosticsProvider?.Invoke() ?? new { version = typeof(MainWindow).Assembly.GetName().Version?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            FooterStatus.Text = "Диагностика сохранена без адресов, ключей, профилей и содержимого журналов.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FooterStatus.Text = "Не удалось записать отчёт диагностики."; }
    }
    private async void DataSize_Click(object sender, RoutedEventArgs e)
    {
        if (_appPaths is null) return;
        var root = _appPaths.DataRoot;
        DataSizeText.Text = "Считаем размер…";
        try
        {
            var bytes = await Task.Run(() => Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(path => { try { return new FileInfo(path).Length; } catch (IOException) { return 0L; } }));
            DataSizeText.Text = $"Данные приложения: {bytes / 1048576d:F1} МБ. Очистка кеша сохраняет вход, историю и настройки. Выход из еды удаляет сеансы только этих сервисов.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DataSizeText.Text = "Не удалось прочитать размер данных."; }
    }
    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (BrowserView.CoreWebView2 is { } core) await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            if (_foodPanel is not null) await _foodPanel.ClearDataAsync(false);
            DataSizeText.Text = "Кеш страниц очищен. Настройки, история и вход сохранены.";
        }
        catch (Exception) { DataSizeText.Text = "Не удалось очистить кеш. Попробуйте после восстановления интерфейса."; }
    }
    private async void ResetFood_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_foodPanel is null) { Food_Click(this, new RoutedEventArgs()); FoodPanelHost.Visibility = Visibility.Collapsed; }
            await _foodPanel!.ClearDataAsync(true);
            DataSizeText.Text = "Сеансы сервисов еды удалены. При следующем открытии потребуется вход.";
        }
        catch (Exception) { DataSizeText.Text = "Не удалось удалить сеансы еды. Повторите после закрытия окон входа."; }
    }
}
