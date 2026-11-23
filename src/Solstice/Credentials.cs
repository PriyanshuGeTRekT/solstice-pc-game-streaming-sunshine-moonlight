using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Solstice;

/// <summary>Sunshine web login, encrypted with Windows DPAPI for the current user.</summary>
static class CredentialStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Solstice.SunshineLogin.v1");
    static string FilePath => Path.Combine(Paths.Data, "sunshine-login.bin");

    public static void Save(string user, string pass)
    {
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { user, pass }));
        File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
    }

    public static (string User, string Pass)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var parts = JsonSerializer.Deserialize<string[]>(plain);
            return parts is { Length: 2 } ? (parts[0], parts[1]) : null;
        }
        catch
        {
            return null;
        }
    }

    public static string NewPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 28);
    }
}
