import { useReducedMotion } from 'motion/react';
import { ArrowDownRight, ArrowRight, ArrowUpRight } from 'lucide-react';
import { useMemo, type CSSProperties, type ReactNode } from 'react';
import { EChart } from '@/components/charts/EChart';
import { Thermometer } from '@/components/charts/Meters';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import type { LiveProcess } from '@/lib/api/types';
import type { ChartOption } from '@/lib/charts/echarts';
import { useThemeColors, type ThemeColors } from '@/lib/charts/useThemeColors';
import { describeUnavailable, formatBitsPerSecond, formatBytes, formatBytesPerSecond, formatUnit } from '@/lib/format';
import { cn } from '@/lib/utils';
import { useDashboardData } from '../DashboardContext';
import { RecentEventsCard } from '../RecentEventsCard';
import { AGGREGATES, DESIGN_INFO, levelFor, PROCESS_MEASURES, sourceColor, WINDOWS, type CustomConfig } from './model';
import { useCustomData, useSources, type SourceData } from './useCustomData';

/** A value in its unit, with the widget's decimals when it sets them. */
export function formatValue(value: number | null | undefined, unit: string, decimals: number | null): string {
  if (value == null || !Number.isFinite(value)) return '—';
  if (unit === 'bit/s') return formatBitsPerSecond(value);
  if (decimals == null) return formatUnit(value, unit);
  if (unit === 'B') return formatBytes(value, decimals);
  if (unit === 'B/s') return `${formatBytes(value, decimals)}/s`;
  const n = value.toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
  return unit === '%' || unit === '°C' ? `${n}${unit}` : unit ? `${n} ${unit}` : n;
}

/** The value a design shows for a source: now, or the average, lowest or highest over the window. */
function shown(data: SourceData, config: CustomConfig): number | null {
  switch (config.aggregate) {
    case 'avg':
      return data.avg;
    case 'min':
      return data.min;
    case 'max':
      return data.max;
    default:
      return data.latest;
  }
}

function labelOf(data: SourceData, config: CustomConfig, index: number): string {
  return config.labels[index] || data.source.label;
}

/** The scale of a source: the widget's own, else 0–100 for percentages, else from the values seen. */
function scaleOf(data: SourceData, config: CustomConfig): { min: number; max: number } {
  const unit = data.source.unit;
  const min = config.min ?? 0;
  if (config.max != null) return { min, max: Math.max(min + 1e-9, config.max) };
  if (unit === '%') return { min, max: 100 };
  if (unit === '°C') return { min, max: Math.max(100, (config.critical ?? 0) + 10) };
  const top = Math.max(data.max ?? 0, data.latest ?? 0, config.critical ?? 0, config.warning ?? 0);
  return { min, max: top > 0 ? niceCeiling(top * 1.15) : 1 };
}

function niceCeiling(value: number): number {
  const power = 10 ** Math.floor(Math.log10(value));
  const step = [1, 2, 2.5, 5, 10].find((s) => s * power >= value) ?? 10;
  return step * power;
}

function levelColor(level: ReturnType<typeof levelFor>, normal: string, colors: ThemeColors): string {
  return level === 'critical' ? colors.danger : level === 'warning' ? colors.warning : level === 'unavailable' ? colors.unavailable : normal;
}

const SIZE_TEXT = { s: 'text-2xl', m: 'text-4xl', l: 'text-6xl' } as const;

export interface CustomWidgetViewProps {
  config: CustomConfig;
  title?: string | null;
  /** Remembers a table's grouping and sorting. */
  viewKey: string;
  className?: string;
}

