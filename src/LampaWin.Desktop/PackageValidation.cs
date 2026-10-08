using System.IO;
using System.Text.Json;
using System.Windows;
using LampaWin.Core;
using Microsoft.Web.WebView2.Core;

namespace LampaWin.Desktop;

internal static class PackageValidation
{
    public static async Task<int> RunAsync(string fixture, string report)
    {
        var checks = new Dictionary<string, object>();
        MainWindow? window = null;
        var scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "package-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(AppContext.BaseDirectory, scratch);
            foreach (var file in new[] { paths.TorrServerExecutable, paths.JackettExecutable, Path.Combine(paths.LampaRoot, "index.html"), Path.Combine(paths.InstallRoot, "source", "LampaWin-source.zip") })
                if (!File.Exists(file)) throw new FileNotFoundException("Package component missing.");
            checks["componentsPresent"] = true;
            checks["bridgePresent"] = BridgeProtocol.Script.Contains("nextEpisode", StringComparison.Ordinal);
            Directory.CreateDirectory(scratch);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(scratch, "webview"));
            window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
            Application.Current.MainWindow = window;
            window.Show();
            await window.Browser.EnsureCoreWebView2Async(environment);
            checks["webViewLoaded"] = window.Browser.CoreWebView2 is not null;
            var played = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.PlaybackChanged += progress =>
            {
                if (progress.State == PlaybackState.Error) played.TrySetException(new InvalidOperationException("Native playback failed."));
                if (progress.State == PlaybackState.Playing && progress.PositionSeconds >= .5) played.TrySetResult(true);
            };
            window.Play(new MediaRequest(new Uri(Path.GetFullPath(fixture)), "Package fixture", 0, Guid.NewGuid().ToString()));
            checks["nativePlayback"] = await played.Task.WaitAsync(TimeSpan.FromSeconds(20));
            checks["success"] = checks.Values.All(value => value is true);
        }
        catch (Exception ex) { checks["success"] = false; checks["failureType"] = ex.GetType().Name; }
        finally
        {
            window?.Close();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            // WebView can hold its profile briefly after disposal; keep this isolated scratch
            // for diagnostics instead of killing shared browser processes.
        }
        return checks["success"] is true ? 0 : 1;
    }
}
