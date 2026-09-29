using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glossa.App.Ai;

/// <summary>
/// API keys for user-supplied endpoints, encrypted with Windows DPAPI (current user): the file is useless on
/// another account or machine. GLOSSA_API_KEY_&lt;NAME&gt; environment variables still work as an override.
/// </summary>
public sealed class KeyStore(string path)
{
    private readonly object _gate = new();

    public string? Get(string name)
    {
        var env = Environment.GetEnvironmentVariable("GLOSSA_API_KEY_" + name.ToUpperInvariant());
        if (!string.IsNullOrEmpty(env)) return env;
        lock (_gate)
        {
            var all = Load();
            if (!all.TryGetValue(name, out var blob)) return null;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                return null; // encrypted by another Windows user
            }
        }
    }

    public bool Has(string name) => Get(name) is { Length: > 0 };

    public void Set(string name, string? key)
    {
        lock (_gate)
        {
            var all = Load();
            if (string.IsNullOrWhiteSpace(key)) all.Remove(name);
            else all[name] = Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Rename(string oldName, string newName)
    {
        if (oldName == newName) return;
        var key = Get(oldName);
        Set(oldName, null);
        if (key is not null) Set(newName, key);
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
