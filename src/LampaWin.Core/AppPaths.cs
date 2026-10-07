namespace LampaWin.Core;

public sealed class AppPaths
{
    public AppPaths(string installRoot, string? dataRoot = null)
    {
        InstallRoot = Path.GetFullPath(installRoot);
        DataRoot = Path.GetFullPath(dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LampaWin"));
    }
    public string InstallRoot { get; }
    public string DataRoot { get; }
    public string ComponentsRoot => Path.Combine(InstallRoot, "components");
    public string LampaRoot => Path.Combine(ComponentsRoot, "lampa");
    public string TorrServerExecutable => Path.Combine(ComponentsRoot, "torrserver", "TorrServer.exe");
    public string JackettExecutable => Path.Combine(ComponentsRoot, "jackett", "JackettConsole.exe");
    public string TorrServerData => Path.Combine(DataRoot, "torrserver");
    public string JackettData => Path.Combine(DataRoot, "jackett");
    public string BrowserData => Path.Combine(DataRoot, "webview");
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public void EnsureDirectories()
    {
        foreach (var path in new[] { DataRoot, TorrServerData, JackettData, BrowserData, LogsDirectory }) Directory.CreateDirectory(path);
    }
}
