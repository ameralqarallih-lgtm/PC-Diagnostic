import { Link } from '@tanstack/react-router';
import { useEffect, useMemo, useState } from 'react';
import { FloatingWindow } from '@/components/ui/floating-window';
import { CaptureBar } from '@/features/capture/CaptureBar';
import { MomentPanel } from '@/features/history/MomentPanel';
import { DEFAULT_SERIES } from '@/features/history/ranges';
import { IncidentStatusCard } from '@/features/incidents/IncidentStatusCard';
import type { SeriesKey } from '@/features/live/series';
import { RecorderCard } from '@/features/recorder/RecorderCard';
import type { Severity } from '@/lib/api/types';
import { useStoredState } from '@/lib/useStoredState';
import { AnomaliesCard } from './AnomaliesCard';
import { CustomWidgetView } from './custom/CustomWidget';
import { readCustom } from './custom/model';
import type { WidgetInstance } from './config/types';
import { flag, list, text } from './config/widgets';
import { useDashboardData } from './DashboardContext';
import { GaugeGrid } from './GaugeGrid';
import { DisplaysCard, GpuCard, MetricWidget, NetworkCard, TemperaturesCard } from './HealthCards';
import { LiveChartCard, viewOf, type ChartView, type ChartWindow, type LiveZoom } from './LiveChartCard';
import { RecentEventsCard } from './RecentEventsCard';
import { TopProcessesCard } from './TopProcessesCard';

const FILL = 'h-full overflow-auto';

/** Renders one widget with its settings, reading live data from the dashboard. */
export function WidgetBody({ widget }: { widget: WidgetInstance }) {
  const data = useDashboardData();
  const { settings } = widget;
  const title = widget.title ?? undefined;

  switch (widget.type) {
    case 'capture':
      return (
        <CaptureBar
          className={FILL}
          canCapture={data.connected && data.status != null}
          busy={data.capturing}
          activeIncident={data.status?.activeIncident}
          activeUntil={data.status?.activeIncidentUntil}
          unavailableReason={data.detail ?? 'Waiting for the recorder Agent…'}
          quickTypes={list(settings, 'quickTypes')}
          showNote={flag(settings, 'note', true)}
          onCapture={data.capture}
        />
      );
    case 'gauges':
      return (
        <GaugeGrid
          live={data.live}
          include={list(settings, 'metrics')}
          selected={data.focus.gauge}
          onSelect={(gauge) => data.setFocus(gauge?.id ?? null, gauge?.series ?? null)}
        />
      );
    case 'chart':
      return <ChartWidget widget={widget} />;
    case 'metric':
      return <MetricWidget className={FILL} live={data.live} series={data.series} metric={text(settings, 'metric', 'cpu')} title={title} />;
    case 'recorder':
      return (
        <RecorderCard
          className={FILL}
          title={title}
          status={data.status}
          unavailable={data.detail}
          agent={data.live?.agent}
          busy={data.commandBusy}
          onCommand={data.command}
          showOverhead={flag(settings, 'overhead', true)}
        />
      );
    case 'processes':
      return (
        <TopProcessesCard
          className={FILL}
          title={title}
          processes={data.live?.processes}
          initialTab={text(settings, 'tab', 'cpu')}
          rows={Number(text(settings, 'rows', '8'))}
          bars={flag(settings, 'bars', true)}
          showFilter={flag(settings, 'filter', true)}
        />
      );
    case 'incidents':
      return <IncidentStatusCard className={FILL} title={title} status={data.status} count={Number(text(settings, 'count', '5'))} />;
    case 'events':
      return (
        <RecentEventsCard
          className={FILL}
          title={title}
          events={data.live?.recentEvents}
          initialMinimum={text(settings, 'minimum', '0')}
          sources={list(settings, 'sources')}
        />
      );
    case 'anomalies':
      return (
        <AnomaliesCard
          className={FILL}
          title={title}
          anomalies={data.live?.anomalies}
          recording={data.live?.recording ?? false}
          runningOnly={flag(settings, 'running', false)}
          minimum={text(settings, 'minimum', 'Info') as Severity}
        />
      );
    case 'sensors':
      return <TemperaturesCard className={FILL} title={title} live={data.live} include={list(settings, 'include')} />;
    case 'gpu':
      return <GpuCard className={FILL} title={title} live={data.live} adapters={text(settings, 'adapters', 'all')} />;
    case 'displays':
      return <DisplaysCard className={FILL} title={title} live={data.live} />;
    case 'network':
      return (
        <NetworkCard className={FILL} title={title} live={data.live} virtual={flag(settings, 'virtual', false)} disconnected={flag(settings, 'disconnected', false)} />
      );
    case 'custom':
      return <CustomWidget widget={widget} />;
  }
}

