import { useReducedMotion } from 'motion/react';
import { RotateCcw } from 'lucide-react';
import { useEffect, useMemo, useRef } from 'react';
import { EChart } from '@/components/charts/EChart';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { keyedChartData, type LiveSeries, type SeriesKey } from '@/features/live/series';
import type { IncidentSummary, LiveEvent } from '@/lib/api/types';
import type { ChartOption, echarts } from '@/lib/charts/echarts';
import { SERIES_COLORS, useThemeColors } from '@/lib/charts/useThemeColors';
import { formatBytesPerSecond } from '@/lib/format';
import { cn } from '@/lib/utils';

export type ChartView = 'usage' | 'temperature' | 'network';

interface ViewDefinition {
  id: ChartView;
  label: string;
  series: { key: SeriesKey; name: string }[];
}

const USAGE_VIEW: ViewDefinition = {
  id: 'usage',
  label: 'Utilization',
  series: [
    { key: 'cpu', name: 'CPU' },
    { key: 'memory', name: 'Memory' },
    { key: 'gpu', name: 'GPU' },
    { key: 'vram', name: 'VRAM' },
    { key: 'disk', name: 'Disk' },
  ],
};

export const CHART_VIEWS: ViewDefinition[] = [
  USAGE_VIEW,
  {
    id: 'temperature',
    label: 'Temperatures',
    series: [
      { key: 'cpuTemperature', name: 'CPU package' },
      { key: 'gpuTemperature', name: 'GPU core' },
      { key: 'gpuHotspot', name: 'GPU hotspot' },
    ],
  },
  {
    id: 'network',
    label: 'Network',
    series: [
      { key: 'netReceive', name: 'Download' },
      { key: 'netSend', name: 'Upload' },
    ],
  },
];

/** The chart view that shows a series. */
export function viewOf(key: SeriesKey): ChartView {
  return CHART_VIEWS.find((v) => v.series.some((s) => s.key === key))?.id ?? 'usage';
}

const WINDOWS = [1, 5, 10] as const;
const REPLACE_SERIES = ['series'];
export type ChartWindow = (typeof WINDOWS)[number];

export interface LiveChartCardProps {
  series: LiveSeries;
  view: ChartView;
  onViewChange: (view: ChartView) => void;
  windowMinutes: ChartWindow;
  onWindowChange: (minutes: ChartWindow) => void;
  /** Emphasized series (selected gauge). */
  focus: SeriesKey | null;
  events: LiveEvent[];
  incidents: IncidentSummary[];
  activeIncident: { key: string; from: string; until: string } | null;
  /** Lines the user included (all when omitted). */
  include?: SeriesKey[];
  /** Incident and event markers. */
  markers?: boolean;
  /** The zoomed part of the window in percent (the window slides, so the zoom is relative); null shows all of it. */
  zoom?: LiveZoom | null;
  /** Called (debounced) when the user zooms; null when back to the whole window. */
  onZoomChange?: (zoom: LiveZoom | null) => void;
  /** A click on the plot picks that moment (Unix ms). */
  onPick?: (ts: number) => void;
  title?: string;
  className?: string;
}

/** Part of the live chart's window, in percent of it. */
export interface LiveZoom {
  start: number;
  end: number;
}

function unitFormatter(view: ChartView): (value: number) => string {
  if (view === 'network') return (v) => formatBytesPerSecond(v);
  if (view === 'temperature') return (v) => `${Math.round(v)}°C`;
  return (v) => `${v.toFixed(0)}%`;
}

/**
 * The realtime chart: up to ten minutes of headline values, streaming once a second. Hover for exact values, drag
 * or scroll to zoom, click legend entries to hide lines. Incidents are marked with a line (and the one being
 * recorded with a band); important events pulse at the top.
 */
