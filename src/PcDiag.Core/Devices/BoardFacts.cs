using System.Text.RegularExpressions;

namespace PcDiag.Core.Devices;

/// <summary>Facts about the motherboard that Windows does not state directly.</summary>
public static partial class BoardFacts
{
    /// <summary>
    /// The chipset: from the LPC/eSPI bridge's name when Intel's chipset driver names it ("Intel(R) Z790 Chipset
    /// LPC/eSPI Controller - 7A04" → "Intel Z790"); else from the board's name, which nearly always carries it
    /// ("ROG STRIX Z890-E GAMING WIFI" → "Z890"); else the Intel chipset family from the bridge's PCI device ID.
    /// </summary>
    /// <param name="bridgeName">The LPC/eSPI (ISA) bridge's name.</param>
    /// <param name="bridgeVendorId">Its PCI vendor ID.</param>
    /// <param name="bridgeDeviceId">Its PCI device ID.</param>
    /// <param name="boardProduct">The board's product name (SMBIOS).</param>
    /// <param name="processor">The processor's name (Intel or AMD).</param>
    public static string? Chipset(string? bridgeName, string? bridgeVendorId, string? bridgeDeviceId, string? boardProduct, string? processor)
    {
        if (bridgeName is not null && NamedChipset().Match(bridgeName) is { Success: true } named)
        {
            return $"{named.Groups[1].Value} {named.Groups[2].Value.ToUpperInvariant()}";
        }

        var maker = string.Equals(bridgeVendorId, "1022", StringComparison.OrdinalIgnoreCase) || (processor?.Contains("AMD", StringComparison.OrdinalIgnoreCase) ?? false) ? "AMD" : "Intel";
        if (boardProduct is not null && BoardChipset().Match(boardProduct) is { Success: true } board)
        {
            var model = board.Groups[1].Value.ToUpperInvariant();
            // A600/A620 and X-series (X570, X670, X870) are AMD; Z, H, B, Q and W can be either, so the processor decides.
            var boardMaker = model.StartsWith('A') || model.StartsWith('X') ? "AMD" : maker;
            return $"{boardMaker} {model}";
        }

        if (string.Equals(bridgeVendorId, "8086", StringComparison.OrdinalIgnoreCase) && bridgeDeviceId is { Length: 4 } id)
        {
            return id.ToUpperInvariant() switch
            {
                var d when d.StartsWith("7F", StringComparison.Ordinal) => "Intel 800 Series",
                var d when d.StartsWith("7A0", StringComparison.Ordinal) || d.StartsWith("7A1", StringComparison.Ordinal) => "Intel 700 Series",
                var d when d.StartsWith("7A8", StringComparison.Ordinal) => "Intel 600 Series",
                var d when d.StartsWith("438", StringComparison.Ordinal) => "Intel 500 Series",
                var d when d.StartsWith("068", StringComparison.Ordinal) || d.StartsWith("A3C", StringComparison.Ordinal) => "Intel 400 Series",
                var d when d.StartsWith("A30", StringComparison.Ordinal) => "Intel 300 Series",
                _ => null,
            };
        }

        return null;
    }

    [GeneratedRegex(@"^(Intel|AMD)(?:\(R\))?\s+(?:\S+\s+)??([A-Z]{1,2}\d{3}[A-Z]?)\s+(?:Series\s+)?Chipset", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedChipset();

    [GeneratedRegex(@"(?<![A-Z0-9])([ZHBQWXA]\d{3}E?)(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoardChipset();
}
