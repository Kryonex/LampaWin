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
    public ProfileStore(AppPaths paths) => _path = Path.Combine(paths.DataRoot, "lampa-profile.bin");

    public Dictionary<string, string> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            if (new FileInfo(_path).Length > 17 * 1024 * 1024)
                throw new InvalidDataException("Profile exceeds allowed size.");
            var data = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(data) ?? new(); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Preserve damaged state for recovery instead of overwriting it silently.
            File.Copy(_path, _path + ".recovery", true);
            return new();
        }
    }

    public async Task SaveAsync(Dictionary<string, string> state, CancellationToken cancellationToken = default)
    {
        if (state.Count > 10000 || state.Any(x => x.Value is null || x.Key.Length > 8192 || x.Value.Length > 4 * 1024 * 1024))
            throw new InvalidDataException("Profile exceeds allowed limits.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(state);
        if (plain.Length > 16 * 1024 * 1024) throw new InvalidDataException("Profile exceeds allowed size.");
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllBytesAsync(temp, encrypted, cancellationToken);
            File.Move(temp, _path, true);
        }
        finally { _lock.Release(); }
    }
}
