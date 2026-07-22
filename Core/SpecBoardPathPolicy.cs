using System;
using System.IO;

internal static class SpecBoardPathPolicy
{
    private static readonly string[] DocumentExtensions =
    {
        ".md", ".markdown", ".txt", ".json", ".jsonl"
    };

    public static bool TryResolve(string projectRoot, string specPath, out string absolutePath)
    {
        absolutePath = string.Empty;
        try
        {
            string root = NormalizeRoot(projectRoot);
            string relative = (specPath ?? string.Empty).Trim().Trim('"');
            if (root.Length == 0 || relative.Length == 0 ||
                IsRootedOrDevicePath(relative) || ContainsTraversal(relative))
            {
                return false;
            }

            string candidate = Path.GetFullPath(Path.Combine(
                root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            string rootPrefix = EnsureTrailingSeparator(root);
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                ContainsExistingReparsePoint(root, candidate))
            {
                return false;
            }

            absolutePath = candidate;
            return true;
        }
        catch
        {
            absolutePath = string.Empty;
            return false;
        }
    }

    public static bool TryResolveDocument(string projectRoot, string specPath, out string documentPath)
    {
        documentPath = string.Empty;
        string candidate;
        if (!TryResolve(projectRoot, specPath, out candidate) ||
            !File.Exists(candidate) ||
            !IsDocumentExtension(Path.GetExtension(candidate)))
        {
            return false;
        }

        documentPath = candidate;
        return true;
    }

    public static string ResolveOpenTarget(string projectRoot, string specPath)
    {
        string documentPath;
        if (TryResolveDocument(projectRoot, specPath, out documentPath))
        {
            return documentPath;
        }

        string candidate;
        if (TryResolve(projectRoot, specPath, out candidate))
        {
            string directory = File.Exists(candidate) ? Path.GetDirectoryName(candidate) : candidate;
            while (!string.IsNullOrEmpty(directory))
            {
                if (Directory.Exists(directory) && IsInsideRoot(projectRoot, directory))
                {
                    return directory;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        string root = NormalizeRoot(projectRoot);
        return root.Length > 0 ? root : string.Empty;
    }

    public static string ResolveRevealTarget(string projectRoot, string specPath)
    {
        string candidate;
        if (TryResolve(projectRoot, specPath, out candidate) && File.Exists(candidate))
        {
            return candidate;
        }

        return ResolveOpenTarget(projectRoot, specPath);
    }

    internal static void RunSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-SpecBoardPath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Docs", "Technical"));
        try
        {
            string relative = "Docs/Technical/Safe-SPEC.md";
            string expected = Path.Combine(root, "Docs", "Technical", "Safe-SPEC.md");
            File.WriteAllText(expected, "safe", SharedEncoding.Utf8NoBom);
            string resolved;
            if (!TryResolveDocument(root, relative, out resolved) ||
                !string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase) ||
                TryResolve(root, "../outside.md", out resolved) ||
                TryResolve(root, Path.Combine(root, "absolute.md"), out resolved) ||
                TryResolve(root, @"\\server\share\outside.md", out resolved) ||
                TryResolve(root, @"\\?\C:\outside.md", out resolved))
            {
                throw new InvalidOperationException("Spec Board path boundary self-test failed.");
            }

            string fileRoot = Path.Combine(root, "root-file.txt");
            File.WriteAllText(fileRoot, "not a directory", SharedEncoding.Utf8NoBom);
            if (TryResolve(fileRoot, relative, out resolved))
            {
                throw new InvalidOperationException("Spec Board file-root rejection self-test failed.");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string NormalizeRoot(string projectRoot)
    {
        try
        {
            string value = (projectRoot ?? string.Empty).Trim().Trim('"');
            if (value.Length == 0 || !Directory.Exists(value))
            {
                return string.Empty;
            }

            string full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return ContainsExistingReparsePoint(full, full) ? string.Empty : full;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsRootedOrDevicePath(string value)
    {
        return Path.IsPathRooted(value) ||
            value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            value.StartsWith(@"\??\", StringComparison.Ordinal);
    }

    private static bool ContainsTraversal(string value)
    {
        string[] segments = value.Replace('\\', '/').Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], "..", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDocumentExtension(string extension)
    {
        for (int i = 0; i < DocumentExtensions.Length; i++)
        {
            if (string.Equals(extension, DocumentExtensions[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideRoot(string projectRoot, string candidate)
    {
        string root = NormalizeRoot(projectRoot);
        if (root.Length == 0)
        {
            return false;
        }

        string full = Path.GetFullPath(candidate);
        return full.StartsWith(EnsureTrailingSeparator(root), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsExistingReparsePoint(string root, string candidate)
    {
        string current = Path.GetFullPath(root);
        if (HasReparsePoint(current))
        {
            return true;
        }

        string rootPrefix = EnsureTrailingSeparator(current);
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string relative = candidate.Length <= rootPrefix.Length
            ? string.Empty
            : candidate.Substring(rootPrefix.Length);
        string[] segments = relative.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            if ((Directory.Exists(current) || File.Exists(current)) && HasReparsePoint(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }
}
