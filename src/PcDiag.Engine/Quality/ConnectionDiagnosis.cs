using System.Globalization;
using PcDiag.Core.Quality;

namespace PcDiag.Engine.Quality;

/// <summary>One hop's figures over a period.</summary>
/// <param name="Ms">The average round trip.</param>
/// <param name="P95Ms">The 95th percentile of the minutes' longest round trips.</param>
/// <param name="Loss">The share of pings lost (%).</param>
/// <param name="Sent">Minutes with pings to it.</param>
public sealed record HopSummary(double? Ms, double? P95Ms, double? Loss, int Sent);

/// <summary>An outage: minutes in which most pings to the internet were lost.</summary>
public sealed record Outage(DateTimeOffset From, DateTimeOffset To, double Loss);

/// <summary>The connection over a period, hop by hop, with its findings.</summary>
/// <param name="Minutes">Minutes with pings.</param>
/// <param name="Router">The router (home network).</param>
/// <param name="Provider">The provider's first hop.</param>
/// <param name="Internet">The internet.</param>
/// <param name="JitterMs">The average change between consecutive round trips to the internet.</param>
/// <param name="DnsMs">The average DNS lookup.</param>
/// <param name="DnsChecks">DNS lookups.</param>
/// <param name="DnsFailed">Failed DNS lookups.</param>
/// <param name="WifiMin">The weakest Wi-Fi signal (%), when on Wi-Fi.</param>
/// <param name="Outages">Minutes most pings to the internet were lost, merged.</param>
/// <param name="Findings">What the figures show, most serious first.</param>
public sealed record ConnectionReport(int Minutes, HopSummary Router, HopSummary Provider, HopSummary Internet, double? JitterMs, double? DnsMs, int DnsChecks, int DnsFailed,
    double? WifiMin, IReadOnlyList<Outage> Outages, IReadOnlyList<QualityFinding> Findings);

/// <summary>
/// Where a connection problem is, from the quality history's minutes: pings lost or slow already at the router (the
/// home network: Wi-Fi or cable), only after it at the provider's first hop (the provider's line), or only beyond the
/// provider; round trips that rise while the connection is busy (bufferbloat), and DNS.
/// </summary>
public static class ConnectionDiagnosis
{
    /// <summary>Lost pings (%) worth a finding.</summary>
    public const double LossWorthNoting = 1;

    /// <summary>A minute with this share (%) or more of the internet's pings lost is part of an outage.</summary>
    public const double OutageLoss = 50;

    /// <summary>The figures and findings for a period.</summary>
    public static ConnectionReport Analyze(IReadOnlyList<QualityMinute> minutes, IReadOnlyList<NetworkProgram>? programs, Func<DateTimeOffset, string> clock)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        ArgumentNullException.ThrowIfNull(clock);
        var pinged = minutes.Where(m => m.Internet.Loss is not null || m.Router.Loss is not null).OrderBy(m => m.At).ToList();
        var router = Hop(pinged, m => m.Router);
        var provider = Hop(pinged, m => m.Provider);
        var internet = Hop(pinged, m => m.Internet);
        var jitter = Weighted(pinged, m => m.InternetJitter);
        var dnsChecks = minutes.Sum(m => m.DnsChecks);
        var dnsFailed = minutes.Sum(m => m.DnsFailed);
        var dnsMs = Weighted(minutes, m => m.DnsMs);
        var wifi = minutes.Where(m => m.WifiMin is not null).Select(m => m.WifiMin!.Value).DefaultIfEmpty(double.NaN).Min() is var w && !double.IsNaN(w) ? w : (double?)null;
        var outages = Outages(pinged);
        var findings = new List<QualityFinding>();
        if (pinged.Count > 0)
        {
            Losses(router, provider, internet, wifi, findings);
            Delays(router, provider, internet, wifi, findings);
            Bufferbloat(pinged, programs, findings);
            if (outages.Count > 0)
            {
                var longest = outages.MaxBy(o => o.To - o.From)!;
                findings.Add(new QualityFinding(router.Loss >= OutageLoss / 2 ? "home-network" : "internet", 3,
                    $"The internet could not be reached {outages.Count} time{(outages.Count == 1 ? "" : "s")} (most pings lost for a minute or more), the longest from {clock(longest.From)} to {clock(longest.To + TimeSpan.FromMinutes(1))}."));
            }
        }

