using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcDiag.Platform.Windows.Network;

/// <summary>
/// Times a DNS lookup through the PC's configured DNS servers, past Windows' own cache (DnsQuery with
/// DNS_QUERY_BYPASS_CACHE), so a slow or failing DNS server shows. Looks up the name Windows itself uses to check its
/// connection (www.msftconnecttest.com).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class DnsCheck
{
    /// <summary>The name looked up.</summary>
    public const string Name = "www.msftconnecttest.com";

    /// <summary>
    /// The lookup's time in ms, or null when it failed (no answer, the name could not be resolved). Blocks for as long
    /// as the lookup takes (Windows gives up after several seconds); call it off the sampling thread.
    /// </summary>
    public static double? Lookup()
    {
        var started = Stopwatch.GetTimestamp();
        var status = DnsQuery_W(Name, TypeA, BypassCache, 0, out var records, 0);
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (records != 0)
        {
            DnsRecordListFree(records, FreeRecordList);
        }

        return status == 0 ? Math.Round(ms, 1) : null;
    }

    private const ushort TypeA = 1;
    private const uint BypassCache = 0x00000008;
    private const int FreeRecordList = 1;

    [LibraryImport("dnsapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DnsQuery_W(string name, ushort type, uint options, nint extra, out nint results, nint reserved);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsRecordListFree(nint records, int freeType);
}
