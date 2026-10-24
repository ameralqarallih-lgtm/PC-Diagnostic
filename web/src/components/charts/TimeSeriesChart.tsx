import { useReducedMotion } from 'motion/react';
import { useEffect, useMemo, useRef } from 'react';
import type { HistoryResult, SeriesInfo } from '@/lib/api/types';
import type { ChartOption, echarts } from '@/lib/charts/echarts';
import { useThemeColors } from '@/lib/charts/useThemeColors';
import { formatUnit } from '@/lib/format';
import { EChart } from './EChart';

/** A moment marked on the time axis. */
export interface ChartMarker {
  ts: number;
  label: string;
  tone: 'incident' | 'event' | 'center' | 'selected';
  /** Passed back to onMarkerClick (e.g. an incident key). */
  id?: string;
}

/** A zoomed part of the time axis (Unix ms). */
export interface ZoomRange {
  from: number;
  to: number;
}

export interface TimeSeriesChartProps {
  result: HistoryResult | undefined;
  catalog: SeriesInfo[];
  markers?: ChartMarker[];
  /** Shade the range between each bucket's minimum and maximum. */
  band?: boolean;
  /** A click on the chart picks that moment. */
  onPick?: (ts: number) => void;
  onMarkerClick?: (marker: ChartMarker) => void;
  /**
   * The zoomed part of the time axis, when the page keeps it (e.g. in the URL); null shows the whole window. Without
   * it the chart keeps the zoom itself across data updates.
   */
  zoom?: ZoomRange | null;
  /** Called (debounced) when the user zooms or pans; null when back to the whole window. */
  onZoomChange?: (zoom: ZoomRange | null) => void;
  label: string;
  className?: string;
}

/** The first data zoom's range from a chart, null when it shows everything. */
export function zoomOf(chart: { getOption: () => unknown }): ZoomRange | null {
  const zoom = (chart.getOption() as { dataZoom?: { start?: number; end?: number; startValue?: number; endValue?: number }[] }).dataZoom?.[0];
  if (!zoom || ((zoom.start ?? 0) <= 0.01 && (zoom.end ?? 100) >= 99.99)) return null;
  if (typeof zoom.startValue !== 'number' || typeof zoom.endValue !== 'number') return null;
  return { from: Math.round(zoom.startValue), to: Math.round(zoom.endValue) };
}

const PALETTE = ['#38bdf8', '#a78bfa', '#34d399', '#fbbf24', '#f472b6', '#fb923c', '#60a5fa', '#f87171', '#2dd4bf', '#c084fc', '#facc15', '#4ade80'];

/**
 * Overlaid series on a shared time axis: one Y axis per unit (up to four: two left, two right), average lines with
 * optional min–max bands, gaps where nothing was recorded, a shared crosshair and markers for incidents and events.
 */
