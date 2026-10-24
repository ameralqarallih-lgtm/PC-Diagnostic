import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { MeterBar } from '@/components/charts/Meters';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Segmented } from '@/features/dashboard/LiveChartCard';
import { apiGet } from '@/lib/api/client';
import type { RecorderCostResponse, RecorderOverhead } from '@/lib/api/types';
import { formatBytes, formatNumber } from '@/lib/format';
import { cn } from '@/lib/utils';

/** The CPU the recorder may use on average (PROJECT §20: under 1–2 %). */
export const CPU_BUDGET_PERCENT = 2;

const PERIODS = [
  { value: '60', label: 'Last hour' },
  { value: '1440', label: 'Last 24 h' },
];

/** What recording costs: the recorder's own CPU, memory and sampling time, from its self-monitoring in every sample. */
export function RecorderOverheadCard() {
  const [minutes, setMinutes] = useState('60');
  const { data } = useQuery({
    queryKey: ['recorder-overhead', minutes],
    queryFn: ({ signal }) => apiGet<RecorderOverhead>(`/recorder/overhead?minutes=${minutes}`, signal),
    refetchInterval: 60_000,
  });
  const within = data?.cpuAvgPercent != null && data.cpuAvgPercent <= CPU_BUDGET_PERCENT;

  return (
    <Card>
      <CardHeader className="flex-wrap">
        <CardTitle>Recorder overhead</CardTitle>
        <Segmented label="Overhead period" options={PERIODS} value={minutes} onChange={setMinutes} />
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {!data ? (
          <p className="text-sm text-muted">Loading…</p>
        ) : data.samples === 0 || data.cpuAvgPercent == null ? (
          <p className="text-sm text-muted">Nothing was recorded in this period; the overhead is measured while recording.</p>
        ) : (
          <>
            <div>
              <p className="flex items-baseline justify-between text-sm">
                <span>CPU (share of the whole PC)</span>
                <span className={cn('font-semibold tabular-nums', within ? 'text-success' : 'text-danger')}>
                  {formatNumber(data.cpuAvgPercent, '%', 2)} average
                </span>
              </p>
              <MeterBar kind="load" value={data.cpuAvgPercent} max={CPU_BUDGET_PERCENT * 2} warning={CPU_BUDGET_PERCENT * 0.75} critical={CPU_BUDGET_PERCENT} label="Recorder CPU against its budget" />
              <p className="mt-1 text-xs text-muted">
                95 % of samples at or below {formatNumber(data.cpuP95Percent, '%', 2)}, highest {formatNumber(data.cpuMaxPercent, '%', 2)}. Budget: {CPU_BUDGET_PERCENT} % on
                average {within ? '(met)' : '(exceeded: the recorder slows its sensor and process sampling by itself)'}.
              </p>
            </div>
            <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
              <dt className="text-muted">Memory (working set)</dt>
              <dd className="text-right tabular-nums">
                {formatBytes(data.workingSetAvgBytes)} average, {formatBytes(data.workingSetMaxBytes)} highest
              </dd>
              <dt className="text-muted">Time to take a sample</dt>
              <dd className="text-right tabular-nums">
                {formatNumber(data.tickAvgMs, 'ms', 1)} average, {formatNumber(data.tickMaxMs, 'ms', 0)} longest
              </dd>
              <dt className="text-muted">Samples</dt>
              <dd className="text-right tabular-nums">
                {data.samples.toLocaleString()}
                {data.samplesWithMissedCollectors > 0 ? ` (${data.samplesWithMissedCollectors} with a late collector)` : ''}
              </dd>
            </dl>
          </>
        )}
        <CostSplit />
      </CardContent>
    </Card>
  );
}

/** Names for the recorder's parts as the cost report counts them. */
const PART_NAMES: Record<string, string> = {
  'sensors.lhm': 'Sensors (temperatures, fans, voltages)',
  'collect.cpu': 'CPU readings',
  'collect.processes': 'Processes',
  'collect.gpu': 'GPU readings',
  'collect.memory': 'Memory readings',
  'collect.disk': 'Disk readings',
  'collect.network': 'Network readings',
  'collect.sensors': 'Sensor merging',
  'collect.services': 'Services',
  'collect.display': 'Displays',
  'collect.inventory': 'Inventory',
  'overlay.panels': 'Panels on the monitors',
  'overlay.motion': 'Moving backgrounds',
  'overlay.sources': 'Values for the panels',
  'usb.refresh': 'USB screens: panels',
  'usb.motion': 'USB screens: moving backgrounds',
  'usb.encode': 'USB screens: encoding pictures',
  'usb.characters': 'USB screens: characters',
  characters: 'Characters on the monitors',
  other: 'Everything else (storage, links, .NET)',
};

/**
 * Where the recorder's CPU went over the last minute, by part (the recorder measures each part's CPU cycles). The
 * motion of a USB screen is counted in both "Moving backgrounds" and its own line, so the moving backgrounds line is
 * left out when a USB screen's is there.
 */
function CostSplit() {
  const { data } = useQuery({ queryKey: ['recorder-cost'], queryFn: ({ signal }) => apiGet<RecorderCostResponse>('/recorder/cost', signal), refetchInterval: 30_000 });
  const cost = data?.cost;
  if (!cost) return null;
  const hasUsbMotion = cost.parts.some((p) => p.part === 'usb.motion');
  const parts = cost.parts.filter((p) => p.ms >= 1 && !(hasUsbMotion && p.part === 'overlay.motion'));
  const top = Math.max(1, ...parts.map((p) => p.ms));
  return (
    <section aria-label="Where the CPU went" className="flex flex-col gap-1.5 border-t border-border pt-3">
      <p className="flex items-baseline justify-between text-sm">
        <span className="font-medium">Where it went, last minute</span>
        <span className="tabular-nums text-muted">
          {formatNumber(cost.sharePercent, '%', 2)} of the PC · {formatNumber(cost.usedMs / 1000, 's', 1)} CPU in {Math.round(cost.seconds)} s
        </span>
      </p>
      <ul className="flex flex-col gap-1">
        {parts.map((p) => (
          <li key={p.part} className="grid grid-cols-[minmax(0,12rem)_1fr_4.5rem] items-center gap-2 text-xs">
            <span className="truncate" title={p.part}>
              {PART_NAMES[p.part] ?? p.part}
            </span>
            <span className="h-1.5 rounded-full bg-surface-muted">
              <span className="block h-full rounded-full bg-accent" style={{ width: `${Math.max(2, (p.ms / top) * 100)}%` }} />
            </span>
            <span className="text-right tabular-nums text-muted">{formatNumber(p.ms, 'ms', 0)}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}
