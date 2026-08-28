using System.Security.Cryptography;

namespace WebMusic.Backend.Services;

/// <summary>PBKDF2 password hashing with backward-compatible verification for legacy plaintext accounts.</summary>
public static class PasswordService
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 210_000;
    private const string Prefix = "pbkdf2-sha256";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string storedValue, string password, out bool needsUpgrade)
    {
        needsUpgrade = false;
        var parts = storedValue.Split('$');
        if (parts.Length == 4 && parts[0] == Prefix && int.TryParse(parts[1], out var iterations))
        {
            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // Existing installations stored passwords in plaintext. Upgrade only after a successful login.
        needsUpgrade = true;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(storedValue),
            System.Text.Encoding.UTF8.GetBytes(password));
    }
}