/** A widget the user built, drawn from its design and value sources with the dashboard's live data. */
export function CustomWidgetView({ config, title, viewKey, className }: CustomWidgetViewProps) {
  const info = DESIGN_INFO[config.design];
  if (config.design === 'events') return <EventFeed config={config} title={title} className={className} />;
  const caption =
    info.maxSources === 0
      ? undefined
      : info.overTime
        ? `Last ${WINDOWS.find((w) => w.value === String(config.window))?.label ?? `${config.window} minutes`}`
        : config.aggregate === 'latest'
          ? undefined
          : `${AGGREGATES.find((a) => a.value === config.aggregate)?.label}`.replace('the window', `${config.window} min`);

  return (
    <Card className={cn('flex h-full flex-col overflow-hidden', className)}>
      {(title || config.design !== 'note') && (
        <CardHeader className="flex-wrap gap-y-0.5">
          <CardTitle className="truncate">{title || info.name}</CardTitle>
          {caption && <span className="text-xs text-muted">{caption}</span>}
        </CardHeader>
      )}
      <CardContent className="flex min-h-0 flex-1 flex-col">
        {config.design === 'note' ? (
          <NoteBody config={config} />
        ) : config.design === 'processes' ? (
          <ProcessBody config={config} viewKey={viewKey} />
        ) : config.sources.length === 0 ? (
          <p className="text-sm text-muted">No values chosen yet. Open the widget's settings to choose what it shows.</p>
        ) : (
          <SourcesBody config={config} viewKey={viewKey} />
        )}
      </CardContent>
    </Card>
  );
}

function SourcesBody({ config, viewKey }: { config: CustomConfig; viewKey: string }) {
  const dashboard = useDashboardData();
  const { byId } = useSources(dashboard.live);
  const now = dashboard.live?.timestamp ? Date.parse(dashboard.live.timestamp) : Date.now();
  const ids = useMemo(() => config.sources.slice(0, DESIGN_INFO[config.design].maxSources), [config.sources, config.design]);
  const data = useCustomData(ids, byId, config.window, now, dashboard.series, dashboard.live?.recording ?? false);

  switch (config.design) {
    case 'value':
      return <ValueBody data={data} config={config} />;
    case 'gauge':
      return <GaugeBody data={data} config={config} />;
    case 'bars':
      return <BarsBody data={data} config={config} />;
    case 'line':
    case 'area':
      return <TimeChart data={data} config={config} now={now} />;
    case 'columns':
      return <ColumnChart data={data} config={config} />;
    case 'table':
      return <TableBody data={data} config={config} viewKey={viewKey} />;
    case 'tiles':
      return <TilesBody data={data} config={config} />;
    default:
      return null;
  }
}

/** A tiny trend line (no axes). */
export function Sparkline({ points, color, className }: { points: [number, number | null][]; color: string; className?: string }) {
  const values = points.filter((p): p is [number, number] => p[1] != null && Number.isFinite(p[1]));
  if (values.length < 2) return <div className={cn('h-8', className)} aria-hidden />;
  const t0 = values[0]![0];
  const t1 = values[values.length - 1]![0];
  const lo = Math.min(...values.map((p) => p[1]));
  const hi = Math.max(...values.map((p) => p[1]));
  const span = hi - lo || 1;
  const path = values.map((p) => `${(((p[0] - t0) / (t1 - t0 || 1)) * 100).toFixed(2)},${(28 - ((p[1] - lo) / span) * 26).toFixed(2)}`).join(' ');
  return (
    <svg viewBox="0 0 100 30" preserveAspectRatio="none" className={cn('h-8 w-full', className)} aria-hidden>
      <polyline points={`0,30 ${path} 100,30`} fill={color} opacity="0.12" stroke="none" />
      <polyline points={path} fill="none" stroke={color} strokeWidth="1.6" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
    </svg>
  );
}

function Trend({ data }: { data: SourceData }) {
  const values = data.points.map((p) => p[1]).filter((v): v is number => v != null);
  if (values.length < 6 || data.latest == null) return null;
  const earlier = values.slice(0, Math.floor(values.length / 2));
  const before = earlier.reduce((a, b) => a + b, 0) / earlier.length;
  const change = data.latest - before;
  const significant = Math.abs(change) > Math.max(Math.abs(before) * 0.05, 1e-9);
  const Icon = !significant ? ArrowRight : change > 0 ? ArrowUpRight : ArrowDownRight;
  return <Icon className="size-4 text-muted" aria-label={!significant ? 'Steady' : change > 0 ? 'Rising' : 'Falling'} />;
}

function Missing({ data }: { data: SourceData }) {
  return <span className="text-xs text-unavailable">{describeUnavailable(data.missing)}</span>;
}

