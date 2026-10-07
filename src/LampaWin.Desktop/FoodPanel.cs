using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LampaWin.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WebView2 = Microsoft.Web.WebView2.Wpf.WebView2CompositionControl;

namespace LampaWin.Desktop;

// Lives in VideoView.Content's foreground surface, not a separate application window.
public sealed class FoodPanel : Grid, IDisposable
{
    private readonly string _profileRoot;
    private readonly Action<Uri> _external;
    private readonly StackPanel _picker = new() { Margin = new Thickness(18) };
    private readonly Grid _sites = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 8, 14, 8), Foreground = Brushes.LightGray, FontSize = 11 };
    private readonly Dictionary<string, WebView2> _browsers = [];
    private readonly List<WebView2> _popups = [];
    private FoodService? _selected;
    private bool _disposed;
    public event Action? CloseRequested;

    public FoodPanel(string profileRoot, Action<Uri> external)
    {
        _profileRoot = profileRoot;
        _external = external;
        Background = new SolidColorBrush(Color.FromRgb(29, 29, 35));
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bar = new DockPanel { Margin = new Thickness(8), LastChildFill = false };
        var close = MakeButton("×", (_, _) => CloseRequested?.Invoke());
        DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
        var choose = MakeButton("← Сервисы", (_, _) => ChooseService());
        bar.Children.Add(choose);
        bar.Children.Add(MakeButton("‹", (_, _) => { if (ActiveBrowser()?.CoreWebView2 is { CanGoBack: true } core) core.GoBack(); }));
        var reload = MakeButton("↻", (_, _) => ActiveBrowser()?.Reload()); bar.Children.Add(reload);
        var browser = MakeButton("↗", (_, _) => { if (_selected is { } service) _external(service.HomeUri); });
        browser.ToolTip = "Открыть сервис в обычном браузере"; bar.Children.Add(browser);
        Children.Add(bar);
        SetRow(_sites, 1); Children.Add(_sites);
        SetRow(_picker, 1); Children.Add(_picker);
        _picker.Children.Add(new TextBlock { Text = "Еда к просмотру", Foreground = Brushes.White, FontSize = 23, Margin = new Thickness(0, 8, 0, 20) });
        foreach (var service in FoodServices.All)
        {
            var button = MakeButton((service.Id == "burgerking" ? "🍔  " : "Я  ") + service.Title, async (_, _) => await SelectServiceAsync(service));
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Padding = new Thickness(16, 24, 16, 24);
            button.Margin = new Thickness(0, 0, 0, 12);
            button.FontSize = 18;
            _picker.Children.Add(button);
        }
        SetRow(_status, 2); Children.Add(_status);
        ChooseService();
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(2), Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(48, 48, 58)) };
        button.Click += click;
        return button;
    }

    public void ChooseService()
    {
        foreach (var popup in _popups.ToArray()) { _sites.Children.Remove(popup); popup.Dispose(); }
        _popups.Clear();
        _selected = null;
        _sites.Visibility = Visibility.Collapsed;
        _picker.Visibility = Visibility.Visible;
        _status.Text = "Вход — на официальном сайте. Сеанс сохраняется на этом компьютере; сервис может запросить повторный вход. Фильм продолжает играть.";
    }

    private WebView2? ActiveBrowser() => _sites.Children.OfType<WebView2>().LastOrDefault(x => x.Visibility == Visibility.Visible);

    public async Task SelectServiceAsync(FoodService service)
    {
        if (_disposed) return;
        var profile = FoodServices.ProfileDirectory(_profileRoot, service);
        _selected = service;
        _picker.Visibility = Visibility.Collapsed;
        _sites.Visibility = Visibility.Visible;
        foreach (WebView2 view in _sites.Children) view.Visibility = Visibility.Collapsed;
        _status.Text = "Загрузка · " + service.Title;
        if (_browsers.TryGetValue(service.Id, out var existing)) { existing.Visibility = Visibility.Visible; return; }
        var browser = new WebView2();
        _browsers.Add(service.Id, browser); _sites.Children.Add(browser);
        try
        {
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            if (_disposed) return;
            await InitializeAsync(browser, environment);
            if (!_disposed) browser.CoreWebView2.Navigate(service.HomeUri.AbsoluteUri);
        }
        catch (Exception) { if (!_disposed) { _status.Text = "Сайт не открылся. Попробуй ↻ или ↗ для обычного браузера."; _browsers.Remove(service.Id); _sites.Children.Remove(browser); browser.Dispose(); } }
    }

    private async Task InitializeAsync(WebView2 browser, CoreWebView2Environment environment)
    {
        await browser.EnsureCoreWebView2Async(environment);
        if (_disposed) return;
        var core = browser.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.IsMuted = true;
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri == "about:blank") return;
            if (!FoodServices.IsSafeNavigation(e.Uri)) { e.Cancel = true; _status.Text = "Эта ссылка требует отдельного приложения или не использует HTTPS."; }
            else if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) _status.Text = "🔒 " + uri.Authority;
        };
        core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) _status.Text = "Не удалось загрузить сайт. Попробуй ↻ или ↗ для обычного браузера."; };
        core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.DownloadStarting += (_, e) => { e.Cancel = true; _status.Text = "Скачивание приложений отключено. Для него используй обычный браузер (↗)."; };
        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            // OAuth/payment windows stay inside this panel and retain their opener/session.
            if (e.Uri != "about:blank" && !FoodServices.IsSafeNavigation(e.Uri)) return;
            using var deferral = e.GetDeferral();
            var popup = new WebView2();
            try
            {
                if (_disposed) return;
                _popups.Add(popup); _sites.Children.Add(popup);
                await InitializeAsync(popup, environment);
                if (_disposed) return;
                e.NewWindow = popup.CoreWebView2;
                browser.Visibility = Visibility.Collapsed;
                popup.CoreWebView2.WindowCloseRequested += (_, _) =>
                { _sites.Children.Remove(popup); _popups.Remove(popup); popup.Dispose(); browser.Visibility = Visibility.Visible; };
            }
            catch (Exception) { _sites.Children.Remove(popup); _popups.Remove(popup); popup.Dispose(); if (!_disposed) _status.Text = "Окно входа не открылось. Попробуй обычный браузер (↗)."; }
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var browser in _browsers.Values.Concat(_popups)) browser.Dispose();
        _browsers.Clear(); _popups.Clear(); _sites.Children.Clear();
    }
}