        if (dnsChecks > 0 && (dnsFailed >= 2 || dnsMs >= 200))
        {
            var failed = dnsFailed > 0 ? $"{dnsFailed} of {dnsChecks} DNS lookups failed" : $"DNS lookups took {N(dnsMs!.Value)} ms on average";
            findings.Add(new QualityFinding("dns", dnsFailed >= 5 ? 2 : 1,
                $"{failed}: websites and game servers may be slow to find; another DNS server (your router's, or a public one such as 1.1.1.1) could help."));
        }

        if (findings.Count == 0 && pinged.Count > 0)
        {
            findings.Add(new QualityFinding("internet", 0,
                $"No lost pings or slow hops: the router answered in {Ms(router.Ms)}, the provider in {Ms(provider.Ms)} and the internet in {Ms(internet.Ms)} on average."));
        }

        return new ConnectionReport(pinged.Count, router, provider, internet, jitter, dnsMs, dnsChecks, dnsFailed, wifi, outages, findings.OrderByDescending(f => f.Severity).ToList());
    }

    private static void Losses(HopSummary router, HopSummary provider, HopSummary internet, double? wifi, List<QualityFinding> findings)
    {
        var lost = internet.Loss ?? 0;
        if (lost < LossWorthNoting && (router.Loss ?? 0) < LossWorthNoting)
        {
            return;
        }

        var severity = lost >= 5 ? 3 : 2;
        if ((router.Loss ?? 0) >= LossWorthNoting && (router.Loss ?? 0) >= lost * 0.5)
        {
            var signal = wifi is { } s ? $" The Wi-Fi signal fell to {N(s)} %." : "";
            findings.Add(new QualityFinding("home-network", severity,
                $"{N(router.Loss!.Value)} % of the pings to the router were lost ({N(lost)} % to the internet): the problem is in the home network, before the provider; a cable instead of Wi-Fi, or the router closer, could help.{signal}"));
        }
        else if (provider.Loss is { } p && p >= LossWorthNoting && p >= lost * 0.5)
        {
            findings.Add(new QualityFinding("provider", severity,
                $"{N(p)} % of the pings to the provider's first hop were lost while the router answered {N(100 - (router.Loss ?? 0))} % of them: the provider's line or equipment may be the problem; worth reporting to the provider with these times."));
        }
        else
        {
            var hop = provider.Sent > 0 ? $", {N(provider.Loss ?? 0)} % to the provider's first hop" : "";
            findings.Add(new QualityFinding("internet", severity,
                $"{N(lost)} % of the pings to the internet were lost ({N(router.Loss ?? 0)} % to the router{hop}): the losses are beyond the home network, further out on the internet or at the provider's exchange."));
        }
    }

    private static void Delays(HopSummary router, HopSummary provider, HopSummary internet, double? wifi, List<QualityFinding> findings)
    {
        if (router.Ms is { } r && (r >= 10 || router.P95Ms >= 50))
        {
            var signal = wifi is { } s ? $" (the Wi-Fi signal fell to {N(s)} %)" : "";
            findings.Add(new QualityFinding("home-network", 2,
                $"The router answered in {N(r)} ms on average and up to {Ms(router.P95Ms)} in a bad minute{signal}: a home network usually answers in a few ms; Wi-Fi interference or a busy router may slow every connection."));
        }
        else if (provider.Ms is { } p && internet.Ms is { } i && router.Ms is { } r2 && p - r2 >= 40 && i - p < 40)
        {
            findings.Add(new QualityFinding("provider", 1,
                $"The provider's first hop answered in {N(p)} ms against {N(r2)} ms for the router: most of the delay to the internet ({N(i)} ms) is on the provider's line."));
        }
    }

    /// <summary>Round trips that rise in the minutes the connection is busiest: a download or upload filling the line.</summary>
    private static void Bufferbloat(List<QualityMinute> minutes, IReadOnlyList<NetworkProgram>? programs, List<QualityFinding> findings)
    {
        var withTraffic = minutes.Where(m => m.NetBps is not null && m.Internet.Ms is not null).ToList();
        if (withTraffic.Count < 10)
        {
            return;
        }

        var sorted = withTraffic.OrderBy(m => m.NetBps).ToList();
        var quiet = sorted.Take(sorted.Count / 2).ToList();
        var busy = sorted.Skip(sorted.Count * 3 / 4).ToList();
        var quietMs = quiet.Average(m => m.Internet.Ms!.Value);
        var busyMs = busy.Average(m => m.Internet.Ms!.Value);
        var busyBps = busy.Average(m => m.NetBps!.Value);
        if (busyMs >= Math.Max(2 * quietMs, quietMs + 30) && busyBps >= 1_000_000)
        {
            var top = programs?.OrderByDescending(p => p.ReceiveBps + p.SendBps).FirstOrDefault(p => p.ReceiveBps + p.SendBps >= 100_000);
            var who = top is not null ? $" {top.Name} moved the most data ({Diagnostics.AnalysisContext.FormatBytes(top.ReceiveBps + top.SendBps)}/s on average)." : "";
            findings.Add(new QualityFinding("bufferbloat", 2,
                $"Round trips to the internet rose to {N(busyMs)} ms in the busiest minutes ({Diagnostics.AnalysisContext.FormatBytes(busyBps)}/s) against {N(quietMs)} ms in quiet ones: a download or upload may fill the line and delay everything else (bufferbloat); limiting its speed, or the router's smart queueing (SQM), could help.{who}"));
        }
    }

    private static List<Outage> Outages(List<QualityMinute> minutes)
    {
        var outages = new List<Outage>();
        Outage? current = null;
        foreach (var m in minutes.Where(m => m.Internet.Loss >= OutageLoss))
        {
            if (current is { } c && m.At - c.To <= TimeSpan.FromMinutes(1))
            {
                current = c with { To = m.At, Loss = Math.Max(c.Loss, m.Internet.Loss!.Value) };
            }
            else
            {
                if (current is { } done)
                {
                    outages.Add(done);
                }

                current = new Outage(m.At, m.At, m.Internet.Loss!.Value);
            }
        }

        if (current is { } last)
        {
            outages.Add(last);
        }

        return outages;
    }

    private static HopSummary Hop(List<QualityMinute> minutes, Func<QualityMinute, HopMinute> hop)
    {
        var with = minutes.Where(m => hop(m).Loss is not null).ToList();
        if (with.Count == 0)
        {
            return new HopSummary(null, null, null, 0);
        }

        var seconds = with.Sum(m => m.Seconds);
        var loss = seconds > 0 ? with.Sum(m => hop(m).Loss!.Value * m.Seconds) / seconds : with.Average(m => hop(m).Loss!.Value);
        var ms = Weighted(with, m => hop(m).Ms);
        var maxes = with.Where(m => hop(m).Max is not null).Select(m => hop(m).Max!.Value).Order().ToList();
        var p95 = maxes.Count > 0 ? maxes[(int)Math.Floor((maxes.Count - 1) * 0.95)] : (double?)null;
        return new HopSummary(ms is { } v ? Math.Round(v, 1) : null, p95, Math.Round(loss, 2), with.Count);
    }

    private static double? Weighted(IEnumerable<QualityMinute> minutes, Func<QualityMinute, double?> value)
    {
        double sum = 0, weight = 0;
        foreach (var m in minutes)
        {
            if (value(m) is { } v)
            {
                sum += v * Math.Max(1, m.Seconds);
                weight += Math.Max(1, m.Seconds);
            }
        }

        return weight > 0 ? Math.Round(sum / weight, 1) : null;
    }

    private static string Ms(double? value) => value is { } v ? $"{N(v)} ms" : "no time";

    private static string N(double value) => value.ToString(Math.Abs(value) < 10 && value != Math.Round(value) ? "0.#" : "0", CultureInfo.InvariantCulture);
}