function ValueBody({ data, config }: { data: SourceData[]; config: CustomConfig }) {
  const colors = useThemeColors();
  return (
    <div className={cn('grid h-full min-h-0 gap-3', data.length > 1 && 'grid-cols-2 @lg:grid-cols-4')} style={{ gridTemplateColumns: data.length > 1 ? undefined : '1fr' }}>
      {data.map((d, i) => {
        const value = shown(d, config);
        const level = levelFor(value, config);
        const color = sourceColor(config, i);
        return (
          <div key={d.source.id} className="flex min-w-0 flex-col justify-between gap-1">
            <p className="flex items-center gap-1.5 truncate text-xs text-muted">
              <span className="size-2 shrink-0 rounded-full" style={{ background: color }} aria-hidden />
              <span className="truncate" title={d.source.label}>
                {labelOf(d, config, i)}
              </span>
            </p>
            <p className="flex items-baseline gap-2">
              <span
                className={cn('font-semibold tabular-nums leading-none', SIZE_TEXT[config.size])}
                style={{ color: level === 'normal' ? undefined : levelColor(level, color, colors) }}
              >
                {formatValue(value, d.source.unit, config.decimals)}
              </span>
              <Trend data={d} />
            </p>
            {value == null && <Missing data={d} />}
            {config.spark && <Sparkline points={d.points} color={color} />}
            {config.legend && value != null && (
              <p className="truncate text-[11px] text-muted tabular-nums">
                low {formatValue(d.min, d.source.unit, config.decimals)} · avg {formatValue(d.avg, d.source.unit, config.decimals)} · high {formatValue(d.max, d.source.unit, config.decimals)}
              </p>
            )}
          </div>
        );
      })}
    </div>
  );
}

function GaugeBody({ data, config }: { data: SourceData[]; config: CustomConfig }) {
  return (
    <div className="grid h-full min-h-0 auto-rows-min grid-cols-2 gap-3 overflow-auto @md:grid-cols-3 @3xl:grid-cols-4">
      {data.map((d, i) => (
        <Dial key={d.source.id} data={d} config={config} index={i} />
      ))}
    </div>
  );
}

function Dial({ data, config, index }: { data: SourceData; config: CustomConfig; index: number }) {
  const colors = useThemeColors();
  const reduced = useReducedMotion() ?? false;
  const value = shown(data, config);
  const level = levelFor(value, config);
  const color = sourceColor(config, index);
  const end = levelColor(level, color, colors);
  const { min, max } = scaleOf(data, config);
  const text = formatValue(value, data.source.unit, config.decimals);
  // The number's size comes from the widest it can be on this scale (its full scale or the lowest), not from the
  // reading now: 99 % to 100 % would otherwise change its size.
  const widest = Math.max(formatValue(max, data.source.unit, config.decimals).length, formatValue(min, data.source.unit, config.decimals).length, text.length > 7 ? 8 : 0);
  const option = useMemo<ChartOption | null>(() => {
    if (config.dial === 'thermo') return null;
    const shape =
      config.dial === 'ring'
        ? { startAngle: 90, endAngle: -270, radius: '84%', center: ['50%', '50%'], width: 10, pointer: false, offset: '0%' }
        : config.dial === 'speed'
          ? { startAngle: 200, endAngle: -20, radius: '94%', center: ['50%', '60%'], width: 6, pointer: true, offset: '46%' }
          : { startAngle: 215, endAngle: -35, radius: '96%', center: ['50%', '56%'], width: 10, pointer: false, offset: '2%' };
    return {
      animation: !reduced,
      animationDuration: 0,
      animationDurationUpdate: 800,
      series: [
        {
          type: 'gauge',
          min,
          max,
          startAngle: shape.startAngle,
          endAngle: shape.endAngle,
          radius: shape.radius,
          center: shape.center,
          data: [{ value: value ?? min }],
          progress: {
            show: true,
            roundCap: config.dial === 'ring',
            width: shape.width,
            itemStyle: { color: { type: 'linear', x: 0, y: 1, x2: 1, y2: 0, colorStops: [{ offset: 0, color }, { offset: 1, color: end }] }, shadowBlur: level === 'critical' ? 14 : 0, shadowColor: end },
          },
          axisLine: { roundCap: config.dial === 'ring', lineStyle: { width: shape.width, color: [[1, colors.border]] } },
          axisTick: { show: false },
          axisLabel: { show: false },
          splitLine: config.dial === 'speed' ? { show: true, distance: 2, length: 5, lineStyle: { color: colors.muted, width: 1, opacity: 0.6 } } : { show: false },
          splitNumber: 5,
          pointer: shape.pointer ? { show: true, length: '64%', width: 3, offsetCenter: [0, 0], itemStyle: { color: end } } : { show: false },
          anchor: shape.pointer ? { show: true, size: 7, itemStyle: { color: colors.surface, borderColor: end, borderWidth: 2 } } : { show: false },
          title: { show: false },
          detail: { offsetCenter: [0, shape.offset], fontSize: widest > 7 ? 14 : 18, fontWeight: 600, color: value == null ? colors.unavailable : colors.foreground, formatter: () => text },
        },
      ],
    } as ChartOption;
  }, [config.dial, min, max, value, color, end, level, colors, reduced, text, widest]);

  return (
    <div className="flex min-w-0 flex-col items-center rounded-lg border border-border px-2 pb-2 pt-1 text-center">
      {option ? (
        <EChart option={option} label={`${labelOf(data, config, index)}: ${text}`} className="pointer-events-none h-24" />
      ) : (
        <div className="flex h-24 w-full flex-col items-center justify-center gap-2" role="img" aria-label={`${labelOf(data, config, index)}: ${text}`}>
          <span className="text-2xl font-semibold tabular-nums" style={{ color: level === 'normal' ? undefined : end }}>
            {text}
          </span>
          <Thermometer value={value} max={max} warning={config.warning ?? max * 0.8} critical={config.critical ?? max * 0.92} className="max-w-[12rem]" />
        </div>
      )}
      <span className={cn('flex max-w-full items-center gap-1.5 truncate text-xs font-medium', config.dial !== 'thermo' && config.dial !== 'ring' && '-mt-3')}>
        <span className="size-2 shrink-0 rounded-full" style={{ background: color }} aria-hidden />
        <span className="truncate" title={data.source.label}>
          {labelOf(data, config, index)}
        </span>
      </span>
      {value == null && <Missing data={data} />}
    </div>
  );
}