export function LiveChartCard(props: LiveChartCardProps) {
  const { series, view, onViewChange, windowMinutes, onWindowChange, focus, events, incidents, activeIncident, include, markers = true, zoom, onZoomChange, onPick, title, className } = props;
  const colors = useThemeColors();
  const reduced = useReducedMotion() ?? false;
  const chartRef = useRef<ReturnType<typeof echarts.init> | null>(null);
  // The zoom in effect, updated at once while the user drags (the chart is redrawn every second).
  const zoomRef = useRef<LiveZoom | null>(zoom ?? null);
  const zoomChangeRef = useRef(onZoomChange);
  zoomChangeRef.current = onZoomChange;
  const pickRef = useRef(onPick);
  pickRef.current = onPick;
  const zoomTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(zoomTimer.current), []);
  const base = CHART_VIEWS.find((v) => v.id === view) ?? USAGE_VIEW;
  const definition = useMemo(() => ({ ...base, series: base.series.filter((s) => !include || include.includes(s.key)) }), [base, include]);

  const option = useMemo<ChartOption>(() => {
    const zoomRange = { start: zoomRef.current?.start ?? 0, end: zoomRef.current?.end ?? 100 };
    const last = series.times.at(-1) ?? Date.now();
    const from = last - windowMinutes * 60_000;
    const format = unitFormatter(view);
    const visibleIncidents = markers ? incidents.filter((i) => Date.parse(i.centerAt) >= from) : [];
    const importantEvents = markers ? events.filter((e) => (e.severity === 'High' || e.severity === 'Critical') && Date.parse(e.timestamp) >= from) : [];
    const percent = view === 'usage';

    const lines = definition.series.map(({ key, name }, index) => {
      const emphasized = focus === key;
      const dimmed = focus != null && !emphasized && definition.series.some((s) => s.key === focus);
      const color = SERIES_COLORS[key];
      return {
        id: key,
        name,
        type: 'line' as const,
        data: keyedChartData(series, key),
        smooth: 0.35,
        showSymbol: false,
        connectNulls: false,
        sampling: 'lttb' as const,
        z: emphasized ? 5 : 2,
        lineStyle: { width: emphasized ? 3 : 1.8, color, opacity: dimmed ? 0.25 : 1, shadowBlur: emphasized ? 10 : 0, shadowColor: color },
        itemStyle: { color },
        areaStyle:
          emphasized || view === 'network'
            ? {
                opacity: dimmed ? 0.05 : 1,
                color: {
                  type: 'linear' as const,
                  x: 0,
                  y: 0,
                  x2: 0,
                  y2: 1,
                  colorStops: [
                    { offset: 0, color: `${color}55` },
                    { offset: 1, color: `${color}00` },
                  ],
                },
              }
            : undefined,
        // Only a selected gauge focuses a line (above). ECharts' hover focus faded the other lines whenever the pointer
        // touched a line's point, so moving across the chart flickered between them.
        emphasis: { disabled: true },
        // Markers ride on the first line so they share its axes.
        ...(index === 0
          ? {
              markLine: {
                symbol: ['none', 'none'],
                silent: false,
                animation: false,
                label: { color: colors.danger, fontSize: 10, formatter: '{b}' },
                lineStyle: { color: colors.danger, type: 'dashed' as const, width: 1.2 },
                data: visibleIncidents.map((i) => ({ name: i.key.slice(-5), xAxis: Date.parse(i.centerAt) })),
              },
              markArea: activeIncident && markers
                ? {
                    silent: true,
                    itemStyle: { color: `${colors.danger}22` },
                    label: { color: colors.danger, fontSize: 10, position: 'insideTop' as const },
                    data: [[{ name: `Recording ${activeIncident.key}`, xAxis: Date.parse(activeIncident.from) }, { xAxis: Date.parse(activeIncident.until) }]],
                  }
                : undefined,
            }
          : {}),
      };
    });

    const ripples = percent
      ? [
          {
            id: 'events',
            name: 'Important events',
            type: 'effectScatter' as const,
            data: importantEvents.map((e) => ({ value: [Date.parse(e.timestamp), 98], name: e.title })),
            symbolSize: 9,
            rippleEffect: { brushType: 'stroke' as const, scale: 3.2, period: 2.4 },
            itemStyle: { color: colors.danger },
            showEffectOn: 'render' as const,
            tooltip: { trigger: 'item' as const, formatter: (p: { name: string }) => p.name },
            z: 6,
          },
        ]
      : [];

    return {
      animation: !reduced,
      // Drawn at once when the dashboard opens (a left-to-right reveal looked like slow loading).
      animationDuration: 0,
      // Points are named by their time, so each update slides the line (a sample arrives every second; the slide lasts
      // about as long, so the chart moves steadily) and never reshapes what was already drawn.
      animationDurationUpdate: reduced ? 0 : 950,
      animationEasingUpdate: 'linear',
      grid: { left: 8, right: 16, top: 36, bottom: 44, containLabel: true },
      legend: {
        top: 0,
        left: 0,
        icon: 'roundRect',
        itemWidth: 12,
        itemHeight: 4,
        textStyle: { color: colors.muted, fontSize: 11 },
        inactiveColor: colors.border,
        data: definition.series.map((s) => s.name),
      },
      tooltip: {
        trigger: 'axis',
        backgroundColor: colors.surface,
        borderColor: colors.border,
        textStyle: { color: colors.foreground, fontSize: 12 },
        axisPointer: { type: 'line', lineStyle: { color: colors.muted, type: 'dashed' } },
        valueFormatter: (value) => (typeof value === 'number' ? format(value) : 'Unavailable'),
      },
      xAxis: {
        type: 'time',
        min: from,
        max: last,
        axisLine: { lineStyle: { color: colors.border } },
        axisLabel: { color: colors.muted, fontSize: 10, hideOverlap: true },
        splitLine: { show: false },
      },
      yAxis: {
        type: 'value',
        min: percent ? 0 : undefined,
        max: percent ? 100 : undefined,
        scale: !percent,
        splitNumber: 4,
        axisLabel: { color: colors.muted, fontSize: 10, formatter: (v: number) => format(v) },
        splitLine: { lineStyle: { color: colors.border, opacity: 0.6 } },
      },
      dataZoom: [
        { type: 'inside', filterMode: 'none', zoomOnMouseWheel: 'ctrl', moveOnMouseWheel: false, ...zoomRange },
        {
          ...zoomRange,
          type: 'slider',
          height: 16,
          bottom: 4,
          borderColor: colors.border,
          fillerColor: `${colors.accent}22`,
          handleStyle: { color: colors.accent },
          textStyle: { color: colors.muted, fontSize: 10 },
          dataBackground: { lineStyle: { color: colors.muted, opacity: 0.4 }, areaStyle: { color: colors.muted, opacity: 0.08 } },
        },
      ],
      series: [...lines, ...ripples],
    } as ChartOption;
  }, [series, view, windowMinutes, focus, events, incidents, activeIncident, colors, reduced, definition, markers]);

  const resetZoom = () => chartRef.current?.dispatchAction({ type: 'dataZoom', start: 0, end: 100 });

  const reportZoom = (chart: ReturnType<typeof echarts.init>) => {
    const current = (chart.getOption() as { dataZoom?: { start?: number; end?: number }[] }).dataZoom?.[0];
    const start = current?.start ?? 0;
    const end = current?.end ?? 100;
    const next = start <= 0.01 && end >= 99.99 ? null : { start: Math.round(start * 100) / 100, end: Math.round(end * 100) / 100 };
    zoomRef.current = next;
    clearTimeout(zoomTimer.current);
    zoomTimer.current = setTimeout(() => zoomChangeRef.current?.(next), 250);
  };

  return (
    <Card className={cn('flex h-full flex-col', className)}>
      <CardHeader className="flex-wrap">
        <CardTitle>{title ?? 'Live chart'}</CardTitle>
        <div className="flex flex-wrap items-center gap-2">
          <Segmented
            label="Chart"
            options={CHART_VIEWS.map((v) => ({ value: v.id, label: v.label }))}
            value={view}
            onChange={(v) => onViewChange(v as ChartView)}
          />
          <Segmented
            label="Time window"
            options={WINDOWS.map((w) => ({ value: String(w), label: `${w}m` }))}
            value={String(windowMinutes)}
            onChange={(v) => onWindowChange(Number(v) as ChartWindow)}
          />
          <Button variant="ghost" size="icon" aria-label="Reset zoom" title="Reset zoom" onClick={resetZoom}>
            <RotateCcw aria-hidden />
          </Button>
        </div>
      </CardHeader>
      <CardContent className="flex min-h-0 flex-1 flex-col">
        <EChart
          option={option}
          replaceMerge={REPLACE_SERIES}
          onReady={(chart) => {
            chartRef.current = chart;
            chart.on('datazoom', () => reportZoom(chart));
            chart.getZr().on('click', (event: { offsetX: number; offsetY: number }) => {
              // Only clicks on the plot itself pick a moment (not the legend or the zoom slider).
              if (!pickRef.current || !chart.containPixel({ gridIndex: 0 }, [event.offsetX, event.offsetY])) return;
              const point = chart.convertFromPixel({ gridIndex: 0 }, [event.offsetX, event.offsetY]) as number[] | undefined;
              if (point && Number.isFinite(point[0])) pickRef.current(Math.round(point[0]!));
            });
          }}
          label={`${definition.label} over the last ${windowMinutes} minute${windowMinutes > 1 ? 's' : ''}`}
          className="min-h-36 flex-1"
        />
        {definition.series.length === 0 && <p className="text-sm text-muted">No lines of this kind are included. Change the widget settings to add some.</p>}
        {onPick && definition.series.length > 0 && <p className="mt-1 text-xs text-muted">Click the chart to see everything recorded at that moment, including the processes running.</p>}
      </CardContent>
    </Card>
  );
}

export function Segmented({
  label,
  options,
  value,
  onChange,
}: {
  label: string;
  options: { value: string; label: string }[];
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <div role="radiogroup" aria-label={label} className="flex rounded-md bg-surface-muted p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={value === o.value}
          onClick={() => onChange(o.value)}
          className={cn(
            'rounded px-2.5 py-1 text-xs font-medium transition-colors',
            value === o.value ? 'bg-surface text-foreground shadow-sm' : 'text-muted hover:text-foreground',
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}
