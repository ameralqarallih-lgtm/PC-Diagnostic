import { Wifi } from 'lucide-react';
import { useMemo } from 'react';
import { EChart } from '@/components/charts/EChart';
import { DataTable, type Column } from '@/components/data/DataTable';
import { MeterBar } from '@/components/charts/Meters';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { chartData, type LiveSeries, type SeriesKey, SERIES_KEYS } from '@/features/live/series';
import type { LiveGpu, LiveMetric, LiveNetwork, LiveSnapshot } from '@/lib/api/types';
import type { ChartOption } from '@/lib/charts/echarts';
import { TEMPERATURE_LIMITS } from '@/lib/charts/readings';
import { SERIES_COLORS, useThemeColors } from '@/lib/charts/useThemeColors';
import {
  describeUnavailable,
  formatBitsPerSecond,
  formatBytes,
  formatBytesPerSecond,
  formatNumber,
  formatPercent,
  formatTemperature,
  metricText,
  UNAVAILABLE,
} from '@/lib/format';
import { cn } from '@/lib/utils';
import { METRIC_OPTIONS } from './config/widgets';
import { MetricRow } from './MetricRow';

const percent = (v: number) => formatPercent(v);

interface CardProps {
  live: LiveSnapshot | null;
  title?: string;
  className?: string;
}

interface SensorRow {
  part: string;
  label: string;
  metric: LiveMetric | undefined;
  format?: (v: number) => string;
  limits?: { warning: number; critical: number };
}

const SENSOR_COLUMNS: Column<SensorRow>[] = [
  { id: 'part', header: 'Part', value: (r) => r.part },
  { id: 'label', header: 'Sensor', value: (r) => r.label, groupable: false },
  { id: 'value', header: 'Value', value: (r) => r.metric?.value, groupable: false, firstDesc: true },
];

const GPU_COLUMNS: Column<LiveGpu>[] = [
  { id: 'name', header: 'Adapter', value: (g) => g.name, groupable: false },
  { id: 'util', header: 'Utilization', value: (g) => g.utilizationPercent.value, groupable: false, firstDesc: true },
  { id: 'vram', header: 'VRAM in use', value: (g) => g.vramUsedBytes.value, groupable: false, firstDesc: true },
  { id: 'power', header: 'Power', value: (g) => g.powerW.value, groupable: false, firstDesc: true },
  { id: 'clock', header: 'Core clock', value: (g) => g.coreClockMhz.value, groupable: false, firstDesc: true },
];

const DISPLAY_COLUMNS: Column<string>[] = [{ id: 'display', header: 'Display', value: (d) => d, groupable: false }];

const NETWORK_COLUMNS: Column<LiveNetwork>[] = [
  { id: 'name', header: 'Adapter', value: (n) => n.name, groupable: false },
  { id: 'kind', header: 'Kind', value: (n) => n.kind },
  { id: 'state', header: 'State', value: (n) => (n.up ? 'Connected' : 'Disconnected') },
  { id: 'link', header: 'Link speed', value: (n) => n.linkBitsPerSec.value, groupable: false, firstDesc: true },
  { id: 'rx', header: 'Download', value: (n) => n.receiveBytesPerSec.value, groupable: false, firstDesc: true },
  { id: 'tx', header: 'Upload', value: (n) => n.sendBytesPerSec.value, groupable: false, firstDesc: true },
  { id: 'wifi', header: 'Wi-Fi signal', value: (n) => n.wifiSignalPercent.value, groupable: false, firstDesc: true },
];