function BarsBody({ data, config }: { data: SourceData[]; config: CustomConfig }) {
  const colors = useThemeColors();
  return (
    <ul className="flex min-h-0 flex-col gap-2.5 overflow-auto pr-1">
      {data.map((d, i) => {
        const value = shown(d, config);
        const level = levelFor(value, config);
        const color = sourceColor(config, i);
        const { min, max } = scaleOf(d, config);
        const at = (v: number) => `${Math.min(100, Math.max(0, ((v - min) / (max - min)) * 100))}%`;
        return (
          <li key={d.source.id} className="flex flex-col gap-1">
            <p className="flex items-baseline justify-between gap-2 text-sm">
              <span className="truncate" title={d.source.label}>
                {labelOf(d, config, i)}
              </span>
              <span className="shrink-0 font-medium tabular-nums" style={{ color: level === 'normal' ? undefined : levelColor(level, color, colors) }}>
                {value == null ? <Missing data={d} /> : formatValue(value, d.source.unit, config.decimals)}
              </span>
            </p>
            <div
              role="meter"
              aria-label={labelOf(d, config, i)}
              aria-valuemin={min}
              aria-valuemax={max}
              aria-valuenow={value ?? undefined}
              className={cn('relative w-full overflow-hidden rounded-full bg-border/70', config.size === 'l' ? 'h-3.5' : config.size === 's' ? 'h-1.5' : 'h-2.5')}
            >
              <div
                className="absolute inset-y-0 left-0 rounded-full transition-[width] duration-700 ease-out motion-reduce:transition-none"
                style={{ width: value == null ? 0 : at(value), background: `linear-gradient(90deg, ${color}, ${levelColor(level, color, colors)})` }}
              />
              {config.warning != null && <span className="absolute inset-y-0 w-0.5 bg-warning/80" style={{ left: at(config.warning) }} aria-hidden />}
              {config.critical != null && <span className="absolute inset-y-0 w-0.5 bg-danger/80" style={{ left: at(config.critical) }} aria-hidden />}
            </div>
          </li>
        );
      })}
    </ul>
  );
}

