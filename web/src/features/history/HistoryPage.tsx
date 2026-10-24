import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { Clock, ListFilter, SlidersHorizontal, Stethoscope, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { TimeSeriesChart, type ChartMarker } from '@/components/charts/TimeSeriesChart';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Segmented } from '@/features/dashboard/LiveChartCard';
import { apiGet } from '@/lib/api/client';
import type { IncidentPage, TimelineEntry } from '@/lib/api/types';
import { formatDateTime, formatTime } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { cn } from '@/lib/utils';
import { HISTORY_RANGES, RANGE_MS, refreshFor, type HistoryRange } from './ranges';
import { MomentPanel } from './MomentPanel';
import { SeriesPicker } from './SeriesPicker';
import { useCatalog, useHistoryEvents, useSeries } from './useHistory';

const LIVE: { kind: 'live' } = { kind: 'live' };

const EVENT_FILTERS = [
  { value: '1', label: 'All' },
  { value: '2', label: 'Warnings' },
  { value: '3', label: 'Serious' },
];

const IMPORTANCE = ['Everything', 'Notable', 'Warnings', 'Serious', 'Critical'];
const KIND_TEXT: Record<string, string> = { trigger: 'Incident', anomaly: 'Anomaly', 'windows-event': 'Windows event', display: 'Display', process: 'Process', service: 'Service', recorder: 'Recorder' };

/** What the events in the window can be grouped and sorted by. */
const EVENT_COLUMNS: Column<TimelineEntry>[] = [
  { id: 'time', header: 'Time', value: (e) => e.ts, groupable: false, firstDesc: true },
  { id: 'kind', header: 'Kind', value: (e) => KIND_TEXT[e.kind] ?? e.kind },
  { id: 'importance', header: 'Importance', value: (e) => e.importance, group: (e) => IMPORTANCE[e.importance] ?? String(e.importance), firstDesc: true },
  { id: 'title', header: 'Title', value: (e) => e.title, groupable: false },
  { id: 'provider', header: 'Provider', value: (e) => (typeof e.detail?.provider === 'string' ? e.detail.provider : null) },
];

/**
 * History: overlay any recorded series over a window of 1 minute to 30 days, with incidents and important events
 * marked. The view lives in the URL (range, series, end), so it can be bookmarked, shared and reloaded.
 */