/** CPU, GPU (core and hotspot) and drive temperatures, and CPU power; each group can be left out. */
export function TemperaturesCard({ live, include = ['cpu', 'gpu', 'disk', 'power'], title, className }: CardProps & { include?: string[] }) {
  const rows: SensorRow[] = [];
  if (include.includes('cpu')) rows.push({ part: 'Processor', label: 'CPU package', metric: live?.cpu?.packageTemperatureC, limits: TEMPERATURE_LIMITS.cpu });
  if (include.includes('gpu')) {
    for (const gpu of live?.gpus ?? []) {
      rows.push({ part: 'Graphics', label: gpu.name, metric: gpu.temperatureC, limits: TEMPERATURE_LIMITS.gpu });
      rows.push({ part: 'Graphics', label: `${gpu.name} hotspot`, metric: gpu.hotspotC, limits: TEMPERATURE_LIMITS.hotspot });
    }
  }
  if (include.includes('disk')) {
    for (const disk of live?.disks ?? []) {
      rows.push({ part: 'Drives', label: disk.volumes ? `${disk.name} (${disk.volumes})` : disk.name, metric: disk.temperatureC, limits: TEMPERATURE_LIMITS.disk });
    }
  }
  if (include.includes('power')) rows.push({ part: 'Power', label: 'CPU package power', metric: live?.cpu?.packagePowerW, format: (v) => formatNumber(v, 'W', 1) });

  return (
    <Card className={className}>
      <CardHeader>
        <CardTitle>{title ?? 'Sensors'}</CardTitle>
      </CardHeader>
      <CardContent>
        {!live ? (
          <p className="text-sm text-unavailable">{UNAVAILABLE}</p>
        ) : rows.length === 0 ? (
          <p className="text-sm text-muted">No sensors selected.</p>
        ) : (
          <DataTable
            viewKey="widget.sensors"
            label={title ?? 'Sensors'}
            rows={rows}
            columns={SENSOR_COLUMNS}
            rowKey={(row) => row.label}
            defaultView={{ groupBy: [], sort: [] }}
            compact
            listClassName="gap-2"
            renderRow={(row) => (
              <dl>
                <MetricRow
                  label={row.label}
                  value={metricText(row.metric, row.format ?? formatTemperature)}
                  meter={
                    row.limits && row.metric?.value != null ? (
                      <MeterBar kind="temperature" value={row.metric.value} max={110} {...row.limits} label={`${row.label} temperature`} />
                    ) : undefined
                  }
                />
              </dl>
            )}
          />
        )}
      </CardContent>
    </Card>
  );
}

/** Per-adapter GPU details (all adapters, or only the busiest). */
export function GpuCard({ live, adapters = 'all', title, className }: CardProps & { adapters?: string }) {
  const all = live?.gpus ?? [];
  const gpus =
    adapters === 'busiest' && all.length > 1
      ? [all.reduce((a, b) => ((b.utilizationPercent.value ?? -1) > (a.utilizationPercent.value ?? -1) ? b : a))]
      : all;
  return (
    <Card className={className}>
      <CardHeader>
        <CardTitle>{title ?? 'Graphics'}</CardTitle>
      </CardHeader>
      <CardContent>
        {gpus.length === 0 ? (
          <p className="text-sm text-muted">{live ? 'No GPU data yet.' : UNAVAILABLE}</p>
        ) : (
          <DataTable
            viewKey="widget.gpus"
            label={title ?? 'Graphics'}
            rows={gpus}
            columns={GPU_COLUMNS}
            rowKey={(gpu) => gpu.key}
            defaultView={{ groupBy: [], sort: [] }}
            compact
            listClassName="gap-4"
            renderRow={(gpu) => (
            <div className="flex flex-col gap-2">
              <h3 className="truncate text-sm font-medium" title={gpu.name}>
                {gpu.name}
              </h3>
              <dl className="flex flex-col gap-1.5">
                <MetricRow
                  label="Utilization"
                  value={metricText(gpu.utilizationPercent, percent)}
                  meter={
                    gpu.utilizationPercent.value != null ? (
                      <MeterBar kind="load" value={gpu.utilizationPercent.value} max={100} warning={85} critical={97} label={`${gpu.name} utilization`} />
                    ) : undefined
                  }
                />
                <MetricRow
                  label="VRAM"
                  value={metricText(gpu.vramUsedBytes, (v) => formatBytes(v))}
                  detail={gpu.vramTotalBytes.value ? `of ${formatBytes(gpu.vramTotalBytes.value)}` : undefined}
                  meter={
                    gpu.vramUsedBytes.value != null && gpu.vramTotalBytes.value ? (
                      <MeterBar
                        kind="capacity"
                        value={(100 * gpu.vramUsedBytes.value) / gpu.vramTotalBytes.value}
                        max={100}
                        warning={85}
                        critical={95}
                        label={`${gpu.name} VRAM in use`}
                      />
                    ) : undefined
                  }
                />
                <MetricRow label="Power" value={metricText(gpu.powerW, (v) => formatNumber(v, 'W', 0))} />
                <MetricRow label="Core clock" value={metricText(gpu.coreClockMhz, (v) => formatNumber(v, 'MHz'))} />
              </dl>
            </div>
            )}
          />
        )}
      </CardContent>
    </Card>
  );
}

