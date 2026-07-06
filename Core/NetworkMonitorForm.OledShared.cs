using System;
using System.Drawing;
using System.Globalization;

// Shared data extraction for the four OLED-safe restyle schemes (Typographic, AmberHud, WarmCard,
// Phosphor) added in 1.0.3.44. Severity is derived directly from the same state enums Classic
// switches on (NetworkAccessState, GfwProbeStatus, DnsServerStatus) rather than reusing a Classic
// color helper's specific accent hue, since those are not guaranteed blue-free by construction.
internal sealed partial class NetworkMonitorForm
{
    private struct OledNetItem
    {
        public string Label;
        public string Value;
        public OledVariantPainting.Severity Severity;
    }

    private OledNetItem[] GetNetworkMonitorOledItems()
    {
        OledNetItem[] items = new OledNetItem[7];
        items[0] = new OledNetItem { Label = "IP4", Value = BuildSingleAddressRowText(this.snapshot.IPv4, 15), Severity = OledVariantPainting.Severity.Neutral };
        items[1] = new OledNetItem { Label = "IP6", Value = BuildSingleAddressRowText(this.snapshot.IPv6, 24), Severity = OledVariantPainting.Severity.Neutral };
        items[2] = new OledNetItem { Label = "IF", Value = BuildInterfaceText(), Severity = OledVariantPainting.Severity.Neutral };
        items[3] = BuildDnsOledItem();
        items[4] = new OledNetItem { Label = "WIFI", Value = BuildWifiText(), Severity = OledVariantPainting.Severity.Neutral };
        items[5] = new OledNetItem { Label = "PING", Value = BuildConnectivityText(), Severity = GetAccessStateSeverity(GetDisplayAccessState()) };
        items[6] = new OledNetItem { Label = "GFW", Value = BuildGfwProbeText(), Severity = GetGfwProbeSeverity() };
        return items;
    }

    private static OledVariantPainting.Severity GetAccessStateSeverity(NetworkAccessState state)
    {
        if (state == NetworkAccessState.Online)
        {
            return OledVariantPainting.Severity.Good;
        }

        if (state == NetworkAccessState.NeedsValidation)
        {
            return OledVariantPainting.Severity.Warn;
        }

        if (state == NetworkAccessState.Offline || state == NetworkAccessState.AdapterMissing)
        {
            return OledVariantPainting.Severity.Danger;
        }

        return OledVariantPainting.Severity.Neutral;
    }

    private OledVariantPainting.Severity GetGfwProbeSeverity()
    {
        GfwProbeSnapshot gfw = this.snapshot == null ? null : this.snapshot.GfwProbe;
        if (gfw == null || !gfw.Enabled || gfw.Status == GfwProbeStatus.Disabled || gfw.Status == GfwProbeStatus.Unknown)
        {
            return OledVariantPainting.Severity.Neutral;
        }

        if (gfw.Status == GfwProbeStatus.Normal)
        {
            return OledVariantPainting.Severity.Good;
        }

        if (gfw.Status == GfwProbeStatus.Inconclusive || gfw.Status == GfwProbeStatus.Checking)
        {
            return OledVariantPainting.Severity.Warn;
        }

        return OledVariantPainting.Severity.Danger;
    }

    private OledNetItem BuildDnsOledItem()
    {
        DnsDisplayItem[] items = BuildDnsDisplayItems();
        if (items.Length == 0)
        {
            return new OledNetItem { Label = "DNS", Value = "--", Severity = OledVariantPainting.Severity.Neutral };
        }

        int visibleCount = Math.Min(3, items.Length);
        string value = string.Empty;
        DnsServerStatus worst = DnsServerStatus.Normal;
        for (int i = 0; i < visibleCount; i++)
        {
            value += (i == 0 ? string.Empty : ", ") + EmptyToDash(items[i].Address);
            if (GetDnsStatusPriority(items[i].Status) > GetDnsStatusPriority(worst))
            {
                worst = items[i].Status;
            }
        }

        int hiddenCount = items.Length - visibleCount;
        if (hiddenCount > 0)
        {
            value += " +" + hiddenCount.ToString(CultureInfo.InvariantCulture);
            for (int i = visibleCount; i < items.Length; i++)
            {
                if (GetDnsStatusPriority(items[i].Status) > GetDnsStatusPriority(worst))
                {
                    worst = items[i].Status;
                }
            }
        }

        OledVariantPainting.Severity severity = OledVariantPainting.Severity.Good;
        if (worst == DnsServerStatus.Hijacked)
        {
            severity = OledVariantPainting.Severity.Danger;
        }
        else if (worst == DnsServerStatus.Problem)
        {
            severity = OledVariantPainting.Severity.Warn;
        }

        return new OledNetItem { Label = "DNS", Value = value, Severity = severity };
    }
}
