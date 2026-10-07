using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using LampaWin.Desktop;
using NAudio.CoreAudioApi;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => new AudioSmoke(args[0]).Run();
}

// Measures only this process's WASAPI session meter. No recording or system changes.
internal sealed class AudioSmoke(string reportPath) : Application
{
    private readonly List<string> _checks = [];
    private readonly List<object> _observations = [];
    private void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); _checks.Add(name); }
    private static async Task WaitFor(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        { if (DateTime.UtcNow > limit) throw new TimeoutException("Audio condition did not become ready."); await Task.Delay(50); }
    }

    private float Peak(MMDevice device)
    {
        device.AudioSessionManager.RefreshSessions();
        var sessions = device.AudioSessionManager.Sessions;
        float peak = 0;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.GetProcessID == Environment.ProcessId)
                peak = Math.Max(peak, session.AudioMeterInformation.MasterPeakValue);
        }
        return peak;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var report = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var fixture = Path.ChangeExtension(report, ".wav");
        WriteTone(fixture);
        PlayerController? player = null;
        bool success = false;
        string? failure = null;
        bool alternateDeviceTested = false;
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var original = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            player = new PlayerController(Dispatcher);
            var monitor = typeof(PlayerController).GetField("_audioDevices", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(player);
            Check(monitor is not null, "real Windows endpoint notification subscription initialized");
            var sink = monitor!.GetType().GetField("_sink", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(monitor)!;
            void Notify(int flow, int role) => sink.GetType().GetMethod("OnDefaultDeviceChanged")!.Invoke(sink, [flow, role, original.ID]);
            player.SetVolume(15);
            player.Play(new MediaRequest(new Uri(fixture), "Audio recovery tone", 0, "audio-recovery"));
            await WaitFor(() => player.MediaPlayer.IsPlaying && player.MediaPlayer.Time > 700 && Peak(original) > .0001f);
            Check(true, "LibVLC produces a measurable signal in its own default-device WASAPI session");
            var time = player.MediaPlayer.Time;
            var track = player.MediaPlayer.AudioTrack;
            var nativeMedia = player.MediaPlayer.Media!.NativeReference;
            var alternate = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault(d => d.ID != original.ID);
            if (alternate is not null)
            {
                using (alternate)
                {
                    alternateDeviceTested = true;
                    player.MediaPlayer.SetOutputDevice(alternate.ID, null);
                    await WaitFor(() => Peak(alternate) > .0001f);
                    _observations.Add(new { step = "alternate-output", peak = Peak(alternate) });
                    // Synthetic OS callback, through the registered monitor and debounce.
                    // Routes only this player; Windows defaults stay unchanged.
                    Notify(0, 0);
                    await Task.Delay(1000);
                    _observations.Add(new { step = "default-requested", returnedToDefault = player.MediaPlayer.OutputDevice == original.ID,
                        defaultPeak = Peak(original), alternatePeak = Peak(alternate) });
                    await WaitFor(() => Peak(original) > .0001f);
                    Check(true, "alternate endpoint -> default recovery produces a real WASAPI signal");
                }
            }
            else
            {
                Notify(0, 0);
                await Task.Delay(900);
                await WaitFor(() => Peak(original) > .0001f);
            }
            Check(player.MediaPlayer.IsPlaying && player.MediaPlayer.Time > time && player.MediaPlayer.AudioTrack == track
                && player.MediaPlayer.Media!.NativeReference == nativeMedia, "automatic audio recovery keeps media, audio track and progressing timeline");
            _observations.Add(new { step = "recovered-default", peak = Peak(original), volume = player.MediaPlayer.Volume });
            Check(player.MediaPlayer.Volume == 15, "recovery preserves requested volume");
            player.TogglePause();
            await WaitFor(() => player.MediaPlayer.State == LibVLCSharp.Shared.VLCState.Paused);
            time = player.MediaPlayer.Time;
            player.RestoreAudioOutput();
            await Task.Delay(800);
            Check(player.MediaPlayer.State == LibVLCSharp.Shared.VLCState.Paused && Math.Abs(player.MediaPlayer.Time - time) < 150,
                "manual recovery preserves pause and position");
            player.TogglePause();
            await WaitFor(() => Peak(original) > .0001f);
            Check(true, "audio returns after resuming a paused recovery");
            player.SetVolume(0);
            Notify(0, 0);
            await Task.Delay(1200);
            Check(player.MediaPlayer.Volume == 0 && Peak(original) < .0001f, "device recovery preserves UI mute");
            player.SetVolume(15);
            player.RestoreAudioOutput();
            await WaitFor(() => Peak(original) > .0001f);
            Check(true, "unmute after recovery restores a measurable output signal");
            // Irrelevant capture/communications changes must not rebind this player.
            player.MediaPlayer.SetOutputDevice(original.ID, null);
            Notify(1, 0); Notify(0, 2);
            await Task.Delay(1000);
            Check(player.MediaPlayer.OutputDevice == original.ID, "capture and communications-only changes leave playback output alone");
            Notify(0, 0);
            player.Stop();
            await Task.Delay(900);
            Check(player.MediaPlayer.State == LibVLCSharp.Shared.VLCState.Stopped, "queued recovery cannot restart a stopped movie");
            player.Dispose();
            Notify(0, 0);
            await Task.Delay(100);
            Check(true, "late notification after disposal is harmless");
            success = true;
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
        finally { player?.Dispose(); }
        File.WriteAllText(report, JsonSerializer.Serialize(new { success, failure, alternateDeviceTested,
            osDefaultActuallySwitched = false, checks = _checks, observations = _observations }, new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(success ? 0 : 1);
    }

    private static void WriteTone(string path)
    {
        const int rate = 48000, seconds = 45;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + rate * seconds * 4);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4);
        writer.Write((short)4); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(rate * seconds * 4);
        for (var i = 0; i < rate * seconds; i++)
        {
            var sample = (short)(Math.Sin(i * 2 * Math.PI * 660 / rate) * 1600);
            writer.Write(sample); writer.Write(sample);
        }
    }
}
