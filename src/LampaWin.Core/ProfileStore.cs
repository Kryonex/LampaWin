using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LampaWin.Core;

[SupportedOSPlatform("windows")]
public sealed class ProfileStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LampaWin.Profile.v1");
    private bool _primaryHealthy;
    public string? RecoveryMessage { get; private set; }
    public ProfileStore(AppPaths paths) => _path = Path.Combine(paths.DataRoot, "lampa-profile.bin");

    public Dictionary<string, string> Load()
    {
        RecoveryMessage = null;
        _primaryHealthy = false;
        if (TryLoad(_path, out var state)) { _primaryHealthy = true; return state; }
        if (File.Exists(_path))
        {
            var recovery = _path + ".recovery";
            try
            {
                if (File.Exists(recovery)) recovery += "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff");
                File.Copy(_path, recovery, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            RecoveryMessage = "Основной профиль повреждён. Копия сохранена для восстановления.";
        }
        if (TryLoad(_path + ".backup", out state))
        {
            RecoveryMessage = "Профиль восстановлен из последней резервной копии.";
            return state;
        }
        if (RecoveryMessage is not null) RecoveryMessage += " Резервная копия недоступна; открыт новый профиль.";
        return new();
    }

    private static bool TryLoad(string path, out Dictionary<string, string> state)
    {
        state = new();
        if (!File.Exists(path)) return false;
        try
        {
            if (new FileInfo(path).Length > 17 * 1024 * 1024)
                throw new InvalidDataException("Profile exceeds allowed size.");
            var data = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try { state = JsonSerializer.Deserialize<Dictionary<string, string>>(data) ?? new(); return true; }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException) { return false; }
    }

    public async Task SaveAsync(Dictionary<string, string> state, CancellationToken cancellationToken = default)
    {
        if (state.Count > 10000 || state.Any(x => x.Value is null || x.Key.Length > 8192 || x.Value.Length > 4 * 1024 * 1024))
            throw new InvalidDataException("Profile exceeds allowed limits.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(state);
        if (plain.Length > 16 * 1024 * 1024) throw new InvalidDataException("Profile exceeds allowed size.");
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllBytesAsync(temp, encrypted, cancellationToken);
            if (_primaryHealthy && File.Exists(_path)) File.Replace(temp, _path, _path + ".backup", true);
            else File.Move(temp, _path, true);
            _primaryHealthy = true;
        }
        finally { _lock.Release(); }
    }
}
