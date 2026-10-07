using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LampaWin.Core;

public sealed class LocalGateway : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private RuntimeSnapshot _runtime;
    private readonly HttpClient _client;
    private readonly HttpClient _publicClient;
    private readonly ConcurrentDictionary<string, (Uri Url, DateTimeOffset Expires)> _downloads = new();
    private readonly string _loginToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _mediaToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _cookie = "LampaWin_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private WebApplication? _app;
    private int _loginUsed;
    public LocalGateway(AppPaths paths, RuntimeSnapshot runtime)
    {
        _paths = paths;
        _runtime = runtime;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 24,
            ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _publicClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5), ConnectCallback = PublicNetworkPolicy.ConnectAsync
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public Uri Origin { get; private set; } = null!;
    public Uri LoginUri => new(Origin, "bootstrap?token=" + _loginToken);
    public void UpdateRuntime(RuntimeSnapshot snapshot) => Volatile.Write(ref _runtime, snapshot);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path.Combine(_paths.LampaRoot, "index.html")))
            throw new FileNotFoundException("Bundled Lampa is missing. Reinstall the application.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = _paths.InstallRoot });
        builder.Logging.ClearProviders(); // HTTP query strings and capability URLs must not enter logs.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 1024 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Cache-Control"] = "no-store";
            var path = context.Request.Path.Value ?? "/";
            if (path == "/bootstrap")
            {
                if (!FixedEquals(context.Request.Query["token"].ToString(), _loginToken) || Interlocked.Exchange(ref _loginUsed, 1) != 0)
                { context.Response.StatusCode = 403; return; }
                context.Response.Cookies.Append(_cookie, _session, new CookieOptions
                { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true, Path = "/" });
                context.Response.Redirect("/");
                return;
            }
            if (path.StartsWith("/media/", StringComparison.Ordinal))
            {
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                { context.Response.StatusCode = 405; return; }
                var expectedPrefix = "/media/" + _mediaToken + "/";
                if (!path.StartsWith(expectedPrefix, StringComparison.Ordinal)) { context.Response.StatusCode = 403; return; }
                var endpoint = path[expectedPrefix.Length..];
                if (endpoint != "stream" && !endpoint.StartsWith("stream/", StringComparison.Ordinal)) { context.Response.StatusCode = 404; return; }
                await ProxyAsync(context, Backend.TorrServer, "/" + endpoint);
                return;
            }
            if (path.StartsWith("/download/", StringComparison.Ordinal))
            {
                var token = path["/download/".Length..];
                if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) ||
                    !_downloads.TryGetValue(token, out var item) || item.Expires < DateTimeOffset.UtcNow)
                { context.Response.StatusCode = 403; return; }
                await ProxyDownloadAsync(context, item.Url);
                return;
            }
            if (!FixedEquals(context.Request.Cookies[_cookie], _session)) { context.Response.StatusCode = 401; return; }
            var requestOrigin = context.Request.Headers.Origin.ToString();
            if (requestOrigin.Length > 0 && !requestOrigin.Equals(Origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            { context.Response.StatusCode = 403; return; }
            if (path.StartsWith("/torrserver", StringComparison.Ordinal))
            {
                var rest = path["/torrserver".Length..];
                if (rest.Length > 0 && rest[0] != '/') { context.Response.StatusCode = 404; return; }
                await ProxyAsync(context, Backend.TorrServer, rest.Length == 0 ? "/" : rest);
                return;
            }
            if (path.StartsWith("/jackett/api/v2.0/indexers/", StringComparison.Ordinal) && path.EndsWith("/results", StringComparison.Ordinal))
            {
                if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
                await ProxyAsync(context, Backend.Jackett, path["/jackett".Length..], rewriteResults: true);
                return;
            }
            if (path == "/lampawin-bridge.js")
            {
                context.Response.ContentType = "text/javascript; charset=utf-8";
                await context.Response.WriteAsync(BridgeProtocol.Script, context.RequestAborted);
                return;
            }
            if (path == "/lampawin-config.json")
            {
                var current = Volatile.Read(ref _runtime);
                await context.Response.WriteAsJsonAsync(new { version = BridgeProtocol.Version,
                    torrServerUrl = new Uri(Origin, "torrserver").AbsoluteUri,
                    jackettUrl = new Uri(Origin, "jackett").AbsoluteUri, jackettKey = "managed",
                    indexers = current.Indexers, warnings = current.Warnings }, context.RequestAborted);
                return;
            }
            if (path is "/" or "/index.html")
            {
                var html = await File.ReadAllTextAsync(Path.Combine(_paths.LampaRoot, "index.html"), context.RequestAborted);
                const string tag = "<script src=\"/lampawin-bridge.js\" defer></script>";
                html = html.Contains("</head>", StringComparison.OrdinalIgnoreCase)
                    ? html.Replace("</head>", tag + "</head>", StringComparison.OrdinalIgnoreCase) : tag + html;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(html, context.RequestAborted);
                return;
            }
            await next(context);
        });
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(_paths.LampaRoot),
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = ctx => ctx.Context.Response.Headers["Cache-Control"] = "private, max-age=3600"
        });
        _app.Run(context => { context.Response.StatusCode = 404; return Task.CompletedTask; });
        await _app.StartAsync(cancellationToken);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Origin = new Uri(address.EndsWith('/') ? address : address + '/');
    }

    public bool TryCreateMediaUri(string value, out Uri media)
    {
        media = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !BridgeProtocol.IsTrustedSource(value, Origin) ||
            !(uri.AbsolutePath == "/torrserver/stream" || uri.AbsolutePath.StartsWith("/torrserver/stream/", StringComparison.Ordinal)) || uri.UserInfo.Length > 0)
            return false;
        media = new Uri(Origin, "media/" + _mediaToken + uri.AbsolutePath["/torrserver".Length..] + uri.Query);
        return true;
    }

    private enum Backend { TorrServer, Jackett }
    private async Task ProxyAsync(HttpContext context, Backend backend, string path, bool rewriteResults = false)
    {
        var current = Volatile.Read(ref _runtime);
        var baseUri = backend == Backend.TorrServer ? current.TorrServerBaseUri : current.JackettBaseUri;
        var query = context.Request.Query.Where(x => backend != Backend.Jackett ||
            !x.Key.Equals("apikey", StringComparison.OrdinalIgnoreCase) && !x.Key.StartsWith("lampawin_", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Value.Select(value => new KeyValuePair<string, string?>(x.Key, value))).ToList();
        if (backend == Backend.Jackett)
        {
            query.Add(new("apikey", current.JackettApiKey));
            // RuTor and certain Russian indexers categorize torrents under 'Other' (8000/100003) rather than standard 2000 (Movies) or 5000 (TV).
            // When Lampa specifies Category[]=2000 or 5000, ensure 8000 and 100003 are included so RuTor results are not filtered out.
            var hasCategory = query.Any(x => x.Key.Equals("Category[]", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("Category", StringComparison.OrdinalIgnoreCase));
            if (hasCategory)
            {
                if (!query.Any(x => (x.Key.Equals("Category[]", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("Category", StringComparison.OrdinalIgnoreCase)) && x.Value == "8000"))
                    query.Add(new("Category[]", "8000"));
                if (!query.Any(x => (x.Key.Equals("Category[]", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("Category", StringComparison.OrdinalIgnoreCase)) && x.Value == "100003"))
                    query.Add(new("Category[]", "100003"));
            }
        }
        var target = new Uri(baseUri, path.TrimStart('/') + QueryString.Create(query));
        if (target.GetLeftPart(UriPartial.Authority) != baseUri.GetLeftPart(UriPartial.Authority))
        { context.Response.StatusCode = 400; return; }
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (backend == Backend.TorrServer) SetTorrAuth(request, current);
        if (context.Request.Headers.Range.Count > 0) request.Headers.TryAddWithoutValidation("Range", context.Request.Headers.Range.ToString());
        if (context.Request.Headers.IfRange.Count > 0) request.Headers.TryAddWithoutValidation("If-Range", context.Request.Headers.IfRange.ToString());
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }
        await SendProxyAsync(context, request, rewriteResults, current);
    }

    private async Task ProxyDownloadAsync(HttpContext context, Uri original)
    {
        var current = Volatile.Read(ref _runtime);
        if (original.GetLeftPart(UriPartial.Authority) != current.JackettBaseUri.GetLeftPart(UriPartial.Authority) ||
            !(original.AbsolutePath.StartsWith("/dl/", StringComparison.Ordinal) || original.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)))
        { context.Response.StatusCode = 403; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var target = original;
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var internalTarget = target.GetLeftPart(UriPartial.Authority) == current.JackettBaseUri.GetLeftPart(UriPartial.Authority);
                if (!internalTarget && (target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0 || target.Port is not (80 or 443)))
                { context.Response.StatusCode = 502; return; }
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                using var response = await (internalTarget ? _client : _publicClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    target = location.IsAbsoluteUri ? location : new Uri(target, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode) { context.Response.StatusCode = 502; return; }
                if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) { context.Response.StatusCode = 502; return; }
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var data = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (data.Length + count > 8 * 1024 * 1024) { context.Response.StatusCode = 502; return; }
                    data.Write(buffer, 0, count);
                }
                var bytes = data.ToArray();
                if (bytes.Length < 16 || bytes[0] != (byte)'d' || bytes[^1] != (byte)'e')
                { context.Response.StatusCode = 502; return; }
                context.Response.ContentType = "application/x-bittorrent";
                context.Response.ContentLength = bytes.Length;
                if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                return;
            }
            context.Response.StatusCode = 502;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 504; }
        catch (HttpRequestException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 502; }
    }

    private async Task SendProxyAsync(HttpContext context, HttpRequestMessage request, bool rewriteResults, RuntimeSnapshot current)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(rewriteResults ? TimeSpan.FromSeconds(100) : TimeSpan.FromMinutes(2));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            if (rewriteResults && response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (body.Length > 16 * 1024 * 1024) { context.Response.StatusCode = 502; return; }
                var json = JsonNode.Parse(body);
                if (json?["Results"] is JsonArray items)
                {
                    var searchQuery = context.Request.Query["Query"].FirstOrDefault()
                        ?? context.Request.Query["query"].FirstOrDefault()
                        ?? context.Request.Query["q"].FirstOrDefault();
                    var releaseYear = int.TryParse(context.Request.Query["lampawin_year"], out var year) && year is >= 1800 and <= 2100
                        ? (int?)year : null;
                    TorrentResultPolicy.FilterAndRank(items, searchQuery, releaseYear, context.Request.Query["lampawin_kind"] == "tv");
                    for (var i = items.Count - 1; i >= 0; i--)
                    {
                        var item = items[i];
                        if (item is null) continue;
                        var link = item["Link"]?.GetValue<string>();
                        if (Uri.TryCreate(link, UriKind.Absolute, out var download) &&
                            download.GetLeftPart(UriPartial.Authority) == current.JackettBaseUri.GetLeftPart(UriPartial.Authority))
                        {
                            foreach (var stale in _downloads.Where(x => x.Value.Expires < DateTimeOffset.UtcNow)) _downloads.TryRemove(stale.Key, out _);
                            if (_downloads.Count >= 5000) { item["Link"] = null; continue; }
                            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
                            _downloads[token] = (download, DateTimeOffset.UtcNow.AddHours(12));
                            item["Link"] = new Uri(Origin, "download/" + token).AbsoluteUri;
                        }
                    }
                }
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(json?.ToJsonString() ?? body, context.RequestAborted);
                return;
            }
            foreach (var header in new[] { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Last-Modified", "ETag" })
            {
                if (response.Content.Headers.TryGetValues(header, out var values) || response.Headers.TryGetValues(header, out values))
                    context.Response.Headers[header] = values.ToArray();
            }
            // Never forward backend Set-Cookie/Location or any permissive CORS headers.
            if (!HttpMethods.IsHead(context.Request.Method))
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 504; }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 502; }
    }
    private static void SetTorrAuth(HttpRequestMessage request, RuntimeSnapshot runtime) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(runtime.TorrServerUser + ":" + runtime.TorrServerPassword)));
    private static bool FixedEquals(string? left, string right) => left is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _client.Dispose();
        _publicClient.Dispose();
        _downloads.Clear();
    }
}
