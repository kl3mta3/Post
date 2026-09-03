using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Post.Core.Publishing;

/// <summary>
/// Holds connected accounts on disk, encrypted at rest with DPAPI under the current
/// Windows user. Nothing readable — access tokens, refresh tokens or a client secret —
/// is ever written in the clear, and the file cannot be decrypted by another user or
/// copied to another machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PublishAccountStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Post.Publishing.v1");

    public PublishAccountStore(string? path = null)
        => Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Post", "accounts.dat");

    public string Path { get; }

    public List<PublishAccount> Load()
    {
        try
        {
            if (!File.Exists(Path)) return [];
            var protectedBytes = File.ReadAllBytes(Path);
            if (protectedBytes.Length == 0) return [];
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var accounts = JsonSerializer.Deserialize<List<PublishAccount>>(plain, Options) ?? [];
            // A store written by an older build can name a platform this one no longer has.
            return accounts.Where(account => Enum.IsDefined(account.Platform)).ToList();
        }
        // A file from another user or a corrupted one cannot be recovered; start clean
        // rather than losing the ability to add accounts at all.
        catch (CryptographicException) { return []; }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    public void Save(IEnumerable<PublishAccount> accounts)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(accounts.ToList(), Options);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        // Write through a temporary file so a crash cannot leave a half-written store.
        var temporary = Path + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, Path, true);
        Array.Clear(plain);
    }

    public void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch (IOException) { }
    }
}
