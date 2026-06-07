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

        double value = kbps / divisor;
        double roundedOneDecimal = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        // Keep precision for small values, but avoid unstable decimals once the display reaches 10.
        if (roundedOneDecimal >= 10.0)
        {
            return string.Format("{0:0} {1}", value, unit);
        }

        return string.Format("{0:0.0} {1}", value, unit);
    }
}