export function TimeSeriesChart({ result, catalog, markers = [], band = false, onPick, onMarkerClick, zoom, onZoomChange, label, className }: TimeSeriesChartProps) {
  const colors = useThemeColors();
  const reduced = useReducedMotion() ?? false;
  const chartRef = useRef<ReturnType<typeof echarts.init> | null>(null);
  const pickRef = useRef(onPick);
  pickRef.current = onPick;
  // The zoom in effect: the page's when it keeps one, else the user's latest (kept across data updates, which
  // replace the whole option).
  const zoomRef = useRef<ZoomRange | null>(zoom ?? null);
  if (zoom !== undefined) zoomRef.current = zoom;
  const zoomChangeRef = useRef(onZoomChange);
  zoomChangeRef.current = onZoomChange;
  const zoomTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(zoomTimer.current), []);

  const option = useMemo<ChartOption>(() => {
    const kept = zoomRef.current;
    const range = kept ? { startValue: kept.from, endValue: kept.to } : { start: 0, end: 100 };
    const info = new Map(catalog.map((s) => [s.id, s]));
    const series = result?.series ?? [];
    const units = [...new Set(series.map((s) => info.get(s.id)?.unit ?? ''))].slice(0, 4);
    const gap = (result?.bucketMs ?? 1000) * 2.5;
    const withBands = band && series.length <= 4;

    const yAxis = units.map((unit, index) => ({
      type: 'value' as const,
      position: index % 2 === 0 ? ('left' as const) : ('right' as const),
      offset: index >= 2 ? 56 : 0,
      min: unit === '%' ? 0 : undefined,
      max: unit === '%' ? 100 : undefined,
      scale: unit !== '%',
      splitNumber: 4,
      axisLine: { show: units.length > 1, lineStyle: { color: colors.border } },
      axisLabel: { color: colors.muted, fontSize: 10, formatter: (v: number) => formatUnit(v, unit) },
      splitLine: { show: index === 0, lineStyle: { color: colors.border, opacity: 0.6 } },
    }));

    const lines: Record<string, unknown>[] = series.flatMap((s, index): Record<string, unknown>[] => {
      const meta = info.get(s.id);
      const unit = meta?.unit ?? '';
      const axis = Math.max(0, units.indexOf(unit));
      const color = PALETTE[index % PALETTE.length]!;
      const avg: [number, number | null][] = [];
      const low: [number, number | null][] = [];
      const spread: [number, number | null][] = [];
      let previous: number | undefined;
      for (const [t, min, mean, max] of s.points) {
        if (previous !== undefined && t - previous > gap) {
          avg.push([previous + 1, null]);
          low.push([previous + 1, null]);
          spread.push([previous + 1, null]);
        }
        avg.push([t, mean]);
        low.push([t, min]);
        spread.push([t, max - min]);
        previous = t;
      }

      const name = meta?.label ?? s.id;
      const main = {
        id: s.id,
        name,
        type: 'line' as const,
        yAxisIndex: axis,
        data: avg,
        showSymbol: false,
        connectNulls: false,
        sampling: 'lttb' as const,
        lineStyle: { width: 1.6, color },
        itemStyle: { color },
        // No hover highlight: the axis tooltip already lists every series, and focusing whichever line's point is under
        // the pointer faded the others in and out as the mouse moved across overlapping lines.
        emphasis: { disabled: true },
        tooltip: { valueFormatter: (v: unknown) => (typeof v === 'number' ? formatUnit(v, unit) : 'No data') },
      };
      if (!withBands) return [main];
      // Band: an invisible line at the minimum plus the spread stacked on it.
      return [
        main,
        { id: `${s.id}~min`, name, type: 'line' as const, yAxisIndex: axis, data: low, stack: `band-${s.id}`, showSymbol: false, lineStyle: { opacity: 0 }, tooltip: { show: false }, silent: true },
        {
          id: `${s.id}~band`,
          name,
          type: 'line' as const,
          yAxisIndex: axis,
          data: spread,
          stack: `band-${s.id}`,
          showSymbol: false,
          lineStyle: { opacity: 0 },
          areaStyle: { color, opacity: 0.12 },
          tooltip: { show: false },
          silent: true,
        },
      ];
    });

    const markerColor = (tone: ChartMarker['tone']) =>
      tone === 'incident' ? colors.danger : tone === 'center' ? colors.danger : tone === 'selected' ? colors.accent : colors.warning;

    const markLine = {
      symbol: ['none', 'none'] as ['none', 'none'],
      animation: false,
      label: { fontSize: 10, formatter: '{b}', position: 'insideEndTop' as const },
      data: markers.map((m) => ({
        name: m.label,
        xAxis: m.ts,
        lineStyle: { color: markerColor(m.tone), type: m.tone === 'selected' ? ('solid' as const) : ('dashed' as const), width: m.tone === 'selected' ? 2 : 1.2 },
        label: { color: markerColor(m.tone), show: m.tone !== 'event' },
        markerId: m.id,
      })),
    };
    const first = lines[0];
    if (first) Object.assign(first, { markLine });
    else
      lines.push({
        id: 'markers',
        name: 'Markers',
        type: 'line',
        yAxisIndex: 0,
        data: [],
        showSymbol: false,
        lineStyle: { width: 1.6, color: colors.muted },
        itemStyle: { color: colors.muted },
        emphasis: { disabled: true },
        tooltip: { valueFormatter: () => '' },
        markLine,
      });

    const legendNames = series.map((s) => info.get(s.id)?.label ?? s.id);
    return {
      animation: !reduced,
      animationDuration: 400,
      grid: { left: 8, right: units.length > 1 ? 12 : 16, top: 40, bottom: 48, containLabel: true },
      legend: { top: 0, type: 'scroll', textStyle: { color: colors.muted, fontSize: 11 }, inactiveColor: colors.border, data: legendNames, pageIconColor: colors.accent },
      tooltip: {
        trigger: 'axis',
        backgroundColor: colors.surface,
        borderColor: colors.border,
        textStyle: { color: colors.foreground, fontSize: 12 },
        axisPointer: { type: 'line', lineStyle: { color: colors.muted, type: 'dashed' } },
      },
      xAxis: {
        type: 'time',
        min: result?.from,
        max: result?.to,
        axisLine: { lineStyle: { color: colors.border } },
        axisLabel: { color: colors.muted, fontSize: 10, hideOverlap: true },
        splitLine: { show: false },
      },
      yAxis: yAxis.length > 0 ? yAxis : [{ type: 'value', axisLabel: { color: colors.muted } }],
      dataZoom: [
        { type: 'inside', filterMode: 'none', zoomOnMouseWheel: 'ctrl', moveOnMouseWheel: false, ...range },
        { type: 'slider', height: 16, bottom: 6, borderColor: colors.border, fillerColor: `${colors.accent}22`, handleStyle: { color: colors.accent }, textStyle: { color: colors.muted, fontSize: 10 }, ...range },
      ],
      series: lines,
    } as ChartOption;
  }, [result, catalog, markers, band, colors, reduced, zoom]);

  return (
    <EChart
      option={option}
      notMerge
      label={label}
      className={className}
      onClick={(params) => {
        const marker = (params.data as { markerId?: string; xAxis?: number } | undefined) ?? undefined;
        const found = marker?.markerId ? markers.find((m) => m.id === marker.markerId) : undefined;
        if (found) onMarkerClick?.(found);
      }}
      onReady={(chart) => {
        chartRef.current = chart;
        chart.on('datazoom', () => {
          const next = zoomOf(chart);
          zoomRef.current = next;
          clearTimeout(zoomTimer.current);
          zoomTimer.current = setTimeout(() => zoomChangeRef.current?.(next), 250);
        });
        chart.getZr().on('click', (event: { offsetX: number; offsetY: number }) => {
          // Only clicks on the plot itself pick a moment (not the legend or the zoom slider).
          if (!pickRef.current || !chart.containPixel({ gridIndex: 0 }, [event.offsetX, event.offsetY])) return;
          const point = chart.convertFromPixel({ gridIndex: 0 }, [event.offsetX, event.offsetY]) as number[] | undefined;
          if (point && Number.isFinite(point[0])) pickRef.current(Math.round(point[0]!));
        });
      }}
    />
  );
}