/** Connected displays and their modes. */
export function DisplaysCard({ live, title, className }: CardProps) {
  const display = live?.display;
  return (
    <Card className={className}>
      <CardHeader>
        <CardTitle>{title ?? 'Displays'}</CardTitle>
      </CardHeader>
      <CardContent>
        {!display ? (
          <p className="text-sm text-muted">Display details appear after the first display check.</p>
        ) : (
          <>
            <DataTable
              viewKey="widget.displays"
              label={title ?? 'Displays'}
              rows={display.displays}
              columns={DISPLAY_COLUMNS}
              rowKey={(d) => d}
              defaultView={{ groupBy: [], sort: [] }}
              compact
              emptyText="No active display"
              listClassName="gap-1.5"
              renderRow={(d) => (
                <p className="truncate text-sm" title={d}>
                  {d}
                </p>
              )}
            />
            {display.remoteSession && <p className="mt-1.5 text-xs text-warning">Remote Desktop session: these are the remote displays, not the physical monitors.</p>}
          </>
        )}
      </CardContent>
    </Card>
  );
}

const VIRTUAL_KINDS = new Set(['Virtual', 'Vpn']);

/** Throughput, link speed and Wi-Fi signal per adapter; virtual and disconnected adapters can be included. */
export function NetworkCard({ live, virtual = false, disconnected = false, title, className }: CardProps & { virtual?: boolean; disconnected?: boolean }) {
  const adapters = (live?.networks ?? []).filter((n) => (virtual || !VIRTUAL_KINDS.has(n.kind)) && (disconnected || n.up));
  return (
    <Card className={className}>
      <CardHeader>
        <CardTitle>{title ?? 'Network adapters'}</CardTitle>
      </CardHeader>
      <CardContent>
        {!live ? (
          <p className="text-sm text-unavailable">{UNAVAILABLE}</p>
        ) : adapters.length === 0 ? (
          <p className="text-sm text-muted">No adapter matches the widget settings.</p>
        ) : (
          <DataTable
            viewKey="widget.network"
            label={title ?? 'Network adapters'}
            rows={adapters}
            columns={NETWORK_COLUMNS}
            rowKey={(n) => n.name}
            defaultView={{ groupBy: [], sort: [] }}
            compact
            listClassName="gap-3"
            renderRow={(n) => (
              <div className="flex flex-col gap-0.5">
                <div className="flex items-center justify-between gap-2">
                  <span className={cn('truncate text-sm font-medium', !n.up && 'text-muted')} title={n.name}>
                    {n.name}
                  </span>
                  <span className="shrink-0 text-xs text-muted">
                    {n.up ? formatBitsPerSecond(n.linkBitsPerSec.value) : 'Disconnected'} · {n.kind}
                  </span>
                </div>
                <div className="flex items-center justify-between text-xs tabular-nums text-muted">
                  <span>
                    ↓ {formatBytesPerSecond(n.receiveBytesPerSec.value)} ↑ {formatBytesPerSecond(n.sendBytesPerSec.value)}
                  </span>
                  {n.wifiSignalPercent.value != null && (
                    <span className="flex items-center gap-1" title="Wi-Fi signal quality">
                      <Wifi className="size-3" aria-hidden /> {formatPercent(n.wifiSignalPercent.value)}
                    </span>
                  )}
                </div>
              </div>
            )}
          />
        )}
      </CardContent>
    </Card>
  );
}

