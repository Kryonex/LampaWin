using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace LampaWin.Desktop;

internal static class DesktopUpdater
{
    private const string Repository = "Kryonex/LampaWin";
    private const string InstallerName = "LampaWin-Setup-win-x64.exe";
    private const string RegistryPath = @"Software\LampaWin";
    private const string LegacyUninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{C7A5C33F-3531-44A0-AB47-622913A015D9}_is1";
    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    private static string UpdateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LampaWin", "Updates");
    private static string UpdatePreferencePath => Path.Combine(UpdateRoot, "preferences.json");
    private static string ExpectedPublisher => typeof(DesktopUpdater).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "UpdatePublisher")?.Value ?? "";

    public static async Task<bool> CheckAndOfferAsync(CancellationToken cancellationToken = default, bool manual = false)
    {
        if (!await CheckGate.WaitAsync(0, cancellationToken)) return false;
        var installing = false;
        try
        {
        var installPath = GetRegisteredInstallPath();
        if (installPath is null || !SamePath(installPath, AppContext.BaseDirectory))
        {
            if (manual) MessageBox.Show(Application.Current.MainWindow, "Портативная копия обновляется вручную. Новая версия доступна в разделе Releases проекта.", "Обновления LampaWin");
            return false;
        }
        ShowPreviousResult();

        try
        {
            using var client = CreateClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var root = release.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagProperty) ||
                !TryReleaseVersion(tagProperty.GetString(), out var latest) ||
                latest <= CurrentVersion())
            { if (manual) MessageBox.Show(Application.Current.MainWindow, "Установлена актуальная версия.", "Обновления LampaWin"); return false; }
            var preference = LampaWin.Core.DesktopPreferences.Load(UpdatePreferencePath);
            if (!manual && preference.DeferredUpdateVersion == latest.ToString() && preference.DeferredUpdateUntil > DateTimeOffset.UtcNow) return false;

            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            var installerUrl = GetAssetUrl(assets, InstallerName);
            var checksumsUrl = GetAssetUrl(assets, "SHA256SUMS.txt");
            var signatureUrl = GetAssetUrl(assets, "SHA256SUMS.txt.p7s");
            if (ExpectedPublisher.Length > 0 && signatureUrl is null) throw new CryptographicException("В релизе отсутствует подпись списка контрольных сумм.");
            if (installerUrl is null || checksumsUrl is null) throw new InvalidDataException("В релизе отсутствует установщик или контрольные суммы.");
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            if (notes.Length > 1800) notes = notes[..1800] + "…";

            var answer = System.Windows.MessageBox.Show(
                Application.Current.MainWindow,
                $"Доступна новая версия LampaWin {latest}.\n\n{notes}\n\nУстановить сейчас? Приложение будет перезапущено. «Нет» — напомнить через сутки.",
                "Обновление LampaWin", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
            if (answer != System.Windows.MessageBoxResult.Yes)
            { preference.DeferredUpdateVersion = latest.ToString(); preference.DeferredUpdateUntil = DateTimeOffset.UtcNow.AddDays(1); preference.Save(UpdatePreferencePath); return false; }
            installing = true;

            var progress = new UpdateProgressWindow { Owner = Application.Current.MainWindow };
            progress.Show();
            try
            {
                using var updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, progress.CancellationToken);
                await InstallAsync(client, installerUrl, checksumsUrl, signatureUrl, installPath, latest, updateCancellation.Token, progress.Report);
            }
            finally
            {
                progress.CloseFromCode();
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (manual || installing) System.Windows.MessageBox.Show(
                $"Не удалось установить обновление. LampaWin запустится в текущей версии.\n\n{ex.Message}",
                "Обновление LampaWin", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }
        }
        finally { CheckGate.Release(); }
    }

    private static async Task InstallAsync(HttpClient client, string installerUrl, string checksumsUrl, string? signatureUrl, string installPath, Version version, CancellationToken cancellationToken, Action<long, long?> reportProgress)
    {
        var updateDirectory = Path.Combine(UpdateRoot, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);
        var installerPath = Path.Combine(updateDirectory, InstallerName);
        using var checksumTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        checksumTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        var checksumText = await client.GetStringAsync(checksumsUrl, checksumTimeout.Token);
        if (checksumText.Length > 65536) throw new InvalidDataException("Список контрольных сумм слишком большой.");
        await File.WriteAllTextAsync(Path.Combine(updateDirectory, "SHA256SUMS.txt"), checksumText, new System.Text.UTF8Encoding(false), checksumTimeout.Token);
        if (ExpectedPublisher.Length > 0 && signatureUrl is not null)
        {
            var signature = await client.GetByteArrayAsync(signatureUrl, checksumTimeout.Token);
            if (signature.Length > 65536) throw new CryptographicException("Подпись списка контрольных сумм слишком большая.");
            await File.WriteAllBytesAsync(Path.Combine(updateDirectory, "SHA256SUMS.txt.p7s"), signature, checksumTimeout.Token);
        }
        var expectedHash = ReadExpectedHash(checksumText, InstallerName);
        reportProgress(0, null);
        var partialPath = installerPath + ".part";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await client.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var destination = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                var total = response.Content.Headers.ContentLength;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), timeout.Token)) != 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    received += read;
                    if (received > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Установщик превышает допустимый размер.");
                    reportProgress(received, total);
                }
                await destination.FlushAsync(timeout.Token);
            }
            reportProgress(0, 0);
            await using (var file = File.OpenRead(partialPath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file, timeout.Token));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Контрольная сумма установщика не совпала. Обновление отменено.");
        }
        File.Move(partialPath, installerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }

        var scriptPath = Path.Combine(updateDirectory, "install-update.ps1");
        await File.WriteAllTextAsync(scriptPath, UpdaterScript, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        var process = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.ArgumentList.Add("-NoProfile");
        process.ArgumentList.Add("-NonInteractive");
        process.ArgumentList.Add("-ExecutionPolicy");
        process.ArgumentList.Add("Bypass");
        process.ArgumentList.Add("-File");
        process.ArgumentList.Add(scriptPath);
        process.ArgumentList.Add("-ProcessIdToWait");
        process.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process.ArgumentList.Add("-InstallerPath");
        process.ArgumentList.Add(installerPath);
        process.ArgumentList.Add("-InstallPath");
        process.ArgumentList.Add(installPath);
        process.ArgumentList.Add("-ExpectedHash"); process.ArgumentList.Add(expectedHash);
        process.ArgumentList.Add("-ExpectedVersion"); process.ArgumentList.Add(version.ToString());
        if (ExpectedPublisher.Length > 0) { process.ArgumentList.Add("-ExpectedPublisher"); process.ArgumentList.Add(ExpectedPublisher); }
        process.ArgumentList.Add("-ValidateOnly");
        using (var validation = Process.Start(process) ?? throw new InvalidOperationException("Не удалось проверить установщик."))
        {
            try { await validation.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
            catch { if (!validation.HasExited) validation.Kill(entireProcessTree: true); throw; }
            if (validation.ExitCode != 0) throw new InvalidOperationException("Обновление не прошло предварительную проверку. Проверьте свободное место для установки и резервной копии. Для подписанной сборки требуется установщик того же издателя.");
        }
        process.ArgumentList.RemoveAt(process.ArgumentList.Count - 1);
        cancellationToken.ThrowIfCancellationRequested();
        using var updater = Process.Start(process) ?? throw new InvalidOperationException("Не удалось запустить установщик обновления.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LampaWin", CurrentVersion().ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string? GetRegisteredInstallPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        var path = key?.GetValue("InstallPath") as string;
        if (!string.IsNullOrWhiteSpace(path)) return path;

        // Also recognize installs made by earlier installers before the updater marker existed.
        using var uninstall = Registry.CurrentUser.OpenSubKey(LegacyUninstallPath, writable: false);
        return uninstall?.GetValue("InstallLocation") as string ?? uninstall?.GetValue("Inno Setup: App Path") as string;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static Version CurrentVersion() => typeof(DesktopUpdater).Assembly.GetName().Version ?? new Version(0, 0);

    private static bool TryReleaseVersion(string? tag, out Version version)
    {
        var value = (tag ?? string.Empty).Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        return Version.TryParse(value, out version!);
    }

    private static string? GetAssetUrl(IEnumerable<JsonElement> assets, string name)
    {
        foreach (var asset in assets)
            if (asset.TryGetProperty("name", out var assetName) && assetName.GetString() == name &&
                asset.TryGetProperty("browser_download_url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal)) return uri.AbsoluteUri;
        return null;
    }

    private static string ReadExpectedHash(string checksums, string filename)
    {
        foreach (var line in checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(line, @"^(?<hash>[A-Fa-f0-9]{64})\s+\*?(?<name>.+?)\s*$", RegexOptions.CultureInvariant);
            if (match.Success && match.Groups["name"].Value.Equals(filename, StringComparison.Ordinal)) return match.Groups["hash"].Value;
        }
        throw new CryptographicException("В списке релиза отсутствует SHA-256 установщика.");
    }

    private static string UpdaterScript
    {
        get
        {
            using var stream = typeof(DesktopUpdater).Assembly.GetManifestResourceStream("LampaWin.Desktop.UpdateInstaller.ps1")
                ?? throw new InvalidOperationException("Отсутствует помощник обновления.");
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
    }

    private static void ShowPreviousResult()
    {
        var file = Path.Combine(UpdateRoot, "result.json");
        if (!File.Exists(file)) return;
        try
        {
            using var result = JsonDocument.Parse(File.ReadAllBytes(file));
            var status = result.RootElement.GetProperty("status").GetString();
            if (status is not ("failed" or "pending")) return;
            var code = result.RootElement.GetProperty("code").GetString();
            var message = code == "restored-previous-version" ? "Обновление не установилось. Предыдущая версия восстановлена; данные пользователя сохранены."
                : "Предыдущее обновление не завершилось. Резервная копия сохранена в папке Updates. Повторите установку или используйте установщик из Releases.";
            MessageBox.Show(Application.Current.MainWindow, message, "Результат обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
            File.Move(file, Path.Combine(UpdateRoot, "last-result.json"), true);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public static async Task ConfirmSuccessfulStartupAsync()
    {
        var file = Path.Combine(UpdateRoot, "result.json");
        try
        {
            if (!File.Exists(file)) return;
            using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
            var root = result.RootElement;
            if (root.GetProperty("status").GetString() != "installed" || !TryReleaseVersion(root.GetProperty("version").GetString(), out var version) || version != CurrentVersion()) return;
            var name = root.GetProperty("run").GetString();
            if (name is null || !Regex.IsMatch(name, "^run-[a-f0-9]{32}$")) return;
            var directory = Path.GetFullPath(Path.Combine(UpdateRoot, name));
            if (!directory.StartsWith(Path.GetFullPath(UpdateRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            await Task.Run(() =>
            {
                if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                if (Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) return;
                Directory.Delete(directory, true);
            });
            await File.WriteAllTextAsync(Path.Combine(UpdateRoot, "last-result.json"), JsonSerializer.Serialize(new { status = "success", version = version.ToString(), utc = DateTimeOffset.UtcNow }));
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) { }
    }

    private sealed class UpdateProgressWindow : Window
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly TextBlock _status;
        private readonly ProgressBar _progress;
        private readonly Button _cancelButton;
        private bool _allowClose;

        public CancellationToken CancellationToken => _cancel.Token;

        public UpdateProgressWindow()
        {
            Title = "Обновление LampaWin";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _status = new TextBlock { Text = "Подготовка обновления…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16, IsIndeterminate = true };
            _cancelButton = new Button { Content = "Отмена", Width = 88, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            _cancelButton.Click += (_, _) => Cancel();

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(_status);
            panel.Children.Add(_progress);
            panel.Children.Add(_cancelButton);
            Content = panel;
            Closing += (_, e) =>
            {
                if (_allowClose) return;
                e.Cancel = true;
                Cancel();
            };
        }

        public void Report(long received, long? total)
        {
            if (total is > 0)
            {
                _progress.IsIndeterminate = false;
                _progress.Value = Math.Clamp(received * 100d / total.Value, 0, 100);
                _status.Text = $"Загрузка обновления: {received / 1048576d:F1} из {total.Value / 1048576d:F1} МБ";
            }
            else if (total == 0)
            {
                _progress.IsIndeterminate = true;
                _status.Text = "Проверка целостности загруженного файла…";
            }
            else
            {
                _progress.IsIndeterminate = true;
                _status.Text = received == 0 ? "Подготовка загрузки…" : $"Загрузка обновления: {received / 1048576d:F1} МБ";
            }
        }

        public void CloseFromCode()
        {
            _allowClose = true;
            Close();
            _cancel.Dispose();
        }

        private void Cancel()
        {
            if (_cancel.IsCancellationRequested) return;
            _cancelButton.IsEnabled = false;
            _status.Text = "Отмена обновления…";
            _cancel.Cancel();
        }
    }
}