function CustomWidget({ widget }: { widget: WidgetInstance }) {
  const config = useMemo(() => readCustom(widget.settings), [widget.settings]);
  return <CustomWidgetView config={config} title={widget.title} viewKey={`custom.${widget.id}`} className={FILL} />;
}

/** What a chart widget shows in this browser; kept across reloads until the widget's own settings change. */
interface ChartViewState {
  /** The widget settings this was chosen against. */
  base: string;
  view: ChartView;
  window: ChartWindow;
  zoom: LiveZoom | null;
}

const VIEWS: readonly string[] = ['usage', 'temperature', 'network'];
const CHART_WINDOWS: readonly number[] = [1, 5, 10];

function isChartViewState(value: unknown): value is ChartViewState {
  const v = value as Partial<ChartViewState> | null;
  const zoom = v?.zoom as Partial<LiveZoom> | null | undefined;
  return (
    typeof v === 'object' &&
    v !== null &&
    typeof v.base === 'string' &&
    VIEWS.includes(v.view as string) &&
    CHART_WINDOWS.includes(v.window as number) &&
    (zoom === null || (typeof zoom === 'object' && typeof zoom?.start === 'number' && typeof zoom.end === 'number' && zoom.start < zoom.end))
  );
}

/**
 * A chart keeps its own view, window and zoom (remembered in this browser, so a reload keeps them); a selected gauge
 * switches it to that gauge's view when it includes the line. Changing the widget's settings starts from them again.
 */
function ChartWidget({ widget }: { widget: WidgetInstance }) {
  const data = useDashboardData();
  const include = list(widget.settings, 'series') as SeriesKey[];
  const initialView = text(widget.settings, 'view', 'usage') as ChartView;
  const initialWindow = Number(text(widget.settings, 'window', '5')) as ChartWindow;
  const base = `${initialView}|${initialWindow}`;
  const initial = useMemo<ChartViewState>(() => ({ base, view: initialView, window: initialWindow, zoom: null }), [base, initialView, initialWindow]);
  const [stored, setStored] = useStoredState<ChartViewState>(`pcdiag.dashboard.chart.${widget.id}`, initial, isChartViewState);
  const [moment, setMoment] = useState<number | null>(null);
  const state = stored.base === base ? stored : initial;
  const update = (patch: Partial<ChartViewState>) => setStored({ ...state, ...patch });

  const focus = data.focus.series;
  const includesFocus = focus != null && include.includes(focus);
  useEffect(() => {
    if (focus && includesFocus && viewOf(focus) !== state.view) setStored({ ...state, view: viewOf(focus) });
    // Only a newly selected gauge switches the view (not every state change).
  }, [focus, includesFocus]);

  return (
    <>
      <LiveChartCard
        title={widget.title ?? undefined}
        series={data.series}
        view={state.view}
        onViewChange={(view) => update({ view })}
        windowMinutes={state.window}
        onWindowChange={(window) => update({ window })}
        zoom={state.zoom}
        onZoomChange={(zoom) => update({ zoom })}
        focus={includesFocus ? focus : null}
        include={include}
        markers={!!widget.settings.markers}
        events={data.live?.recentEvents ?? []}
        incidents={data.incidents}
        activeIncident={data.activeIncident}
        onPick={setMoment}
      />
      <FloatingWindow open={moment !== null} title="This moment" onClose={() => setMoment(null)} storageKey="pcdiag.moment.window">
        {moment !== null && (
          <MomentPanel
            at={moment}
            actions={
              <Link
                to="/history"
                search={{ range: '15m', series: DEFAULT_SERIES.join(','), end: moment + 450_000 < Date.now() ? moment + 450_000 : undefined, at: moment }}
                className="text-sm text-accent hover:underline"
              >
                Open in History
              </Link>
            }
          />
        )}
      </FloatingWindow>
    </>
  );
}
