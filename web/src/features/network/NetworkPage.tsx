import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { apiGet } from '@/lib/api/client';
import type { ConnectionResponse, HopSummary } from '@/lib/api/types';
import { formatBytesPerSecond, formatTime } from '@/lib/format';
import { Findings, ms, pct, Stat, toChart } from '@/features/quality/shared';

const RANGES = [
  { id: 'hour', label: 'Last hour', ms: 3_600_000 },
  { id: 'day', label: 'Last 24 hours', ms: 86_400_000 },
  { id: 'week', label: 'Last 7 days', ms: 7 * 86_400_000 },
] as const;

/**
 * The connection hop by hop: pings to the router (the home network), the provider's first hop and the internet, each
 * second, with lost pings, round trips and DNS lookups, where a problem is, and which programs used the network most.
 */
export function NetworkPage() {
  const [range, setRange] = useState<(typeof RANGES)[number]['id']>('day');
  const span = RANGES.find((r) => r.id === range)!.ms;
  const { data, error } = useQuery({
    queryKey: ['connection', range],
    queryFn: ({ signal }) => apiGet<ConnectionResponse>(`/connection?from=${Date.now() - span}`, signal),
    refetchInterval: 60_000,
  });
  const chart = useMemo(
    () =>
      data
        ? toChart(
            data.minutes,
            (m) => m.at,
            [
              { id: 'router', label: 'Router', unit: 'ms', value: (m) => m.router.ms, high: (m) => m.router.max },
              { id: 'provider', label: 'Provider', unit: 'ms', value: (m) => m.provider.ms, high: (m) => m.provider.max },
              { id: 'internet', label: 'Internet', unit: 'ms', value: (m) => m.internet.ms, high: (m) => m.internet.max },
              { id: 'loss', label: 'Lost pings to the internet', unit: '%', value: (m) => m.internet.loss },
            ],
            'Connection',
          )
        : null,
    [data],
  );

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <div className="flex flex-col gap-1">
          <h1 className="text-xl font-semibold">Network</h1>
          <p className="text-sm text-muted">
            A ping each second to your router, the provider&apos;s first hop and the internet shows where a problem is: lost or slow already at the router is the
            home network (Wi-Fi or cable); only after it, the provider&apos;s line; only at the internet, further out.
          </p>
        </div>
        <div className="flex gap-1" role="group" aria-label="Period">
          {RANGES.map((r) => (
            <Button key={r.id} size="sm" variant={r.id === range ? 'default' : 'outline'} onClick={() => setRange(r.id)}>
              {r.label}
            </Button>
          ))}
        </div>
      </header>

      {error && <p className="text-sm text-danger">The connection history could not be loaded.</p>}
      {!data && !error && <p className="text-sm text-muted">Loading…</p>}
      {data && data.report.minutes === 0 && (
        <Card>
          <CardContent className="py-4 text-sm text-muted">
            No connection checks in this period. They run while the recorder runs (Settings › Recording › Connection checks) and are kept minute by minute.
          </CardContent>
        </Card>
      )}
      {data && data.report.minutes > 0 && chart && (
        <>
          <Card>
            <CardHeader>
              <CardTitle>What the checks show</CardTitle>
            </CardHeader>
            <CardContent className="flex flex-col gap-4">
              <Findings findings={data.report.findings} />
              <div className="grid gap-2 sm:grid-cols-3">
                <Hop name="Router" what="your home network" hop={data.report.router} />
                <Hop name="Provider" what="the first hop after the router" hop={data.report.provider} />
                <Hop name="Internet" what="1.1.1.1" hop={data.report.internet} />
              </div>
              <div className="flex flex-wrap gap-2">
                <Stat label="Jitter" value={ms(data.report.jitterMs)} hint="change between pings" />
                <Stat label="DNS lookups" value={ms(data.report.dnsMs)} hint={`${data.report.dnsFailed} of ${data.report.dnsChecks} failed`} />
                {data.report.wifiMin != null && <Stat label="Weakest Wi-Fi signal" value={pct(data.report.wifiMin)} />}
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Round trips and lost pings</CardTitle>
            </CardHeader>
            <CardContent>
              <TimeSeriesChart result={chart.result} catalog={chart.catalog} band label="Round trips and lost pings" className="h-72" />
            </CardContent>
          </Card>

          {data.report.outages.length > 0 && (
            <Card>
              <CardHeader>
                <CardTitle>When the internet could not be reached</CardTitle>
              </CardHeader>
              <CardContent>
                <ul className="flex flex-col gap-1 text-sm">
                  {data.report.outages.map((o) => (
                    <li key={o.from}>
                      {formatTime(o.from)} to {formatTime(new Date(Date.parse(o.to) + 60_000).toISOString())}: up to {pct(o.loss)} of pings lost
                    </li>
                  ))}
                </ul>
              </CardContent>
            </Card>
          )}

          <Card>
            <CardHeader>
              <CardTitle>Programs using the network</CardTitle>
            </CardHeader>
            <CardContent>
              {data.programs.length === 0 ? (
                <p className="text-sm text-muted">Programs are stored with their network use only while the recorder records.</p>
              ) : (
                <table className="w-full text-sm">
                  <thead className="text-left text-xs text-muted">
                    <tr>
                      <th className="py-1 font-medium">Program</th>
                      <th className="py-1 text-right font-medium">Download</th>
                      <th className="py-1 text-right font-medium">Upload</th>
                      <th className="py-1 text-right font-medium">Busiest second</th>
                      <th className="py-1 text-right font-medium" title="TCP segments sent again because the other side did not acknowledge them in time">
                        Resent
                      </th>
                    </tr>
                  </thead>
                  <tbody className="tabular-nums">
                    {data.programs.map((p) => (
                      <tr key={p.name} className="border-t border-border">
                        <td className="py-1">{p.name}</td>
                        <td className="py-1 text-right">{formatBytesPerSecond(p.receiveBps)}</td>
                        <td className="py-1 text-right">{formatBytesPerSecond(p.sendBps)}</td>
                        <td className="py-1 text-right">{formatBytesPerSecond(p.peakBps)}</td>
                        <td className={`py-1 text-right ${p.retransmitsPerSec >= 1 ? 'text-warning' : ''}`}>{p.retransmitsPerSec.toFixed(1)}/s</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </CardContent>
          </Card>
        </>
      )}
    </div>
  );
}

function Hop({ name, what, hop }: { name: string; what: string; hop: HopSummary }) {
  const lost = hop.loss ?? 0;
  return (
    <div className="flex flex-col rounded-lg border border-border bg-surface px-3 py-2 text-sm">
      <span className="font-medium">
        {name} <span className="text-xs font-normal text-muted">{what}</span>
      </span>
      {hop.sent === 0 ? (
        <span className="text-muted">Not checked</span>
      ) : (
        <span className="tabular-nums">
          {ms(hop.ms)} average · {ms(hop.p95Ms)} in a bad minute · <span className={lost >= 1 ? 'text-danger' : lost > 0 ? 'text-warning' : ''}>{pct(hop.loss)} lost</span>
        </span>
      )}
    </div>
  );
}
