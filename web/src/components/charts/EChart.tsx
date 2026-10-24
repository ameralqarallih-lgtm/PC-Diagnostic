import { useEffect, useRef } from 'react';
import { echarts, type ChartOption } from '@/lib/charts/echarts';
import { cn } from '@/lib/utils';

type ChartInstance = ReturnType<typeof echarts.init>;

export interface ChartClickParams {
  seriesName?: string;
  name?: string;
  value?: unknown;
  data?: unknown;
}

export interface EChartProps {
  option: ChartOption;
  /** Describes the chart for screen readers (the numbers are also shown as text nearby). */
  label: string;
  className?: string;
  /** Replace instead of merge. */
  notMerge?: boolean;
  /** Components replaced by id instead of merged (e.g. ['series'] when series come and go but should still animate). */
  replaceMerge?: string[];
  onClick?: (params: ChartClickParams) => void;
  /** Called with the chart once it exists (e.g. to dispatch highlight actions). */
  onReady?: (chart: ChartInstance) => void;
}

/**
 * One ECharts canvas. Resizes with its container, skips work while the page is hidden (updates are applied
 * when it becomes visible again) and disposes itself on unmount.
 */
export function EChart({ option, label, className, notMerge = false, replaceMerge, onClick, onReady }: EChartProps) {
  const host = useRef<HTMLDivElement>(null);
  const chart = useRef<ChartInstance | null>(null);
  const pending = useRef<ChartOption | null>(null);
  const clickRef = useRef(onClick);
  clickRef.current = onClick;

  useEffect(() => {
    const element = host.current;
    if (!element) return undefined;
    const instance = echarts.init(element, undefined, { renderer: 'canvas' });
    chart.current = instance;
    instance.on('click', (params) => clickRef.current?.(params as ChartClickParams));
    onReady?.(instance);

    const resize = new ResizeObserver(() => instance.resize());
    resize.observe(element);
    const visibility = () => {
      if (!document.hidden && pending.current) {
        instance.setOption(pending.current, { notMerge, replaceMerge });
        pending.current = null;
      }
    };
    document.addEventListener('visibilitychange', visibility);

    return () => {
      document.removeEventListener('visibilitychange', visibility);
      resize.disconnect();
      instance.dispose();
      chart.current = null;
    };
    // The instance lives as long as the element; option updates are handled below.
  }, []);

  useEffect(() => {
    const instance = chart.current;
    if (!instance) return;
    if (document.hidden) {
      pending.current = option;
      return;
    }
    instance.setOption(option, { notMerge, replaceMerge, lazyUpdate: true });
  }, [option, notMerge, replaceMerge]);

  return <div ref={host} role="img" aria-label={label} className={cn('min-h-0 w-full', className)} />;
}
