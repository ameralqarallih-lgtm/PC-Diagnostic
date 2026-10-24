import { motion, useReducedMotion } from 'motion/react';
import { useMemo } from 'react';
import { EChart } from '@/components/charts/EChart';
import { Thermometer } from '@/components/charts/Meters';
import type { SeriesKey } from '@/features/live/series';
import type { LiveMetric, LiveSnapshot } from '@/lib/api/types';
import type { ChartOption } from '@/lib/charts/echarts';
import { heatColor, levelOf as readingLevel, READING_PALETTES, TEMPERATURE_LIMITS, type ReadingKind, type ReadingLevel } from '@/lib/charts/readings';
import { SERIES_COLORS, useThemeColors, type ThemeColors } from '@/lib/charts/useThemeColors';
import { describeUnavailable, formatBitsPerSecond, formatBytes, formatBytesPerSecond, formatNumber } from '@/lib/format';
import { cn } from '@/lib/utils';

type Level = ReadingLevel;

export interface GaugeSpec {
  id: string;
  label: string;
  /** What the value is; decides the gauge's shape and colors. */
  kind: ReadingKind;
  /** Series the chart focuses when this gauge is selected. */
  series: SeriesKey;
  value: number | null;
  max: number;
  /** Text in the middle of the dial. */
  display: string;
  detail?: string;
  warning: number;
  critical: number;
  /** Why there is no value. */
  unavailable?: string | null;
}

function levelOf(spec: GaugeSpec): Level {
  return readingLevel(spec.value, spec.warning, spec.critical);
}

const percentText = (v: number | null) => (v == null ? '—' : `${Math.round(v)}%`);
const temperatureText = (v: number | null) => (v == null ? '—' : `${Math.round(v)}°C`);

function maxMetric(metrics: LiveMetric[], fallback: string): LiveMetric {
  const present = metrics.filter((m) => m.value != null);
  if (present.length === 0) return { value: null, unavailable: metrics[0]?.unavailable ?? fallback };
  return present.reduce((a, b) => ((b.value ?? 0) > (a.value ?? 0) ? b : a));
}