function TilesBody({ data, config }: { data: SourceData[]; config: CustomConfig }) {
  const tone = { normal: 'border-success/40 bg-success/10', warning: 'border-warning/50 bg-warning/15', critical: 'border-danger/60 bg-danger/15 gauge-critical', unavailable: 'border-border bg-surface-muted' } as const;
  return (
    <div className="grid min-h-0 auto-rows-min gap-2 overflow-auto" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(7.5rem, 1fr))' }}>
      {data.map((d, i) => {
        const value = shown(d, config);
        const level = levelFor(value, config);
        return (
          <div key={d.source.id} className={cn('flex min-w-0 flex-col gap-1 rounded-lg border p-2.5', tone[level])} title={d.source.label}>
            <span className="truncate text-xs text-muted">{labelOf(d, config, i)}</span>
            <span className={cn('font-semibold tabular-nums leading-tight', config.size === 'l' ? 'text-3xl' : config.size === 's' ? 'text-lg' : 'text-2xl')}>
              {formatValue(value, d.source.unit, config.decimals)}
            </span>
            <span className="truncate text-[11px] text-muted">
              {level === 'unavailable' ? describeUnavailable(d.missing) : level === 'critical' ? 'Critical' : level === 'warning' ? 'Warning' : 'OK'}
            </span>
          </div>
        );
      })}
    </div>
  );
}

function axisFormatter(unit: string, decimals: number | null) {
  return (v: number) => formatValue(v, unit, unit === '%' || unit === '°C' ? (decimals ?? 0) : decimals);
}

function TimeChart({ data, config, now }: { data: SourceData[]; config: CustomConfig; now: number }) {
  const colors = useThemeColors();
  const reduced = useReducedMotion() ?? false;
  const units = [...new Set(data.map((d) => d.source.unit))].slice(0, 2);
  const option = useMemo<ChartOption>(() => {
    const area = config.design === 'area';
    const from = now - config.window * 60_000;
    const limits = [
      ...(config.warning != null ? [{ yAxis: config.warning, name: 'Warning', lineStyle: { color: colors.warning } }] : []),
      ...(config.critical != null ? [{ yAxis: config.critical, name: 'Critical', lineStyle: { color: colors.danger } }] : []),
    ];
    return {
      animation: !reduced,
      animationDuration: 0,
      animationDurationUpdate: reduced ? 0 : 400,
      grid: { left: 8, right: 12, top: config.legend ? 30 : 10, bottom: 8, containLabel: true },
      legend: config.legend
        ? { top: 0, left: 0, type: 'scroll', icon: 'roundRect', itemWidth: 12, itemHeight: 4, textStyle: { color: colors.muted, fontSize: 11 }, inactiveColor: colors.border, pageTextStyle: { color: colors.muted } }
        : { show: false },
      tooltip: {
        trigger: 'axis',
        backgroundColor: colors.surface,
        borderColor: colors.border,
        textStyle: { color: colors.foreground, fontSize: 12 },
        axisPointer: { type: 'line', lineStyle: { color: colors.muted, type: 'dashed' } },
      },
      xAxis: { type: 'time', min: from, max: now, axisLine: { lineStyle: { color: colors.border } }, axisLabel: { color: colors.muted, fontSize: 10, hideOverlap: true }, splitLine: { show: false } },
      yAxis: units.map((unit, i) => ({
        type: 'value' as const,
        position: i === 0 ? ('left' as const) : ('right' as const),
        min: config.min ?? (unit === '%' ? 0 : undefined),
        max: config.max ?? (unit === '%' && !config.stacked ? 100 : undefined),
        scale: config.min == null && unit !== '%',
        splitNumber: 4,
        axisLabel: { color: colors.muted, fontSize: 10, formatter: axisFormatter(unit, config.decimals) },
        splitLine: { show: i === 0, lineStyle: { color: colors.border, opacity: 0.6 } },
      })),
      series: data.map((d, i) => {
        const color = sourceColor(config, i);
        const axis = Math.max(0, units.indexOf(d.source.unit));
        return {
          id: d.source.id,
          name: labelOf(d, config, i),
          type: 'line' as const,
          yAxisIndex: axis,
          data: d.points,
          smooth: config.smooth ? 0.35 : false,
          showSymbol: false,
          connectNulls: false,
          sampling: 'lttb' as const,
          stack: area && config.stacked ? `stack-${axis}` : undefined,
          lineStyle: { width: 1.8, color },
          itemStyle: { color },
          areaStyle: area
            ? {
                opacity: config.stacked ? 0.55 : 1,
                color: config.stacked
                  ? color
                  : { type: 'linear' as const, x: 0, y: 0, x2: 0, y2: 1, colorStops: [{ offset: 0, color: `${color}66` }, { offset: 1, color: `${color}00` }] },
              }
            : undefined,
          emphasis: { disabled: true },
          tooltip: { valueFormatter: (v: unknown) => (typeof v === 'number' ? formatValue(v, d.source.unit, config.decimals) : 'Unavailable') },
          markLine: i === 0 && limits.length ? { symbol: ['none', 'none'], silent: true, animation: false, label: { show: false }, lineStyle: { type: 'dashed' as const, width: 1 }, data: limits } : undefined,
        };
      }),
    } as ChartOption;
  }, [data, config, now, colors, reduced, units]);

  const missing = data.filter((d) => d.points.length === 0);
  return (
    <>
      <EChart option={option} replaceMerge={['series', 'yAxis']} label={`${data.map((d, i) => labelOf(d, config, i)).join(', ')} over the last ${config.window} minutes`} className="min-h-28 flex-1" />
      {missing.length > 0 && (
        <p className="mt-1 truncate text-xs text-muted" title={missing.map((d) => `${d.source.label}: ${describeUnavailable(d.missing)}`).join('\n')}>
          No values yet for {missing.map((d, i) => labelOf(d, config, data.indexOf(d) >= 0 ? data.indexOf(d) : i)).join(', ')}: {describeUnavailable(missing[0]!.missing)}
        </p>
      )}
    </>
  );
}

