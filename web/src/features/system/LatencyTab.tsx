import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo } from 'react';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useToast } from '@/components/ui/toast';
import { apiGet, apiPost } from '@/lib/api/client';
import type { LatencyResponse, MeasureResponse, MeasurementView } from '@/lib/api/types';
import { formatDateTime } from '@/lib/format';
import { Findings, toChart } from '@/features/quality/shared';

const REASON: Record<string, string> = {
  measure: 'asked for',
  dpc: 'drivers held a processor long',
  stutter: 'a game stuttered',
};

/** "1.2 ms", "350 µs". */
function us(value: number): string {
  return value >= 1000 ? `${(value / 1000).toFixed(1)} ms` : `${Math.round(value)} µs`;
}

/**
 * Driver latency, as LatencyMon measures it: how long drivers hold a processor in DPCs and interrupts (each minute, the
 * busiest processor), and measurements that time every DPC and ISR for 10 s and name the driver it belongs to.
 */
export function LatencyTab() {
  const queryClient = useQueryClient();
  const toast = useToast();
  const { data, error } = useQuery({
    queryKey: ['latency'],
    queryFn: ({ signal }) => apiGet<LatencyResponse>(`/latency?from=${Date.now() - 86_400_000}`, signal),
    refetchInterval: (query) => (query.state.data?.status?.measuring ? 2000 : 60_000),
  });
  const measure = useMutation({
    mutationFn: () => apiPost<MeasureResponse>('/latency/measure', {}),
    onSuccess: (answer) => {
      if (answer.problem && !answer.status?.measuring) toast.show({ title: answer.problem, tone: 'error' });
      else toast.show({ title: 'Measuring the drivers for 10 s…', tone: 'success' });
      void queryClient.invalidateQueries({ queryKey: ['latency'] });
    },
  });
  const chart = useMemo(
    () =>
      data
        ? toChart(
            data.minutes,
            (m) => m.at,
            [
              { id: 'dpc', label: 'DPC time (busiest processor)', unit: '%', value: (m) => m.dpcCoreMax },
              { id: 'isr', label: 'Interrupt time (busiest processor)', unit: '%', value: (m) => m.isrCoreMax },
            ],
            'Driver latency',
          )
        : null,
    [data],
  );

  if (error) return <p className="text-sm text-danger">Driver latency could not be loaded.</p>;
  if (!data || !chart) return <p className="text-sm text-muted">Loading…</p>;
  const measuring = data.status?.measuring ?? false;
  return (
    <div className="flex flex-col gap-4">
      <Card>
        <CardHeader>
          <CardTitle>Driver latency in the last 24 hours</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <p className="text-sm text-muted">
            While a driver runs a deferred procedure call (DPC) or an interrupt routine (ISR), nothing else gets that processor: under 500 µs at a time is normal;
            longer, sound may crackle and frames and input come late. A measurement times every DPC and ISR for 10 s and names the driver, as LatencyMon does; one
            also runs by itself when drivers hold a processor long or a game stutters.
          </p>
          <Findings findings={data.findings} empty="No driver time recorded yet: it is kept minute by minute while the recorder runs." />
          <div className="flex flex-wrap items-center gap-3">
            <Button variant="outline" disabled={measuring || measure.isPending || data.problem != null} onClick={() => measure.mutate()}>
              {measuring ? 'Measuring…' : 'Measure now (10 s)'}
            </Button>
            {data.problem && <span className="text-sm text-muted">{data.problem}</span>}
          </div>
          {data.minutes.length > 0 && <TimeSeriesChart result={chart.result} catalog={chart.catalog} label="Driver time" className="h-56" />}
        </CardContent>
      </Card>
      {data.measurements.map((m) => (
        <MeasurementCard key={m.at} measurement={m} />
      ))}
    </div>
  );
}

function MeasurementCard({ measurement }: { measurement: MeasurementView }) {
  const slowest = measurement.drivers[0];
  return (
    <Card>
      <CardHeader>
        <CardTitle>
          Measured {formatDateTime(measurement.from)} for {measurement.seconds} s{' '}
          <span className="text-sm font-normal text-muted">({REASON[measurement.reason] ?? measurement.reason})</span>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        {measurement.problem && <p className="text-muted">{measurement.problem}</p>}
        {slowest && Math.max(slowest.latency.dpcMaxUs, slowest.latency.isrMaxUs) >= 500 && slowest.try && (
          <p>
            <span className="font-medium">{slowest.latency.driver}</span> is {slowest.what}; what could help: {slowest.try}.
          </p>
        )}
        {measurement.drivers.length > 0 && (
          <table className="w-full">
            <thead className="text-left text-xs text-muted">
              <tr>
                <th className="py-1 font-medium">Driver</th>
                <th className="py-1 text-right font-medium">Longest DPC</th>
                <th className="py-1 text-right font-medium">Longest ISR</th>
                <th className="py-1 text-right font-medium">500 µs or more</th>
                <th className="py-1 text-right font-medium">Time in all</th>
              </tr>
            </thead>
            <tbody className="tabular-nums">
              {measurement.drivers.slice(0, 15).map(({ latency: d, what }) => {
                const longest = Math.max(d.dpcMaxUs, d.isrMaxUs);
                return (
                  <tr key={d.driver} className="border-t border-border">
                    <td className="py-1">
                      {d.driver}
                      {what && <span className="ml-1 text-xs text-muted">{what}</span>}
                    </td>
                    <td className={`py-1 text-right ${d.dpcMaxUs >= 1000 ? 'text-danger' : d.dpcMaxUs >= 500 ? 'text-warning' : ''}`}>{d.dpcs > 0 ? us(d.dpcMaxUs) : '–'}</td>
                    <td className={`py-1 text-right ${d.isrMaxUs >= 1000 ? 'text-danger' : d.isrMaxUs >= 500 ? 'text-warning' : ''}`}>{d.isrs > 0 ? us(d.isrMaxUs) : '–'}</td>
                    <td className={`py-1 text-right ${d.over500Us > 0 && longest >= 500 ? 'text-warning' : 'text-muted'}`}>{d.over500Us}</td>
                    <td className="py-1 text-right text-muted">{us(d.dpcTotalUs + d.isrTotalUs)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </CardContent>
    </Card>
  );
}