/** The eight dials: utilization, memory, network and temperatures. */
export function gaugeSpecs(live: LiveSnapshot | null): GaugeSpec[] {
  const cpu = live?.cpu;
  const memory = live?.memory;
  const gpus = live?.gpus ?? [];
  const busiestGpu = gpus.reduce<(typeof gpus)[number] | undefined>((a, b) => ((b.utilizationPercent.value ?? -1) > (a?.utilizationPercent.value ?? -1) ? b : a), undefined);
  const vramGpu = gpus
    .filter((g) => g.vramTotalBytes.value)
    .reduce<(typeof gpus)[number] | undefined>(
      (a, b) => ((b.vramUsedBytes.value ?? 0) / (b.vramTotalBytes.value ?? 1) > (a ? (a.vramUsedBytes.value ?? 0) / (a.vramTotalBytes.value ?? 1) : -1) ? b : a),
      undefined,
    );
  const vramPercent = vramGpu && vramGpu.vramUsedBytes.value != null && vramGpu.vramTotalBytes.value ? (100 * vramGpu.vramUsedBytes.value) / vramGpu.vramTotalBytes.value : null;
  const disk = maxMetric(live?.disks.map((d) => d.activePercent) ?? [], 'NotPresent');
  const up = live?.networks.filter((n) => n.up) ?? [];
  const rx = up.reduce((t, n) => t + (n.receiveBytesPerSec.value ?? 0), 0);
  const tx = up.reduce((t, n) => t + (n.sendBytesPerSec.value ?? 0), 0);
  const link = up.reduce((t, n) => Math.max(t, n.linkBitsPerSec.value ?? 0), 0);
  const netPercent = up.length > 0 && link > 0 ? Math.min(100, ((rx + tx) * 8 * 100) / link) : null;
  const gpuHot = maxMetric(gpus.map((g) => g.hotspotC), 'NotPresent');
  const gpuCore = maxMetric(gpus.map((g) => g.temperatureC), 'NotPresent');
  const gpuTemp = gpuHot.value != null ? gpuHot : gpuCore;

  return [
    {
      id: 'cpu',
      label: 'CPU',
      kind: 'load',
      series: 'cpu',
      value: cpu?.totalPercent.value ?? null,
      max: 100,
      display: percentText(cpu?.totalPercent.value ?? null),
      detail: cpu ? `${formatNumber(cpu.frequencyMhz.value != null ? cpu.frequencyMhz.value / 1000 : null, 'GHz', 2)} · ${cpu.logicalProcessors} threads` : undefined,
      warning: 75,
      critical: 90,
      unavailable: cpu?.totalPercent.unavailable ?? 'Pending',
    },
    {
      id: 'memory',
      label: 'Memory',
      kind: 'capacity',
      series: 'memory',
      value: memory?.usedPercent.value ?? null,
      max: 100,
      display: percentText(memory?.usedPercent.value ?? null),
      detail: memory ? `${formatBytes(memory.usedBytes.value)} / ${formatBytes(memory.totalBytes.value)}` : undefined,
      warning: 80,
      critical: 92,
      unavailable: memory?.usedPercent.unavailable ?? 'Pending',
    },
    {
      id: 'gpu',
      label: 'GPU',
      kind: 'load',
      series: 'gpu',
      value: busiestGpu?.utilizationPercent.value ?? null,
      max: 100,
      display: percentText(busiestGpu?.utilizationPercent.value ?? null),
      detail: busiestGpu?.name,
      warning: 85,
      critical: 97,
      unavailable: busiestGpu?.utilizationPercent.unavailable ?? 'NotPresent',
    },
    {
      id: 'vram',
      label: 'VRAM',
      kind: 'capacity',
      series: 'vram',
      value: vramPercent,
      max: 100,
      display: percentText(vramPercent),
      detail: vramGpu ? `${formatBytes(vramGpu.vramUsedBytes.value)} / ${formatBytes(vramGpu.vramTotalBytes.value)}` : undefined,
      warning: 85,
      critical: 95,
      unavailable: vramGpu?.vramUsedBytes.unavailable ?? 'NotPresent',
    },
    {
      id: 'disk',
      label: 'Disk',
      kind: 'load',
      series: 'disk',
      value: disk.value,
      max: 100,
      display: percentText(disk.value),
      detail: live?.disks.length
        ? `R ${formatBytesPerSecond(live.disks.reduce((t, d) => t + (d.readBytesPerSec.value ?? 0), 0))} · W ${formatBytesPerSecond(live.disks.reduce((t, d) => t + (d.writeBytesPerSec.value ?? 0), 0))}`
        : undefined,
      warning: 70,
      critical: 90,
      unavailable: disk.unavailable,
    },
    {
      id: 'network',
      label: 'Network',
      kind: 'throughput',
      series: 'netReceive',
      value: netPercent,
      max: 100,
      display: up.length > 0 ? formatBytesPerSecond(rx + tx) : '—',
      detail: up.length > 0 ? `↓ ${formatBytesPerSecond(rx)} ↑ ${formatBytesPerSecond(tx)}${link ? ` of ${formatBitsPerSecond(link)}` : ''}` : undefined,
      warning: 70,
      critical: 90,
      unavailable: up.length > 0 ? 'Unsupported' : 'NotPresent',
    },
    {
      id: 'cpuTemp',
      label: 'CPU temp',
      kind: 'temperature',
      series: 'cpuTemperature',
      value: cpu?.packageTemperatureC.value ?? null,
      max: 105,
      display: temperatureText(cpu?.packageTemperatureC.value ?? null),
      detail: cpu?.packagePowerW.value != null ? `Package · ${Math.round(cpu.packagePowerW.value)} W` : 'Package',
      warning: TEMPERATURE_LIMITS.cpu.warning,
      critical: TEMPERATURE_LIMITS.cpu.critical,
      unavailable: cpu?.packageTemperatureC.unavailable ?? 'Pending',
    },
    {
      id: 'gpuTemp',
      label: gpuHot.value != null ? 'GPU hotspot' : 'GPU temp',
      kind: 'temperature',
      series: gpuHot.value != null ? 'gpuHotspot' : 'gpuTemperature',
      value: gpuTemp.value,
      max: 110,
      display: temperatureText(gpuTemp.value),
      detail: gpuHot.value != null && gpuCore.value != null ? `Core ${Math.round(gpuCore.value)}°C` : undefined,
      warning: gpuHot.value != null ? TEMPERATURE_LIMITS.hotspot.warning : TEMPERATURE_LIMITS.gpu.warning,
      critical: gpuHot.value != null ? TEMPERATURE_LIMITS.hotspot.critical : TEMPERATURE_LIMITS.gpu.critical,
      unavailable: gpuTemp.unavailable,
    },
  ];
}

const LEVEL_COLOR = (colors: ThemeColors, level: Level) =>
  level === 'critical' ? colors.danger : level === 'warning' ? colors.warning : level === 'unavailable' ? colors.unavailable : colors.success;

