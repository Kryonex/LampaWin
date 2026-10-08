using System.Text.Json;

namespace LampaWin.Core;

public sealed class DesktopPreferences
{
    public double Volume { get; set; } = 80;
    public double VolumeBeforeMute { get; set; } = 80;
    public string AudioLanguage { get; set; } = "";
    public string SubtitleLanguage { get; set; } = "off";
    public string OutputDevice { get; set; } = "";
    public bool PreviewEnabled { get; set; } = true;
    public bool AutoNextEpisode { get; set; }
    public bool ShowAllSearchResults { get; set; }
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool Maximized { get; set; }
    public string? DeferredUpdateVersion { get; set; }
    public DateTimeOffset? DeferredUpdateUntil { get; set; }

    public static DesktopPreferences Load(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 65536)
                return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllBytes(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this));
        File.Move(temp, path, true);
    }
}
