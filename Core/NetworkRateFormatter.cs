using System;

internal static class NetworkRateFormatter
{
    public static string Format(double bytesPerSecond)
    {
        // PDH reports bytes/sec; the UI intentionally uses decimal network-style bit rates.
        double kbps = Math.Max(0.0, bytesPerSecond) * 8.0 / 1000.0;
        string unit = "Kbps";
        double divisor = 1.0;

        if (kbps >= 1000000.0)
        {
            unit = "Gbps";
            divisor = 1000000.0;
        }
        else if (kbps >= 1000.0)
        {
            unit = "Mbps";
            divisor = 1000.0;
        }

        return FormatScaledValue(kbps / divisor, unit);
    }

    public static string FormatStorage(double bytesPerSecond)
    {
        // Disk throughput follows the conventional byte-rate notation. Keep KB/s as the minimum
        // unit so an idle counter remains comparable with the existing compact network readout.
        double kilobytesPerSecond = Math.Max(0.0, bytesPerSecond) / 1000.0;
        string unit = "KB/s";
        double divisor = 1.0;

        if (kilobytesPerSecond >= 1000000.0)
        {
            unit = "GB/s";
            divisor = 1000000.0;
        }
        else if (kilobytesPerSecond >= 1000.0)
        {
            unit = "MB/s";
            divisor = 1000.0;
        }

        return FormatScaledValue(kilobytesPerSecond / divisor, unit);
    }

    private static string FormatScaledValue(double value, string unit)
    {
        double roundedOneDecimal = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        // Keep precision for small values, but avoid unstable decimals once the display reaches 10.
        if (roundedOneDecimal >= 10.0)
        {
            return string.Format("{0:0} {1}", value, unit);
        }

        return string.Format("{0:0.0} {1}", value, unit);
    }

    internal static void RunSelfTest()
    {
        if (Format(125.0) != "1.0 Kbps" ||
            FormatStorage(0.0) != "0.0 KB/s" ||
            FormatStorage(1500.0) != "1.5 KB/s" ||
            FormatStorage(1500000.0) != "1.5 MB/s" ||
            FormatStorage(1500000000.0) != "1.5 GB/s")
        {
            throw new InvalidOperationException("Network bit-rate and storage byte-rate units must remain distinct.");
        }

        Console.WriteLine("Rate formatter: PASS network bits, storage bytes, decimal KB/MB/GB scaling");
    }
}