function metricValue(live: LiveSnapshot | null, metric: string, latestSeries: (key: SeriesKey) => number | null): { text: string; reason?: string } {
  const unavailable = (m: LiveMetric | undefined) => ({ text: UNAVAILABLE, reason: describeUnavailable(m?.unavailable) });
  switch (metric) {
    case 'cpuFrequency':
      return live?.cpu?.frequencyMhz.value != null ? { text: formatNumber(live.cpu.frequencyMhz.value / 1000, 'GHz', 2) } : unavailable(live?.cpu?.frequencyMhz);
    case 'commit':
      return live?.memory?.commitPercent.value != null ? { text: formatPercent(live.memory.commitPercent.value) } : unavailable(live?.memory?.commitPercent);
    case 'hardFaults':
      return live?.memory?.hardFaultsPerSec.value != null ? { text: formatNumber(live.memory.hardFaultsPerSec.value, '/s') } : unavailable(live?.memory?.hardFaultsPerSec);
    case 'processCount':
      return live?.processes ? { text: String(live.processes.count) } : { text: UNAVAILABLE, reason: 'Waiting for the process list' };
    case 'agentCpu':
      return live?.agent.cpuPercent.value != null ? { text: formatPercent(live.agent.cpuPercent.value, 2) } : unavailable(live?.agent.cpuPercent);
    default: {
      const key = metric as SeriesKey;
      const value = latestSeries(key);
      if (value == null) return { text: UNAVAILABLE, reason: 'No value right now' };
      if (key === 'netReceive' || key === 'netSend') return { text: formatBytesPerSecond(value) };
      if (key.endsWith('emperature') || key === 'gpuHotspot') return { text: formatTemperature(value) };
      return { text: formatPercent(value) };
    }
  }
}

/** One value, large, with a small trend line when it is one of the charted values. */
export function MetricWidget({ live, series, metric, title, className }: CardProps & { series: LiveSeries; metric: string }) {
  const colors = useThemeColors();
  const charted = (SERIES_KEYS as readonly string[]).includes(metric) ? (metric as SeriesKey) : null;
  const label = title ?? METRIC_OPTIONS.find((o) => o.value === metric)?.label ?? metric;
  const value = metricValue(live, metric, (key) => {
    const values = series.values[key];
    return values.length > 0 ? (values[values.length - 1] ?? null) : null;
  });

  const option = useMemo<ChartOption | null>(() => {
    if (!charted) return null;
    const color = SERIES_COLORS[charted];
    return {
      animation: false,
      grid: { left: 0, right: 0, top: 4, bottom: 0 },
      xAxis: { type: 'time', show: false },
      yAxis: { type: 'value', show: false, scale: true },
      series: [
        {
          type: 'line',
          data: chartData(series, charted).slice(-180),
          showSymbol: false,
          smooth: 0.4,
          lineStyle: { width: 1.5, color },
          areaStyle: { color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1, colorStops: [{ offset: 0, color: `${color}55` }, { offset: 1, color: `${color}00` }] } },
        },
      ],
    };
  }, [charted, series]);

  return (
    <Card className={cn('flex flex-col', className)}>
      <CardHeader>
        <CardTitle className="truncate">{label}</CardTitle>
      </CardHeader>
      <CardContent className="flex min-h-0 flex-1 flex-col">
        <p className={cn('text-3xl font-semibold tabular-nums', value.reason && 'text-unavailable text-xl')} title={value.reason}>
          {value.text}
        </p>
        {option && colors && <EChart option={option} label={`${label} over the last three minutes`} className="min-h-8 flex-1" />}
      </CardContent>
    </Card>
  );
}
