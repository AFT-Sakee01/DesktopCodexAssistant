using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

internal static class SecretStore
{

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        byte[] plain = Encoding.UTF8.GetBytes(secret);
        byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
        {
            return string.Empty;
        }

        byte[] protectedBytes = Convert.FromBase64String(protectedBase64.Trim());
        byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public static bool TryReadOrMigrateSecret(
        string encryptedPath,
        string legacyTextPath,
        Func<string, string> normalize,
        out string secret,
        out bool migrated,
        out string errorCode)
    {
        secret = string.Empty;
        migrated = false;
        errorCode = string.Empty;

        try
        {
            if (!string.IsNullOrWhiteSpace(encryptedPath) && File.Exists(encryptedPath))
            {
                secret = ApplyNormalize(normalize, Unprotect(File.ReadAllText(encryptedPath, Encoding.UTF8)));
                return true;
            }

            if (string.IsNullOrWhiteSpace(legacyTextPath) || !File.Exists(legacyTextPath))
            {
                return true;
            }

            string legacySecret = ApplyNormalize(normalize, File.ReadAllText(legacyTextPath, Encoding.UTF8));
            if (legacySecret.Length > 0)
            {
                WriteSecret(encryptedPath, legacySecret);
            }

            MoveLegacySecretToMigratedFile(legacyTextPath);
            secret = legacySecret;
            migrated = true;
            return true;
        }
        catch (CryptographicException)
        {
            errorCode = "SECRET_DPAPI";
            return false;
        }
        catch (FormatException)
        {
            errorCode = "SECRET_FORMAT";
            return false;
        }
        catch (IOException)
        {
            errorCode = "SECRET_IO";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            errorCode = "SECRET_ACCESS";
            return false;
        }
    }

    public static void WriteSecret(string encryptedPath, string secret)
    {
        if (string.IsNullOrWhiteSpace(encryptedPath))
        {
            throw new ArgumentException("Encrypted secret path is required.", "encryptedPath");
        }

        string directory = Path.GetDirectoryName(encryptedPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = encryptedPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, Protect(secret ?? string.Empty) + Environment.NewLine, SharedEncoding.Utf8NoBom);
            if (File.Exists(encryptedPath))
            {
                File.Replace(tempPath, encryptedPath, null);
            }
            else
            {
                File.Move(tempPath, encryptedPath);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public static void DeleteSecretFiles(string encryptedPath, string legacyTextPath)
    {
        TryDeleteFile(encryptedPath);
        TryDeleteFile(legacyTextPath);
        DeleteMigratedLegacyFiles(legacyTextPath);
    }

    public static void DeleteLegacySecretFiles(string legacyTextPath)
    {
        TryDeleteFile(legacyTextPath);
        DeleteMigratedLegacyFiles(legacyTextPath);
    }

    public static string TrimSecret(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    public static void RunSelfTest()
    {
        string original = "secret-" + Guid.NewGuid().ToString("N");
        string protectedText = Protect(original);
        if (string.IsNullOrWhiteSpace(protectedText) ||
            protectedText.IndexOf(original, StringComparison.Ordinal) >= 0 ||
            !string.Equals(Unprotect(protectedText), original, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SecretStore DPAPI round-trip self-test failed.");
        }

        string root = Path.Combine(Path.GetTempPath(), "desktopcodex-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encrypted = Path.Combine(root, "secret.bin");
            string legacy = Path.Combine(root, "secret.txt");
            File.WriteAllText(legacy, " legacy-value " + Environment.NewLine, SharedEncoding.Utf8NoBom);

            string secret;
            bool migrated;
            string errorCode;
            if (!TryReadOrMigrateSecret(encrypted, legacy, TrimSecret, out secret, out migrated, out errorCode) ||
                !migrated ||
                !string.Equals(secret, "legacy-value", StringComparison.Ordinal) ||
                !File.Exists(encrypted) ||
                File.Exists(legacy) ||
                !File.Exists(legacy + ".migrated"))
            {
                throw new InvalidOperationException("SecretStore legacy migration self-test failed: " + errorCode);
            }

            string stored = File.ReadAllText(encrypted, Encoding.UTF8);
            if (stored.IndexOf("legacy-value", StringComparison.Ordinal) >= 0 ||
                !string.Equals(Unprotect(stored), "legacy-value", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SecretStore encrypted file self-test failed.");
            }

            DeleteSecretFiles(encrypted, legacy);
            if (File.Exists(encrypted) || File.Exists(legacy) || File.Exists(legacy + ".migrated"))
            {
                throw new InvalidOperationException("SecretStore delete self-test failed.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    private static string ApplyNormalize(Func<string, string> normalize, string value)
    {
        return normalize == null ? TrimSecret(value) : normalize(value);
    }

    private static void MoveLegacySecretToMigratedFile(string legacyTextPath)
    {
        if (string.IsNullOrWhiteSpace(legacyTextPath) || !File.Exists(legacyTextPath))
        {
            return;
        }

        string target = legacyTextPath + ".migrated";
        if (File.Exists(target))
        {
            string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            target = legacyTextPath + ".migrated." + timestamp;
        }

        File.Move(legacyTextPath, target);
    }

    private static void DeleteMigratedLegacyFiles(string legacyTextPath)
    {
        if (string.IsNullOrWhiteSpace(legacyTextPath))
        {
            return;
        }

        string directory = Path.GetDirectoryName(legacyTextPath);
        string fileName = Path.GetFileName(legacyTextPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
        {
            return;
        }

        string pattern = fileName + ".migrated*";
        string[] files = Directory.GetFiles(directory, pattern);
        for (int i = 0; i < files.Length; i++)
        {
            TryDeleteFile(files[i]);
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