/** The end color of a gauge's gradient: its kind's color, or amber/red past the limits. */
function endColor(kind: Exclude<ReadingKind, 'temperature'>, level: Level, colors: ThemeColors) {
  return level === 'normal' ? READING_PALETTES[kind].to : LEVEL_COLOR(colors, level);
}

function detail(spec: GaugeSpec, colors: ThemeColors, animate: boolean, offset: [number, string], size: number) {
  const percent = spec.display.endsWith('%') && spec.value != null;
  return {
    valueAnimation: animate && percent,
    offsetCenter: offset,
    // One size per gauge kind, never from the reading now (a longer reading, 100 % or a rate, would change it).
    fontSize: size,
    fontWeight: 600,
    color: spec.value == null && spec.display === '—' ? colors.unavailable : colors.foreground,
    formatter: percent ? (v: number) => `${Math.round(v)}%` : () => spec.display,
  };
}

const HIDDEN = {
  anchor: { show: false },
  axisTick: { show: false },
  axisLabel: { show: false },
  title: { show: false },
};

/**
 * Usage: a 250° arc cut into segments like a load meter, in cool blues. Capacity: a full ring filling up, in
 * violet. Throughput: a speedometer with a needle, teal to indigo. (Temperatures are thermometers, not ECharts.)
 */
function gaugeOption(spec: GaugeSpec, level: Level, colors: ThemeColors, animate: boolean): ChartOption {
  const kind = spec.kind === 'temperature' ? 'load' : spec.kind;
  const palette = READING_PALETTES[kind];
  const end = endColor(kind, level, colors);
  const glow = { shadowBlur: level === 'critical' ? 18 : level === 'warning' ? 8 : 0, shadowColor: end };
  const base = {
    type: 'gauge' as const,
    min: 0,
    max: spec.max,
    data: [{ value: spec.value ?? 0 }],
    ...HIDDEN,
  };
  // Drawn at the value straight away (counting up from zero on opening looked like loading); later changes glide.
  const common = { animation: animate, animationDuration: 0, animationDurationUpdate: 900, animationEasingUpdate: 'cubicOut' as const };

  if (kind === 'capacity') {
    return {
      ...common,
      series: [
        {
          ...base,
          startAngle: 90,
          endAngle: -270,
          radius: '84%',
          center: ['50%', '50%'],
          // Always shown (zero length without a value): ECharts 6 throws on every update after a hidden progress arc is switched on.
          progress: {
            show: true,
            roundCap: true,
            width: 11,
            itemStyle: { color: { type: 'linear', x: 0, y: 0, x2: 1, y2: 1, colorStops: [{ offset: 0, color: palette.from }, { offset: 1, color: end }] }, ...glow },
          },
          axisLine: { roundCap: true, lineStyle: { width: 11, color: [[1, colors.border]] } },
          pointer: { show: false },
          splitLine: { show: false },
          detail: detail(spec, colors, animate, [0, '0%'], 20),
        },
      ],
    };
  }

  if (kind === 'throughput') {
    return {
      ...common,
      series: [
        {
          ...base,
          startAngle: 200,
          endAngle: -20,
          radius: '94%',
          center: ['50%', '60%'],
          progress: {
            show: true,
            width: 6,
            itemStyle: { color: { type: 'linear', x: 0, y: 0, x2: 1, y2: 0, colorStops: [{ offset: 0, color: palette.from }, { offset: 1, color: end }] }, ...glow },
          },
          axisLine: { lineStyle: { width: 6, color: [[1, colors.border]] } },
          splitLine: { show: true, distance: 2, length: 5, lineStyle: { color: colors.muted, width: 1, opacity: 0.6 } },
          splitNumber: 5,
          pointer: { show: true, length: '64%', width: 3, offsetCenter: [0, 0], itemStyle: { color: end } },
          anchor: { show: true, size: 7, itemStyle: { color: colors.surface, borderColor: end, borderWidth: 2 } },
          detail: detail(spec, colors, animate, [0, '46%'], 15),
        },
      ],
    };
  }

  const arc = { startAngle: 215, endAngle: -35, radius: '96%', center: ['50%', '56%'] };
  return {
    ...common,
    series: [
      {
        ...base,
        ...arc,
        progress: {
          show: true,
          width: 11,
          itemStyle: { color: { type: 'linear', x: 0, y: 1, x2: 1, y2: 0, colorStops: [{ offset: 0, color: palette.from }, { offset: 1, color: end }] }, ...glow },
        },
        axisLine: { lineStyle: { width: 11, color: [[1, colors.border]] } },
        splitLine: { show: false },
        pointer: { show: false },
        detail: detail(spec, colors, animate, [0, '2%'], 21),
      },
      {
        // Drawn on top: gaps in the tile's color cut the arc into segments, like a load meter.
        ...base,
        ...arc,
        z: 5,
        silent: true,
        data: [],
        progress: { show: false },
        axisLine: { show: false },
        pointer: { show: false },
        detail: { show: false },
        splitNumber: 16,
        splitLine: { show: true, distance: -11, length: 11, lineStyle: { color: colors.surface, width: 3 } },
      },
    ],
  };
}

