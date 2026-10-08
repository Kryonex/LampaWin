using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LampaWin.Core;
using LampaWin.Core.Runtime;

internal static class ComponentRecoveryCheck
{
    public static async Task RunAsync(string root, string report)
    {
        var data = Path.Combine(root, ".cache", "component-recovery-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, data);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(Path.Combine(paths.JackettData, "lampawin-indexers-bootstrap-v2.json"), "{\"version\":2}");
        await using var runtime = new LocalRuntime(paths);
        var checks = new List<string>();
        var started = Stopwatch.StartNew();
        var results = new Dictionary<string, object>();
        Process Child(string name) => (Process)typeof(LocalRuntime).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            await runtime.StartAsync(deadline.Token);
            var torrId = Child("_torr").Id;
            var jackettId = Child("_jackett").Id;
            Child("_jackett").Kill(entireProcessTree: true);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.StatusChanged += status => { if (status.State == RuntimeState.Ready) ready.TrySetResult(); };
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(110), deadline.Token);
            if (Child("_torr").Id != torrId || Child("_torr").HasExited) throw new InvalidOperationException("Healthy TorrServer was restarted.");
            checks.Add("Jackett failure leaves the owned TorrServer PID running");
            if (Child("_jackett").Id == jackettId || Child("_jackett").HasExited) throw new InvalidOperationException("Jackett was not restored.");
            checks.Add("Only the failed Jackett process is replaced after repeated health failures");
            var restoredJackettId = Child("_jackett").Id;
            await runtime.RecoverAsync(deadline.Token);
            if (Child("_torr").Id != torrId || Child("_jackett").Id != restoredJackettId) throw new InvalidOperationException("Healthy manual recovery restarted a component.");
            checks.Add("Manual recovery preserves both healthy component PIDs");
            results["success"] = true;
        }
        catch (Exception ex) { results["success"] = false; results["failureType"] = ex.GetType().Name; results["failure"] = ex.Message; throw; }
        finally
        {
            results["checks"] = checks; results["seconds"] = started.Elapsed.TotalSeconds;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
