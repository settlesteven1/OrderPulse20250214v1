namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Detects shipping carrier from tracking number format and provides tracking page URLs.
/// Supports Amazon Logistics, UPS, USPS, and FedEx.
/// </summary>
public static class CarrierDetector
{
    /// <summary>
    /// Identifies the carrier from a tracking number and returns tracking info.
    /// Returns null if the carrier cannot be determined.
    /// </summary>
    public static CarrierInfo? Detect(string trackingNumber)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber))
            return null;

        var tn = trackingNumber.Trim();

        // Amazon Logistics: TBA prefix
        if (tn.StartsWith("TBA", StringComparison.OrdinalIgnoreCase))
        {
            return new CarrierInfo(
                "Amazon Logistics",
                $"https://track.amazon.com/tracking/{tn}",
                tn);
        }

        // UPS: 1Z prefix
        if (tn.StartsWith("1Z", StringComparison.OrdinalIgnoreCase))
        {
            return new CarrierInfo(
                "UPS",
                $"https://www.ups.com/track?tracknum={tn}",
                tn);
        }

        // USPS: starts with 9400, 9200, 9202, 9205, 9208, 9261, 9270, 9274,
        //        or is 20-22 digits starting with 92
        if (tn.StartsWith("9400") || tn.StartsWith("9200") || tn.StartsWith("9202") ||
            tn.StartsWith("9205") || tn.StartsWith("9261") || tn.StartsWith("9270") ||
            tn.StartsWith("9274") || tn.StartsWith("9208") ||
            (tn.StartsWith("92") && tn.Length >= 20 && tn.Length <= 22 && IsAllDigits(tn)))
        {
            return new CarrierInfo(
                "USPS",
                $"https://tools.usps.com/go/TrackConfirmAction?tLabels={tn}",
                tn);
        }

        // FedEx: starts with 6129, 0096, 0200, 7489, or is 12-15 digits
        if (tn.StartsWith("6129") || tn.StartsWith("0096") ||
            tn.StartsWith("0200") || tn.StartsWith("7489"))
        {
            return new CarrierInfo(
                "FedEx",
                $"https://www.fedex.com/fedextrack/?trknbr={tn}",
                tn);
        }

        // FedEx: 12-15 digit number (common format)
        if (tn.Length >= 12 && tn.Length <= 15 && IsAllDigits(tn))
        {
            return new CarrierInfo(
                "FedEx",
                $"https://www.fedex.com/fedextrack/?trknbr={tn}",
                tn);
        }

        return null;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }
}

/// <summary>
/// Carrier identification result with tracking page URL.
/// </summary>
public record CarrierInfo(string CarrierName, string TrackingUrl, string TrackingNumber);
