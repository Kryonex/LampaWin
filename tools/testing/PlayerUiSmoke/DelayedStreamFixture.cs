using System.IO;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>A real HTTP Range source whose later bytes are held until explicitly released.</summary>
internal sealed class DelayedStreamFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _blockedRequests;
    public int BlockedRequests => Volatile.Read(ref _blockedRequests);
    public Uri Url { get; private set; } = null!;
    private DelayedStreamFixture(WebApplication app) => _app = app;
    public void Release() => _gate.TrySetResult();

    public static async Task<DelayedStreamFixture> StartAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var fixture = new DelayedStreamFixture(app);
        app.MapGet("/video.mp4", async context =>
        {
            long start = 0, end = bytes.Length - 1;
            var range = context.Request.Headers.Range.ToString();
            if (range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var limits = range[6..].Split('-');
                if (long.TryParse(limits[0], out var offset)) start = offset;
                if (limits.Length > 1 && long.TryParse(limits[1], out var last)) end = Math.Min(end, last);
                context.Response.StatusCode = 206;
                context.Response.Headers.ContentRange = $"bytes {start}-{end}/{bytes.Length}";
            }
            if (start < 0 || start > end || start >= bytes.Length) { context.Response.StatusCode = 416; return; }
            context.Response.Headers.AcceptRanges = "bytes";
            context.Response.ContentType = "video/mp4";
            context.Response.ContentLength = end - start + 1;
            await context.Response.StartAsync(context.RequestAborted);
            var threshold = bytes.Length / 6;
            try
            {
                for (var position = start; position <= end;)
                {
                    if (position >= threshold && !fixture._gate.Task.IsCompleted)
                    {
                        Interlocked.Increment(ref fixture._blockedRequests);
                        await fixture._gate.Task.WaitAsync(context.RequestAborted);
                    }
                    var count = (int)Math.Min(16384, end - position + 1);
                    await context.Response.Body.WriteAsync(bytes.AsMemory((int)position, count), context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    position += count;
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        fixture.Url = new Uri(address.TrimEnd('/') + "/video.mp4");
        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