function ColumnChart({ data, config }: { data: SourceData[]; config: CustomConfig }) {
  const colors = useThemeColors();
  const reduced = useReducedMotion() ?? false;
  const unit = data[0]?.source.unit ?? '';
  const option = useMemo<ChartOption>(() => {
    const limits = [
      ...(config.warning != null ? [{ yAxis: config.warning, lineStyle: { color: colors.warning } }] : []),
      ...(config.critical != null ? [{ yAxis: config.critical, lineStyle: { color: colors.danger } }] : []),
    ];
    return {
      animation: !reduced,
      animationDuration: 0,
      animationDurationUpdate: reduced ? 0 : 600,
      grid: { left: 8, right: 8, top: 22, bottom: 4, containLabel: true },
      tooltip: { trigger: 'item', backgroundColor: colors.surface, borderColor: colors.border, textStyle: { color: colors.foreground, fontSize: 12 } },
      xAxis: { type: 'category', data: data.map((d, i) => labelOf(d, config, i)), axisLine: { lineStyle: { color: colors.border } }, axisLabel: { color: colors.muted, fontSize: 10, interval: 0, overflow: 'truncate', width: 80, hideOverlap: true }, axisTick: { show: false } },
      yAxis: {
        type: 'value',
        min: config.min ?? 0,
        max: config.max ?? (unit === '%' ? 100 : undefined),
        splitNumber: 4,
        axisLabel: { color: colors.muted, fontSize: 10, formatter: axisFormatter(unit, config.decimals) },
        splitLine: { lineStyle: { color: colors.border, opacity: 0.6 } },
      },
      series: [
        {
          type: 'bar',
          barMaxWidth: 46,
          data: data.map((d, i) => {
            const value = shown(d, config);
            const level = levelFor(value, config);
            const color = sourceColor(config, i);
            const end = levelColor(level, color, colors);
            return {
              value: value ?? 0,
              itemStyle: { borderRadius: [4, 4, 0, 0], color: { type: 'linear', x: 0, y: 1, x2: 0, y2: 0, colorStops: [{ offset: 0, color }, { offset: 1, color: end }] } },
              label: { show: true, position: 'top', color: colors.foreground, fontSize: 10, formatter: () => formatValue(value, d.source.unit, config.decimals) },
              tooltip: { formatter: () => `${d.source.label}: ${value == null ? describeUnavailable(d.missing) : formatValue(value, d.source.unit, config.decimals)}` },
            };
          }),
          markLine: limits.length ? { symbol: ['none', 'none'], silent: true, label: { show: false }, lineStyle: { type: 'dashed' as const, width: 1 }, data: limits } : undefined,
        },
      ],
    } as ChartOption;
  }, [data, config, colors, reduced, unit]);
  return <EChart option={option} label={data.map((d, i) => `${labelOf(d, config, i)} ${formatValue(shown(d, config), d.source.unit, config.decimals)}`).join(', ')} className="min-h-28 flex-1" />;
}