export function HistoryPage() {
  const search = useSearch({ from: '/history' });
  const navigate = useNavigate({ from: '/history' });
  const live = search.end === undefined;
  // A live window moves forward on the range's refresh interval; each move is one fetch.
  const refresh = refreshFor(search.range);
  const now = useNow(live && refresh !== undefined, refresh ?? 60_000);
  const to = search.end ?? Math.floor(now / 1000) * 1000;
  const from = to - RANGE_MS[search.range];
  const ids = useMemo(() => search.series.split(',').filter(Boolean), [search.series]);
  const band = search.band ?? false;
  const minImportance = String(search.events ?? 2);
  // A zoom the moving live window has left behind no longer applies.
  const zoomed = search.zoomFrom !== undefined && search.zoomTo !== undefined && search.zoomTo > from;
  const zoom = useMemo(
    () => (zoomed && search.zoomFrom !== undefined && search.zoomTo !== undefined ? { from: search.zoomFrom, to: search.zoomTo } : null),
    [zoomed, search.zoomFrom, search.zoomTo],
  );
  const [pickerOpen, setPickerOpen] = useState(false);

  const catalog = useCatalog(LIVE);
  const series = useSeries(LIVE, { ids, from, to, points: 900 });
  const events = useHistoryEvents(from, to, Number(minImportance));
  const incidents = useQuery({
    queryKey: ['incidents', 'range', Math.round(from / 60_000), Math.round(to / 60_000)],
    queryFn: ({ signal }) => apiGet<IncidentPage>(`/incidents?from=${Math.round(from)}&to=${Math.round(to)}&limit=200`, signal),
  });

  const set = (patch: Partial<typeof search>) => void navigate({ search: (s) => ({ ...s, ...patch }), replace: true });
  // A new window starts unzoomed.
  const unzoomed = { zoomFrom: undefined, zoomTo: undefined };
  const centerOn = (ts: number) => set({ end: Math.round(ts + RANGE_MS[search.range] / 2), ...unzoomed });

  const markers = useMemo<ChartMarker[]>(
    () => [
      ...(incidents.data?.items ?? []).map((i) => ({ ts: Date.parse(i.centerAt), label: i.key.slice(-5), tone: 'incident' as const, id: i.key })),
      ...(events.data ?? []).filter((e) => e.importance >= 3).map((e) => ({ ts: e.ts, label: e.title, tone: 'event' as const })),
      ...(search.at !== undefined ? [{ ts: search.at, label: formatTime(new Date(search.at).toISOString()), tone: 'selected' as const }] : []),
    ],
    [incidents.data, events.data, search.at],
  );

  useEffect(() => {
    if (!pickerOpen) return undefined;
    const close = (e: KeyboardEvent) => e.key === 'Escape' && setPickerOpen(false);
    document.addEventListener('keydown', close);
    return () => document.removeEventListener('keydown', close);
  }, [pickerOpen]);

  const picker = <SeriesPicker catalog={catalog.data ?? []} selected={ids} onChange={(next) => set({ series: next.join(',') })} />;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">History</h1>
        <div className="flex flex-wrap items-center gap-2">
          <Segmented label="Time range" options={HISTORY_RANGES.map((r) => ({ value: r, label: r }))} value={search.range} onChange={(r) => set({ range: r as HistoryRange, ...unzoomed })} />
          {live ? (
            <span className="flex items-center gap-1.5 text-xs text-muted">
              <span className="relative flex size-2">
                <span className="absolute inline-flex size-full animate-ping rounded-full bg-success opacity-60" />
                <span className="relative inline-flex size-2 rounded-full bg-success" />
              </span>
              Up to now
            </span>
          ) : (
            <Button size="sm" variant="outline" onClick={() => set({ end: undefined, ...unzoomed })}>
              <Clock aria-hidden /> Back to now
            </Button>
          )}
          <label className="flex items-center gap-1.5 text-xs">
            <input type="checkbox" checked={band} onChange={(e) => set({ band: e.target.checked || undefined })} className="size-3.5 accent-[var(--accent)]" />
            Min–max band
          </label>
          <Button size="sm" variant="outline" className="xl:hidden" onClick={() => setPickerOpen(true)}>
            <SlidersHorizontal aria-hidden /> Series ({ids.length})
          </Button>
        </div>
      </div>

      <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,1fr)_20rem]">
        <Card className="flex flex-col">
          <CardHeader className="flex-wrap">
            <CardTitle>
              {formatDateTime(new Date(from).toISOString())} – {live ? 'now' : formatDateTime(new Date(to).toISOString())}
            </CardTitle>
            <div className="flex items-center gap-3">
              {series.data && <span className="text-xs text-muted">{describeBucket(series.data.bucketMs)}</span>}
              <Button
                size="sm"
                variant="outline"
                onClick={() => void navigate({ to: '/diagnostics', search: { from: Math.round(zoom?.from ?? from), to: Math.round(zoom?.to ?? to) } })}
                title="Analyze everything recorded in this period: CPU, memory, GPU, storage, network, processes, services, events and sensors"
              >
                <Stethoscope aria-hidden /> {zoom ? 'Diagnose the zoomed part' : 'Diagnose this period'}
              </Button>
            </div>
          </CardHeader>
          <CardContent>
            {ids.length === 0 ? (
              <p className="py-24 text-center text-sm text-muted">Choose series to chart.</p>
            ) : (
              <TimeSeriesChart
                result={series.data}
                catalog={catalog.data ?? []}
                markers={markers}
                band={band}
                zoom={zoom}
                onZoomChange={(z) => set(z ? { zoomFrom: z.from, zoomTo: z.to } : unzoomed)}
                onMarkerClick={(m) => m.id && void navigate({ to: '/incidents/$key', params: { key: m.id } })}
                onPick={(ts) => set({ at: ts })}
                label={`History of ${ids.length} series`}
                className="h-[26rem]"
              />
            )}
            {ids.length > 0 && search.at === undefined && <p className="mt-2 text-xs text-muted">Click the chart to see everything recorded at that moment, including the processes running.</p>}
            {RANGE_MS[search.range] > 86_400_000 && (
              <p className="mt-2 text-xs text-muted">Beyond the last 24 hours the chart uses one-minute summaries (minimum, average and maximum per minute).</p>
            )}
          </CardContent>
        </Card>

        {/* The list scrolls inside its card, beside the chart, so the chart's card never stretches to the list's length. */}
        <Card className="hidden xl:sticky xl:top-16 xl:flex xl:max-h-[calc(100vh-5rem)] xl:flex-col">
          <CardHeader>
            <CardTitle>Series</CardTitle>
          </CardHeader>
          <CardContent className="flex min-h-0 flex-1 flex-col">{picker}</CardContent>
        </Card>
      </div>

      {search.at !== undefined && (
        <Card>
          <CardContent className="pt-4">
            <MomentPanel
              at={search.at}
              actions={
                <Button size="sm" variant="ghost" onClick={() => set({ at: undefined })} aria-label="Close this moment">
                  <X aria-hidden /> Close
                </Button>
              }
            />
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader className="flex-wrap">
          <CardTitle>Events in this window</CardTitle>
          <Segmented
            label="Event importance"
            options={EVENT_FILTERS}
            value={minImportance}
            onChange={(v) => set({ events: v === '2' ? undefined : (Number(v) as 1 | 3) })}
          />
        </CardHeader>
        <CardContent>
          {!events.data || events.data.length === 0 ? (
            <p className="text-sm text-muted">No events of this importance in the window.</p>
          ) : (
            <DataTable
              viewKey="history.events"
              label="Events in the window"
              rows={events.data}
              columns={EVENT_COLUMNS}
              rowKey={(e) => `${e.kind}-${e.sourceId ?? e.ts}-${e.ts}`}
              defaultView={{ groupBy: [], sort: [{ id: 'time', desc: true }] }}
              fill
              listClassName="gap-1"
              compact
              renderRow={(e) => (
                <button
                  type="button"
                  onClick={() => centerOn(e.ts)}
                  className="grid w-full grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-x-2.5 rounded px-1.5 py-1 text-left hover:bg-surface-muted"
                  title="Center the chart on this moment"
                >
                  <span className={cn('size-2 rounded-full', importanceColor(e.importance))} aria-hidden />
                  <span className="truncate text-sm">{e.title}</span>
                  <span className="text-xs tabular-nums text-muted">
                    <time dateTime={new Date(e.ts).toISOString()}>{formatTime(new Date(e.ts).toISOString())}</time>
                  </span>
                </button>
              )}
            />
          )}
        </CardContent>
      </Card>

      {pickerOpen && (
        <div className="fixed inset-0 z-40 flex justify-end bg-black/50 xl:hidden" onMouseDown={(e) => e.target === e.currentTarget && setPickerOpen(false)}>
          <div role="dialog" aria-modal="true" aria-label="Choose series" className="flex h-full w-80 max-w-full flex-col gap-3 overflow-y-auto bg-surface p-4">
            <div className="flex items-center justify-between">
              <h2 className="flex items-center gap-2 font-semibold">
                <ListFilter className="size-4" aria-hidden /> Series
              </h2>
              <button type="button" aria-label="Close" onClick={() => setPickerOpen(false)} className="rounded p-1 text-muted hover:text-foreground">
                <X className="size-4" aria-hidden />
              </button>
            </div>
            {picker}
          </div>
        </div>
      )}
    </div>
  );
}

function describeBucket(ms: number): string {
  if (ms <= 1000) return 'every sample';
  if (ms < 60_000) return `${Math.round(ms / 1000)} s per point`;
  if (ms < 3_600_000) return `${Math.round(ms / 60_000)} min per point`;
  return `${(ms / 3_600_000).toFixed(1)} h per point`;
}

export function importanceColor(importance: number): string {
  return importance >= 4 ? 'bg-danger ring-2 ring-danger/40' : importance >= 3 ? 'bg-danger' : importance >= 2 ? 'bg-warning' : importance >= 1 ? 'bg-accent' : 'bg-muted';
}
