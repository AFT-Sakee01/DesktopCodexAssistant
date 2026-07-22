using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

internal static class SecretStore
{
    private const string DpapiV1Prefix = "dpapi-v1:";
    private const long MaxSecretFileBytes = 64L * 1024L;

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        byte[] plain = Encoding.UTF8.GetBytes(secret);
        byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        return DpapiV1Prefix + Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return string.Empty;
        }

        string value = protectedText.Trim();
        if (value.StartsWith(DpapiV1Prefix, StringComparison.Ordinal))
        {
            value = value.Substring(DpapiV1Prefix.Length);
        }

        byte[] protectedBytes = Convert.FromBase64String(value);
        byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public static bool TryReadOrMigrateSecret(
        string encryptedPath,
        string legacyTextPath,
        Func<string, string> normalize,
        Func<string, bool> legacyPlaintextValidator,
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
                string stored = ReadSecretText(encryptedPath);
                bool versioned = stored.Trim().StartsWith(DpapiV1Prefix, StringComparison.Ordinal);
                bool legacyPlaintext = false;
                try
                {
                    secret = ApplyNormalize(normalize, Unprotect(stored));
                }
                catch (CryptographicException)
                {
                    if (versioned)
                    {
                        throw;
                    }

                    legacyPlaintext = true;
                }
                catch (FormatException)
                {
                    if (versioned)
                    {
                        throw;
                    }

                    legacyPlaintext = true;
                }

                if (legacyPlaintext)
                {
                    secret = ApplyNormalize(normalize, stored);
                    if (!IsAllowedLegacyPlaintext(secret, legacyPlaintextValidator))
                    {
                        errorCode = "SECRET_LEGACY_FORMAT";
                        secret = string.Empty;
                        return false;
                    }
                }
                else if (secret.Length == 0)
                {
                    throw new FormatException("Protected secret is empty.");
                }

                // Rewrite both supported legacy forms (raw DPAPI and strictly validated plaintext)
                // into the versioned envelope. Failure leaves the original target untouched.
                if (!versioned)
                {
                    WriteSecret(encryptedPath, secret);
                    migrated = true;
                }

                DeleteLegacySecretFiles(legacyTextPath);
                return true;
            }

            if (string.IsNullOrWhiteSpace(legacyTextPath) || !File.Exists(legacyTextPath))
            {
                return true;
            }

            string legacySecret = ApplyNormalize(normalize, ReadSecretText(legacyTextPath));
            if (!IsAllowedLegacyPlaintext(legacySecret, legacyPlaintextValidator))
            {
                errorCode = "SECRET_LEGACY_FORMAT";
                return false;
            }

            WriteSecret(encryptedPath, legacySecret);
            // Delete only after the atomic encrypted write commits. Cleanup failure is best-effort
            // and leaves the source available for a later retry without invalidating the new file.
            TryDeleteFile(legacyTextPath);
            secret = legacySecret;
            migrated = true;
            return true;
        }
        catch (CryptographicException)
        {
            errorCode = "SECRET_DPAPI";
            secret = string.Empty;
            return false;
        }
        catch (FormatException)
        {
            errorCode = "SECRET_FORMAT";
            secret = string.Empty;
            return false;
        }
        catch (IOException)
        {
            errorCode = "SECRET_IO";
            secret = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            errorCode = "SECRET_ACCESS";
            secret = string.Empty;
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

        string tempPath = encryptedPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, SharedEncoding.Utf8NoBom))
            {
                writer.WriteLine(Protect(secret ?? string.Empty));
                writer.Flush();
                stream.Flush(true);
            }

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
        Func<string, bool> validator = delegate(string value)
        {
            return value != null && value.StartsWith("oauth-", StringComparison.Ordinal) && value.Length >= 8;
        };
        string original = "oauth-" + Guid.NewGuid().ToString("N");
        string protectedText = Protect(original);
        if (!protectedText.StartsWith(DpapiV1Prefix, StringComparison.Ordinal) ||
            protectedText.IndexOf(original, StringComparison.Ordinal) >= 0 ||
            !string.Equals(Unprotect(protectedText), original, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SecretStore DPAPI v1 round-trip self-test failed.");
        }

        string root = Path.Combine(Path.GetTempPath(), "desktopcodex-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encrypted = Path.Combine(root, "secret.bin");
            string legacy = Path.Combine(root, "secret.txt");
            string secret;
            bool migrated;
            string errorCode;

            File.WriteAllText(legacy, " oauth-legacy-value " + Environment.NewLine, SharedEncoding.Utf8NoBom);
            if (!TryReadOrMigrateSecret(encrypted, legacy, TrimSecret, validator, out secret, out migrated, out errorCode) ||
                !migrated ||
                !string.Equals(secret, "oauth-legacy-value", StringComparison.Ordinal) ||
                !File.ReadAllText(encrypted, Encoding.UTF8).Trim().StartsWith(DpapiV1Prefix, StringComparison.Ordinal) ||
                File.Exists(legacy))
            {
                throw new InvalidOperationException("SecretStore legacy text migration self-test failed: " + errorCode);
            }

            string rawLegacyDpapi = Protect("oauth-old-dpapi").Substring(DpapiV1Prefix.Length);
            File.WriteAllText(encrypted, rawLegacyDpapi, SharedEncoding.Utf8NoBom);
            if (!TryReadOrMigrateSecret(encrypted, legacy, TrimSecret, validator, out secret, out migrated, out errorCode) ||
                !migrated ||
                !string.Equals(secret, "oauth-old-dpapi", StringComparison.Ordinal) ||
                !File.ReadAllText(encrypted, Encoding.UTF8).Trim().StartsWith(DpapiV1Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SecretStore unversioned DPAPI migration self-test failed: " + errorCode);
            }

            File.WriteAllText(encrypted, "oauth-plaintext-bin", SharedEncoding.Utf8NoBom);
            if (!TryReadOrMigrateSecret(encrypted, legacy, TrimSecret, validator, out secret, out migrated, out errorCode) ||
                !migrated ||
                !string.Equals(secret, "oauth-plaintext-bin", StringComparison.Ordinal) ||
                !string.Equals(Unprotect(File.ReadAllText(encrypted, Encoding.UTF8)), secret, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SecretStore validated plaintext .bin migration self-test failed: " + errorCode);
            }

            string randomBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("not-a-dpapi-payload"));
            File.WriteAllText(encrypted, randomBase64, SharedEncoding.Utf8NoBom);
            byte[] randomHashBefore = ComputeFileHash(encrypted);
            if (TryReadOrMigrateSecret(encrypted, legacy, TrimSecret, validator, out secret, out migrated, out errorCode) ||
                migrated ||
                !string.Equals(errorCode, "SECRET_LEGACY_FORMAT", StringComparison.Ordinal) ||
                !HashesEqual(randomHashBefore, ComputeFileHash(encrypted)))
            {
                throw new InvalidOperationException("SecretStore random Base64 fail-closed self-test failed: " + errorCode);
            }

            File.WriteAllText(encrypted, DpapiV1Prefix + randomBase64, SharedEncoding.Utf8NoBom);
            byte[] damagedHashBefore = ComputeFileHash(encrypted);
            if (TryReadOrMigrateSecret(encrypted, legacy, TrimSecret, validator, out secret, out migrated, out errorCode) ||
                migrated ||
                !string.Equals(errorCode, "SECRET_DPAPI", StringComparison.Ordinal) ||
                !HashesEqual(damagedHashBefore, ComputeFileHash(encrypted)))
            {
                throw new InvalidOperationException("SecretStore damaged envelope preservation self-test failed: " + errorCode);
            }

            File.Delete(encrypted);
            File.WriteAllText(legacy, "arbitrary unknown plaintext", SharedEncoding.Utf8NoBom);
            if (TryReadOrMigrateSecret(encrypted, legacy, TrimSecret, validator, out secret, out migrated, out errorCode) ||
                !File.Exists(legacy) ||
                File.Exists(encrypted) ||
                !string.Equals(errorCode, "SECRET_LEGACY_FORMAT", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SecretStore invalid legacy preservation self-test failed: " + errorCode);
            }

            File.Delete(legacy);
            WriteSecret(encrypted, "oauth-existing");
            File.WriteAllText(legacy, "oauth-cleanup-retry", SharedEncoding.Utf8NoBom);
            using (FileStream lockedLegacy = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (!TryReadOrMigrateSecret(encrypted, legacy, TrimSecret, validator, out secret, out migrated, out errorCode) ||
                    migrated ||
                    !File.Exists(legacy) ||
                    !string.Equals(secret, "oauth-existing", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("SecretStore legacy cleanup failure self-test failed: " + errorCode);
                }
            }

            File.Delete(legacy);
            using (FileStream lockedTarget = new FileStream(encrypted, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                string oldDpapi = Protect("oauth-rewrite").Substring(DpapiV1Prefix.Length);
                string rewritePath = Path.Combine(root, "rewrite.bin");
                File.WriteAllText(rewritePath, oldDpapi, SharedEncoding.Utf8NoBom);
                using (FileStream lockedRewrite = new FileStream(rewritePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    if (TryReadOrMigrateSecret(rewritePath, legacy, TrimSecret, validator, out secret, out migrated, out errorCode) ||
                        migrated ||
                        !string.Equals(errorCode, "SECRET_IO", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("SecretStore atomic replace failure self-test failed: " + errorCode);
                    }
                }

                TryDeleteFile(rewritePath);
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

    private static string ReadSecretText(string path)
    {
        FileInfo info = new FileInfo(path);
        if (!info.Exists || info.Length < 0 || info.Length > MaxSecretFileBytes)
        {
            throw new FormatException("Secret file size is invalid.");
        }

        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static bool IsAllowedLegacyPlaintext(string value, Func<string, bool> validator)
    {
        return !string.IsNullOrEmpty(value) && validator != null && validator(value);
    }

    private static string ApplyNormalize(Func<string, string> normalize, string value)
    {
        return normalize == null ? TrimSecret(value) : normalize(value);
    }

    private static byte[] ComputeFileHash(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            return sha.ComputeHash(stream);
        }
    }

    private static bool HashesEqual(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        int difference = 0;
        for (int i = 0; i < left.Length; i++)
        {
            difference |= left[i] ^ right[i];
        }

        return difference == 0;
    }

    private static void DeleteMigratedLegacyFiles(string legacyTextPath)
    {
        if (string.IsNullOrWhiteSpace(legacyTextPath))
        {
            return;
        }

        try
        {
            string directory = Path.GetDirectoryName(legacyTextPath);
            string fileName = Path.GetFileName(legacyTextPath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
            {
                return;
            }

            string[] files = Directory.GetFiles(directory, fileName + ".migrated*");
            for (int i = 0; i < files.Length; i++)
            {
                TryDeleteFile(files[i]);
            }
        }
        catch
        {
            // Cleanup must not block a valid protected read.
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