interface TableRow {
  data: SourceData;
  index: number;
}

function TableBody({ data, config, viewKey }: { data: SourceData[]; config: CustomConfig; viewKey: string }) {
  const rows = data.map((d, index) => ({ data: d, index }));
  const fmt = (v: number | null, d: SourceData) => formatValue(v, d.source.unit, config.decimals);
  const columns: Column<TableRow>[] = [
    {
      id: 'label',
      header: 'Value',
      value: (r) => labelOf(r.data, config, r.index),
      groupable: false,
      className: 'max-w-56',
      cell: (r) => (
        <span className="flex min-w-0 items-center gap-1.5" title={r.data.source.label}>
          <span className="size-2 shrink-0 rounded-full" style={{ background: sourceColor(config, r.index) }} aria-hidden />
          <span className="truncate">{labelOf(r.data, config, r.index)}</span>
        </span>
      ),
    },
    { id: 'group', header: 'Area', value: (r) => r.data.source.group, hidden: true },
    { id: 'unit', header: 'Unit', value: (r) => r.data.source.unit || null, hidden: true },
    { id: 'now', header: 'Now', value: (r) => r.data.latest, align: 'right', firstDesc: true, groupable: false, cell: (r) => (r.data.latest == null ? <Missing data={r.data} /> : <LevelText value={r.data.latest} config={config} text={fmt(r.data.latest, r.data)} />) },
    { id: 'min', header: 'Low', value: (r) => r.data.min, align: 'right', groupable: false, cell: (r) => fmt(r.data.min, r.data), className: 'text-muted' },
    { id: 'avg', header: 'Average', value: (r) => r.data.avg, align: 'right', groupable: false, cell: (r) => fmt(r.data.avg, r.data), className: 'text-muted' },
    { id: 'max', header: 'High', value: (r) => r.data.max, align: 'right', groupable: false, firstDesc: true, cell: (r) => fmt(r.data.max, r.data) },
    ...(config.spark
      ? [{ id: 'trend', header: 'Trend', value: () => null, sortable: false, groupable: false, className: 'w-24', cell: (r: TableRow) => <Sparkline points={r.data.points} color={sourceColor(config, r.index)} className="h-5" /> } satisfies Column<TableRow>]
      : []),
  ];
  return (
    <DataTable
      viewKey={viewKey}
      label="Values"
      rows={rows}
      columns={columns}
      rowKey={(r) => r.data.source.id}
      defaultView={{ groupBy: [], sort: [] }}
      className="min-h-0 flex-1"
      scrollClassName="flex-1"
      compact
    />
  );
}

function LevelText({ value, config, text }: { value: number; config: CustomConfig; text: string }) {
  const level = levelFor(value, config);
  return <span className={cn('font-medium', level === 'critical' ? 'text-danger' : level === 'warning' ? 'text-warning' : undefined)}>{text}</span>;
}

function processValue(p: LiveProcess, measure: string): number | null {
  switch (measure) {
    case 'memory':
      return p.workingSetBytes;
    case 'gpu':
      return p.gpuPercent;
    case 'vram':
      return p.gpuMemoryBytes ?? null;
    case 'io':
      return p.ioBytesPerSec;
    case 'network':
      return p.netBytesPerSec ?? null;
    default:
      return p.cpuPercent;
  }
}

function processText(value: number | null, measure: string): string {
  if (value == null) return '—';
  if (measure === 'memory' || measure === 'vram') return formatBytes(value);
  if (measure === 'io' || measure === 'network') return formatBytesPerSecond(value);
  return `${value.toFixed(1)}%`;
}