export interface GaugeGridProps {
  live: LiveSnapshot | null;
  /** Gauge ids to show (all when omitted). */
  include?: string[];
  /** The gauge whose series the chart focuses. */
  selected: string | null;
  onSelect: (gauge: GaugeSpec | null) => void;
}

/** Animated dials for the headline values; selecting one focuses its line in the live chart. */
export function GaugeGrid({ live, include, selected, onSelect }: GaugeGridProps) {
  const specs = useMemo(() => gaugeSpecs(live).filter((s) => !include || include.includes(s.id)), [live, include]);
  return (
    <section aria-label="Live gauges" className="grid h-full auto-rows-min grid-cols-2 gap-3 overflow-auto @md:grid-cols-4 @5xl:grid-cols-8">
      {specs.map((spec) => (
        <GaugeTile key={spec.id} spec={spec} selected={selected === spec.id} onSelect={() => onSelect(selected === spec.id ? null : spec)} />
      ))}
    </section>
  );
}

function GaugeTile({ spec, selected, onSelect }: { spec: GaugeSpec; selected: boolean; onSelect: () => void }) {
  const colors = useThemeColors();
  const reduced = useReducedMotion() ?? false;
  const level = levelOf(spec);
  const option = useMemo(() => (spec.kind === 'temperature' ? null : gaugeOption(spec, level, colors, !reduced)), [spec, level, colors, reduced]);
  const reason = spec.value == null && spec.display === '—' ? describeUnavailable(spec.unavailable) : undefined;

  return (
    <motion.button
      type="button"
      aria-pressed={selected}
      title={reason ? `${spec.label}: ${reason}` : `Show ${spec.label} in the live chart`}
      onClick={onSelect}
      // All the gauges fade in together (one after another, left to right, looked like slow loading).
      initial={reduced ? false : { opacity: 0 }}
      animate={{ opacity: 1 }}
      transition={{ duration: 0.25 }}
      whileTap={reduced ? undefined : { scale: 0.98 }}
      className={cn(
        'relative flex flex-col items-center overflow-hidden rounded-lg border bg-surface px-2 pb-3 pt-1 text-center transition-shadow',
        selected ? 'border-accent ring-2 ring-accent/40' : 'border-border hover:border-accent/50',
        level === 'critical' && 'gauge-critical',
      )}
    >
      {option ? (
        <EChart option={option} label={`${spec.label}: ${reason ? 'unavailable' : spec.display}`} className="pointer-events-none h-24 sm:h-28" />
      ) : (
        <div className="pointer-events-none flex h-24 w-full flex-col items-center justify-center gap-2 px-1 sm:h-28" role="img" aria-label={`${spec.label}: ${reason ? 'unavailable' : spec.display}`}>
          <span
            className="text-2xl font-semibold tabular-nums leading-none"
            style={{
              // The heat color reads well on the dark theme; on the light one yellows and greens would be too faint.
              color: spec.value == null ? colors.unavailable : colors.dark ? heatColor(spec.value, spec.warning, spec.critical) : colors.foreground,
            }}
          >
            {spec.display}
          </span>
          <Thermometer value={spec.value} max={spec.max} warning={spec.warning} critical={spec.critical} className="max-w-[12.5rem]" />
        </div>
      )}
      <span className={cn('flex items-center gap-1.5 text-sm font-medium', (spec.kind === 'load' || spec.kind === 'throughput') && '-mt-3')}>
        <span aria-hidden className="size-2 rounded-full" style={{ background: SERIES_COLORS[spec.series] }} title="Its line in the live chart" />
        {spec.label}
      </span>
      <span className={cn('mt-0.5 max-w-full truncate text-xs', reason ? 'text-unavailable' : 'text-muted')}>{reason ?? spec.detail ?? ' '}</span>
      <span className="sr-only">{reason ? `${spec.label} unavailable: ${reason}` : `${spec.label} ${spec.display}`}</span>
    </motion.button>
  );
}
