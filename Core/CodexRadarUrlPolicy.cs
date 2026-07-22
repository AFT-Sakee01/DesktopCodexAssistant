using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>
/// Validates the server-advertised Codex Radar full-API endpoint before any transport is created.
/// The exact-origin rule is the primary SSRF boundary; DNS checks fail closed when the approved
/// host resolves to a local or otherwise non-public address.
/// </summary>
internal static class CodexRadarUrlPolicy
{
    internal const string AllowedHost = "codexradar.com";
    internal const string AllowedFullApiPath = "/api/v1/current";
    internal const int MaxUrlLength = 2048;

    internal static bool TryNormalizeFullApiUrl(string value, out Uri normalized, out string errorCode)
    {
        normalized = null;
        errorCode = string.Empty;
        string candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            errorCode = "EMPTY_URL";
            return false;
        }

        if (candidate.Length > MaxUrlLength)
        {
            errorCode = "URL_TOO_LONG";
            return false;
        }

        Uri uri;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri))
        {
            errorCode = "INVALID_URL";
            return false;
        }

        string normalizedHost = (uri.Host ?? string.Empty).TrimEnd('.').ToLowerInvariant();
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "SCHEME_NOT_ALLOWED";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errorCode = "USERINFO_NOT_ALLOWED";
            return false;
        }

        if (uri.Port != 443)
        {
            errorCode = "PORT_NOT_ALLOWED";
            return false;
        }

        if (uri.HostNameType != UriHostNameType.Dns ||
            !string.Equals(normalizedHost, AllowedHost, StringComparison.Ordinal))
        {
            errorCode = "HOST_NOT_ALLOWED";
            return false;
        }

        if (!string.Equals(uri.AbsolutePath, AllowedFullApiPath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            errorCode = "PATH_NOT_ALLOWED";
            return false;
        }

        UriBuilder builder = new UriBuilder(Uri.UriSchemeHttps, AllowedHost, 443, AllowedFullApiPath);
        normalized = builder.Uri;
        return true;
    }

    internal static bool TryValidateFullApiUrl(
        string value,
        Func<string, IPAddress[]> resolver,
        out Uri normalized,
        out string errorCode)
    {
        if (!TryNormalizeFullApiUrl(value, out normalized, out errorCode))
        {
            return false;
        }

        if (resolver == null)
        {
            normalized = null;
            errorCode = "DNS_RESOLVER_MISSING";
            return false;
        }

        IPAddress[] addresses;
        try
        {
            addresses = resolver(AllowedHost);
        }
        catch
        {
            normalized = null;
            errorCode = "DNS_FAILED";
            return false;
        }

        if (addresses == null || addresses.Length == 0)
        {
            normalized = null;
            errorCode = "DNS_EMPTY";
            return false;
        }

        HashSet<string> localAddresses = GetLocalAddressSet();
        for (int i = 0; i < addresses.Length; i++)
        {
            IPAddress address = addresses[i];
            if (!IsPublicRemoteAddress(address) ||
                (address != null && localAddresses.Contains(address.ToString())))
            {
                normalized = null;
                errorCode = "DNS_ADDRESS_NOT_PUBLIC";
                return false;
            }
        }

        return true;
    }

    internal static bool TryExecuteFullApi<T>(
        string value,
        Func<string, IPAddress[]> resolver,
        Func<Uri, T> transport,
        out T result,
        out string errorCode)
    {
        result = default(T);
        Uri normalized;
        if (!TryValidateFullApiUrl(value, resolver, out normalized, out errorCode))
        {
            return false;
        }

        if (transport == null)
        {
            errorCode = "TRANSPORT_MISSING";
            return false;
        }

        result = transport(normalized);
        return true;
    }

    private static HashSet<string> GetLocalAddressSet()
    {
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                IPInterfaceProperties properties = interfaces[i].GetIPProperties();
                foreach (UnicastIPAddressInformation item in properties.UnicastAddresses)
                {
                    if (item != null && item.Address != null)
                    {
                        result.Add(item.Address.ToString());
                    }
                }
            }
        }
        catch
        {
            // Exact-origin and non-public range checks remain active if interface enumeration fails.
        }

        return result;
    }

    private static bool IsPublicRemoteAddress(IPAddress address)
    {
        if (address == null || IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) ||
            address.Equals(IPAddress.IPv6Loopback))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicRemoteAddress(address.MapToIPv4());
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            int first = bytes[0];
            int second = bytes[1];
            if (first == 0 || first == 10 || first == 127 || first >= 224 ||
                (first == 100 && second >= 64 && second <= 127) ||
                (first == 169 && second == 254) ||
                (first == 172 && second >= 16 && second <= 31) ||
                (first == 192 && (second == 0 || second == 168)) ||
                (first == 198 && (second == 18 || second == 19 || second == 51)) ||
                (first == 203 && second == 0 && bytes[2] == 113))
            {
                return false;
            }

            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
                (bytes[0] & 0xFE) == 0xFC)
            {
                return false;
            }

            // 2001:db8::/32 is documentation-only and must never become a transport target.
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    internal static void RunSelfTest()
    {
        IPAddress[] publicDns = new IPAddress[] { IPAddress.Parse("104.21.10.20") };
        Uri normalized;
        string error;
        if (!TryValidateFullApiUrl(
                "https://CODEXRADAR.COM./api/v1/current",
                delegate { return publicDns; },
                out normalized,
                out error) ||
            normalized == null ||
            !string.Equals(normalized.AbsoluteUri, "https://codexradar.com/api/v1/current", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex Radar URL policy self-test: valid same-origin URL was rejected.");
        }

        string[] rejected = new string[]
        {
            "http://codexradar.com/api/v1/current",
            "https://user:secret@codexradar.com/api/v1/current",
            "https://codexradar.com:444/api/v1/current",
            "https://localhost/api/v1/current",
            "https://127.0.0.1/api/v1/current",
            "https://[::1]/api/v1/current",
            "https://2130706433/api/v1/current",
            "https://[::ffff:127.0.0.1]/api/v1/current",
            "https://codexradar.com/api/v1/other",
            "https://codexradar.com/api/v1/current?next=https://127.0.0.1/",
            "https://codexradar.com/api/v1/current#fragment",
            "https://codexradar.com/" + new string('a', MaxUrlLength)
        };

        int connectionAttempts = 0;
        for (int i = 0; i < rejected.Length; i++)
        {
            string ignored;
            int unused;
            if (TryExecuteFullApi<int>(
                rejected[i],
                delegate { return publicDns; },
                delegate
                {
                    connectionAttempts++;
                    return 1;
                },
                out unused,
                out ignored))
            {
                throw new InvalidOperationException("Codex Radar URL policy self-test: unsafe URL was accepted.");
            }
        }

        IPAddress[] unsafeDns = new IPAddress[]
        {
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("169.254.1.1"),
            IPAddress.Parse("::ffff:192.168.1.5")
        };
        for (int i = 0; i < unsafeDns.Length; i++)
        {
            int unused;
            string ignored;
            if (TryExecuteFullApi<int>(
                "https://codexradar.com/api/v1/current",
                delegate { return new IPAddress[] { unsafeDns[i] }; },
                delegate
                {
                    connectionAttempts++;
                    return 1;
                },
                out unused,
                out ignored))
            {
                throw new InvalidOperationException("Codex Radar URL policy self-test: private DNS result was accepted.");
            }
        }

        if (connectionAttempts != 0)
        {
            throw new InvalidOperationException("Codex Radar URL policy self-test: rejected targets reached transport.");
        }
    }
}