function ProcessBody({ config, viewKey }: { config: CustomConfig; viewKey: string }) {
  const { live } = useDashboardData();
  const lists = live?.processes;
  const source =
    config.measure === 'memory'
      ? lists?.byMemory
      : config.measure === 'gpu'
        ? lists?.byGpu
        : config.measure === 'vram'
          ? (lists?.byGpuMemory ?? (lists ? [] : undefined))
          : config.measure === 'io'
            ? lists?.byIo
            : config.measure === 'network'
              ? lists?.byNetwork
              : lists?.byCpu;
  const words = config.match
    .split(',')
    .map((w) => w.trim().toLowerCase())
    .filter(Boolean);
  const rows = (source ?? []).filter((p) => words.length === 0 || words.some((w) => p.name.toLowerCase().includes(w))).slice(0, config.count);
  const top = Math.max(1e-9, ...rows.map((p) => processValue(p, config.measure) ?? 0));
  const label = PROCESS_MEASURES.find((m) => m.value === config.measure)?.label ?? 'CPU';
  if (!source) return <p className="text-sm text-muted">{config.measure === 'network' && lists ? 'Per-process network is measured with per-process I/O (Settings › Enhanced mode, on by default; needs a Pro license and the recorder running as administrator).' : 'Waiting for the process list…'}</p>;
  return (
    <DataTable
      viewKey={viewKey}
      label={`Processes by ${label}`}
      rows={rows}
      columns={[
        {
          id: 'name',
          header: 'Process',
          value: (p) => p.name,
          groupable: false,
          cell: (p) => (
            <div className="min-w-0">
              <span className="block truncate" title={p.name}>
                {p.name}
              </span>
              {config.showBars && (
                <div className="mt-1 h-1 rounded-full bg-surface-muted">
                  <div className="h-1 rounded-full bg-accent transition-[width] duration-500" style={{ width: `${Math.min(100, ((processValue(p, config.measure) ?? 0) / top) * 100)}%` }} />
                </div>
              )}
            </div>
          ),
        },
        { id: 'pid', header: 'PID', value: (p) => p.pid, groupable: false, align: 'right', className: 'w-16 font-mono text-xs text-muted' },
        { id: 'value', header: label, value: (p) => processValue(p, config.measure), groupable: false, align: 'right', firstDesc: true, className: 'w-24', cell: (p) => processText(processValue(p, config.measure), config.measure) },
        ...(config.measure === 'vram'
          ? []
          : [{ id: 'vram', header: 'GPU memory', value: (p: LiveProcess) => p.gpuMemoryBytes ?? null, groupable: false, align: 'right' as const, firstDesc: true, className: 'w-24 text-xs text-muted', cell: (p: LiveProcess) => processText(p.gpuMemoryBytes ?? null, 'vram') }]),
        ...(config.measure === 'network'
          ? []
          : [{ id: 'network', header: 'Network', value: (p: LiveProcess) => p.netBytesPerSec ?? null, groupable: false, align: 'right' as const, firstDesc: true, className: 'w-24 text-xs text-muted', cell: (p: LiveProcess) => processText(p.netBytesPerSec ?? null, 'network') }]),
        { id: 'state', header: 'Responding', value: (p) => (p.notResponding ? 'Not responding' : 'Responding'), hidden: true },
      ]}
      rowKey={(p) => p.pid}
      defaultView={{ groupBy: [], sort: [{ id: 'value', desc: true }] }}
      emptyText={words.length ? 'No running process matches.' : 'No processes.'}
      className="min-h-0 flex-1"
      scrollClassName="flex-1"
      compact
    />
  );
}

function EventFeed({ config, title, className }: { config: CustomConfig; title?: string | null; className?: string }) {
  const { live } = useDashboardData();
  const words = config.match
    .split(',')
    .map((w) => w.trim().toLowerCase())
    .filter(Boolean);
  const events = (live?.recentEvents ?? []).filter((e) => words.length === 0 || words.some((w) => e.title.toLowerCase().includes(w)));
  return <RecentEventsCard className={cn('h-full', className)} title={title || 'Event feed'} events={live ? events : undefined} initialMinimum={config.minimum} sources={config.eventSources} />;
}

function NoteBody({ config }: { config: CustomConfig }) {
  const size = { s: 'text-sm', m: 'text-base', l: 'text-xl font-medium' }[config.size];
  return <p className={cn('whitespace-pre-wrap break-words', size, config.align === 'center' && 'text-center')}>{config.text || 'Write a note in the widget settings.'}</p>;
}

/** The style that gives a widget its own accent color (buttons, bars and highlights inside it). */
export function accentStyle(accent: string | null | undefined): CSSProperties | undefined {
  if (!accent || !/^#[0-9a-f]{6}$/i.test(accent)) return undefined;
  return { '--accent': accent, '--color-accent': accent } as CSSProperties;
}

export function AccentScope({ accent, children, className }: { accent: string | null | undefined; children: ReactNode; className?: string }) {
  return (
    <div className={className} style={accentStyle(accent)}>
      {children}
    </div>
  );
}
