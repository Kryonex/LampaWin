using System.IO;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly Dictionary<WebView2, (Window Window, CancelEventHandler Handler)> _popupCloseGuards = [];
    private readonly StackPanel _permissionPrompt = new() { Margin = new Thickness(12, 8, 12, 0), Visibility = Visibility.Collapsed };
    private CoreWebView2PermissionRequestedEventArgs? _permission;
    private CoreWebView2Deferral? _permissionDeferral;
    private WebView2? _permissionBrowser;
    private FoodService? _selected;
    private bool _disposed;
    private int _selectionGeneration;
    private bool _clearingData;
    public event Action? CloseRequested;
    public event Action? ExpandRequested;
    private readonly Button _expand;
    private readonly Button _closePopup;

    public FoodPanel(string profileRoot, Action<Uri> external)
    {
        _profileRoot = profileRoot;
        _external = external;
        Background = new SolidColorBrush(Color.FromRgb(29, 29, 35));
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bar = new DockPanel { Margin = new Thickness(8), LastChildFill = false };
        var close = MakeButton("×", (_, _) => CloseRequested?.Invoke());
        NameButton(close, "Закрыть панель еды");
        DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
        _expand = MakeButton("⤢", (_, _) => ExpandRequested?.Invoke());
        SetExpanded(false);
        DockPanel.SetDock(_expand, Dock.Right); bar.Children.Add(_expand);
        var choose = MakeButton("← Сервисы", (_, _) => ChooseService());
        NameButton(choose, "Выбрать сервис еды");
        bar.Children.Add(choose);
        var back = MakeButton("‹", (_, _) => { if (ActiveBrowser()?.CoreWebView2 is { CanGoBack: true } core) core.GoBack(); });
        NameButton(back, "Предыдущая страница"); bar.Children.Add(back);
        var reload = MakeButton("↻", async (_, _) =>
        {
            if (ActiveBrowser()?.CoreWebView2 is not null) ActiveBrowser()!.Reload();
            else if (_selected is { } selected) await SelectServiceAsync(selected);
        }); NameButton(reload, "Повторить загрузку сайта"); bar.Children.Add(reload);
        _closePopup = MakeButton("✕", (_, _) => CloseActivePopup());
        NameButton(_closePopup, "Закрыть всплывающее окно входа или оплаты");
        _closePopup.Visibility = Visibility.Collapsed;
        bar.Children.Add(_closePopup);
        var browser = MakeButton("↗", (_, _) => { if (_selected is { } service) _external(service.HomeUri); });
        NameButton(browser, "Открыть сервис в обычном браузере"); bar.Children.Add(browser);
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
        SetRow(_permissionPrompt, 2); Children.Add(_permissionPrompt);
        SetRow(_status, 3); Children.Add(_status);
        ChooseService();
    }

    public void SetExpanded(bool expanded)
    {
        _expand.Content = expanded ? "⤡" : "⤢";
        _expand.ToolTip = expanded ? "Свернуть панель" : "Расширить панель для заказа";
        System.Windows.Automation.AutomationProperties.SetName(_expand, (string)_expand.ToolTip);
    }

    private static Button MakeButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(2), Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(48, 48, 58)) };
        button.Click += click;
        return button;
    }

    private static void NameButton(Button button, string name)
    {
        button.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(button, name);
    }

    public void PrepareClose()
    {
        CompletePermission(CoreWebView2PermissionState.Deny);
        foreach (var guard in _popupCloseGuards.Values) guard.Window.Closing -= guard.Handler;
        _popupCloseGuards.Clear();
    }

    private void CloseActivePopup()
    {
        if (_popups.LastOrDefault() is not { } popup) return;
        RemovePopup(popup);
        var previous = _popups.LastOrDefault() ?? (_selected is { } service ? _browsers.GetValueOrDefault(service.Id) : null);
        if (previous is not null) previous.Visibility = Visibility.Visible;
    }

    public void ChooseService()
    {
        _selectionGeneration++;
        CompletePermission(CoreWebView2PermissionState.Deny);
        foreach (var popup in _popups.ToArray()) RemovePopup(popup);
        _popups.Clear();
        _selected = null;
        _sites.Visibility = Visibility.Collapsed;
        _picker.Visibility = Visibility.Visible;
        _status.Text = "Вход — на официальном сайте. Сеанс сохраняется на этом компьютере; сервис может запросить повторный вход. Фильм продолжает играть.";
    }

    private WebView2? ActiveBrowser() => _sites.Children.OfType<WebView2>().LastOrDefault(x => x.Visibility == Visibility.Visible);

    public async Task SelectServiceAsync(FoodService service)
    {
        if (_disposed || _clearingData) return;
        var generation = ++_selectionGeneration;
        foreach (var popup in _popups.ToArray()) RemovePopup(popup);
        CompletePermission(CoreWebView2PermissionState.Deny);
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
            await InitializeAsync(browser, environment, service.Id == "yandex");
            if (_disposed) return;
            if (_browsers.GetValueOrDefault(service.Id) != browser) { browser.Dispose(); return; }
            browser.Visibility = _selected?.Id == service.Id ? Visibility.Visible : Visibility.Collapsed;
            browser.CoreWebView2.Navigate(service.HomeUri.AbsoluteUri);
        }
        catch (Exception) { if (!_disposed) { if (generation == _selectionGeneration) _status.Text = "Сайт не открылся. Попробуй ↻ или ↗ для обычного браузера."; if (_browsers.GetValueOrDefault(service.Id) == browser) _browsers.Remove(service.Id); _sites.Children.Remove(browser); browser.Dispose(); } }
    }

    private async Task InitializeAsync(WebView2 browser, CoreWebView2Environment environment, bool useMobileAgent)
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
        await ConfigureViewportAsync(core, environment, useMobileAgent);
        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind != CoreWebView2PermissionKind.Geolocation) return;
            // Composition WebView's native permission bubble can obscure the page.
            // Keep the decision in the visible WPF panel; never grant it automatically.
            e.Handled = true;
            e.SavesInProfile = false;
            if (_disposed || browser.Visibility != Visibility.Visible || _permission is not null)
            { e.State = CoreWebView2PermissionState.Deny; return; }
            _permission = e; _permissionDeferral = e.GetDeferral(); _permissionBrowser = browser;
            _permissionPrompt.Children.Clear();
            _permissionPrompt.Children.Add(new TextBlock
            {
                Text = $"{new Uri(e.Uri).Host} запрашивает местоположение для доставки.",
                Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(MakeButton("Разрешить", (_, _) => CompletePermission(CoreWebView2PermissionState.Allow)));
            actions.Children.Add(MakeButton("Без геолокации", (_, _) => CompletePermission(CoreWebView2PermissionState.Deny)));
            _permissionPrompt.Children.Add(actions);
            _permissionPrompt.Visibility = Visibility.Visible;
        };
        core.NavigationStarting += (_, e) =>
        {
            if (_permissionBrowser == browser) CompletePermission(CoreWebView2PermissionState.Deny);
            if (e.Uri == "about:blank") return;
            if (!FoodServices.IsSafeNavigation(e.Uri)) { e.Cancel = true; if (ActiveBrowser() == browser) _status.Text = "Эта ссылка требует отдельного приложения или не использует HTTPS."; }
            else if (ActiveBrowser() == browser && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) _status.Text = "🔒 " + uri.Authority;
        };
        core.NavigationCompleted += (_, e) => { if (!e.IsSuccess && ActiveBrowser() == browser) _status.Text = "Не удалось загрузить сайт. Попробуй ↻ или ↗ для обычного браузера."; };
        core.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.DownloadStarting += (_, e) => { e.Cancel = true; _status.Text = "Скачивание приложений отключено. Для него используй обычный браузер (↗)."; };
        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            // OAuth/payment windows stay inside this panel and retain their opener/session.
            if (e.Uri != "about:blank" && !FoodServices.IsSafeNavigation(e.Uri)) return;
            using var deferral = e.GetDeferral();
            var popup = new WebView2();
            var generation = _selectionGeneration;
            try
            {
                if (_disposed) return;
                _popups.Add(popup); _sites.Children.Add(popup);
                _closePopup.Visibility = Visibility.Visible;
                if (Window.GetWindow(popup) is { } host)
                {
                    // WPF WebView2's default window.close handler closes its host.
                    // This host also contains the video and original service.
                    CancelEventHandler guard = (_, closing) => { if (!_disposed) closing.Cancel = true; };
                    host.Closing += guard;
                    _popupCloseGuards.Add(popup, (host, guard));
                }
                // Auth/payment providers keep the normal browser identity but use
                // the panel's responsive viewport and the service's session.
                await InitializeAsync(popup, environment, false);
                if (_disposed) return;
                if (generation != _selectionGeneration || browser.Visibility != Visibility.Visible) { RemovePopup(popup); return; }
                e.NewWindow = popup.CoreWebView2;
                browser.Visibility = Visibility.Collapsed;
                popup.CoreWebView2.WindowCloseRequested += (_, _) =>
                {
                    if (_permissionBrowser == popup) CompletePermission(CoreWebView2PermissionState.Deny);
                    RemovePopup(popup);
                    browser.Visibility = Visibility.Visible;
                };
            }
            catch (Exception) { RemovePopup(popup); if (!_disposed) _status.Text = "Окно входа не открылось. Попробуй обычный браузер (↗)."; }
        };
    }

    private void RemovePopup(WebView2 popup)
    {
        if (_popupCloseGuards.Remove(popup, out var guard)) guard.Window.Closing -= guard.Handler;
        _sites.Children.Remove(popup); _popups.Remove(popup); popup.Dispose();
        if (_popups.Count == 0) _closePopup.Visibility = Visibility.Collapsed;
    }

    private static async Task ConfigureViewportAsync(CoreWebView2 core, CoreWebView2Environment environment, bool useMobileAgent)
    {
        // Yandex serves separate desktop/mobile layouts based on the request.
        // Burger King is responsive with its normal UA; keep that for compatibility.
        if (useMobileAgent)
        {
            var version = Regex.Match(core.Settings.UserAgent, @"Chrome/([\d.]+)").Groups[1].Value;
            if (string.IsNullOrEmpty(version)) version = environment.BrowserVersionString.Split(' ')[0];
            var mobileAgent = $"Mozilla/5.0 (Linux; Android 13; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{version} Mobile Safari/537.36";
            core.Settings.UserAgent = mobileAgent;
            await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", JsonSerializer.Serialize(new
            {
                userAgent = mobileAgent, platform = "Linux armv8l",
                userAgentMetadata = new
                {
                    brands = new[] { new { brand = "Chromium", version = version.Split('.')[0] } },
                    fullVersion = version, platform = "Android", platformVersion = "13.0.0",
                    architecture = "", model = "", mobile = true
                }
            }));
        }
        // Zero width/height follow the real WebView bounds, including resize and DPI.
        await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
            "{\"width\":0,\"height\":0,\"deviceScaleFactor\":0,\"mobile\":true}");
    }

    private void CompletePermission(CoreWebView2PermissionState state)
    {
        var permission = _permission;
        var deferral = _permissionDeferral;
        _permission = null; _permissionDeferral = null; _permissionBrowser = null;
        _permissionPrompt.Visibility = Visibility.Collapsed;
        if (permission is null) return;
        permission.State = state;
        deferral?.Dispose();
    }

    public async Task ClearDataAsync(bool sessions)
    {
        if (_clearingData || _disposed) return;
        _clearingData = true;
        try
        {
        ChooseService();
        // Also clear profiles that were used on a previous app run but not opened now.
        foreach (var service in FoodServices.All)
        {
            var profile = FoodServices.ProfileDirectory(_profileRoot, service);
            if (!Directory.Exists(profile)) continue;
            CoreWebView2Controller? temporary = null;
            try
            {
                var browser = _browsers.GetValueOrDefault(service.Id);
                if (browser?.CoreWebView2 is null)
                {
                    var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                    temporary = await environment.CreateCoreWebView2ControllerAsync(new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle);
                    temporary.IsVisible = false;
                }
                var core = temporary?.CoreWebView2 ?? browser!.CoreWebView2;
                await core.Profile.ClearBrowsingDataAsync(sessions ? CoreWebView2BrowsingDataKinds.AllProfile : CoreWebView2BrowsingDataKinds.DiskCache);
            }
            finally { temporary?.Close(); }
        }
        if (sessions)
        {
            foreach (var browser in _browsers.Values) browser.Dispose();
            _browsers.Clear(); _sites.Children.Clear();
        }
        }
        finally { _clearingData = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        CompletePermission(CoreWebView2PermissionState.Deny);
        PrepareClose();
        _disposed = true;
        foreach (var popup in _popups.ToArray()) RemovePopup(popup);
        foreach (var browser in _browsers.Values.Concat(_popups)) browser.Dispose();
        _browsers.Clear(); _popups.Clear(); _sites.Children.Clear();
    }
}
