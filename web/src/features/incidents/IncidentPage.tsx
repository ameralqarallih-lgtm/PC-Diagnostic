import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { Check, Maximize2, Pencil, Pin, PinOff, Trash2, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { EChart } from '@/components/charts/EChart';
import { ProcessTable } from '@/components/ProcessTable';
import { TimeSeriesChart, type ChartMarker } from '@/components/charts/TimeSeriesChart';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Dialog } from '@/components/ui/dialog';
import { useToast } from '@/components/ui/toast';
import { Segmented } from '@/features/dashboard/LiveChartCard';
import { importanceColor } from '@/features/history/HistoryPage';
import { SeriesPicker } from '@/features/history/SeriesPicker';
import { useCatalog, useSeries, type DataScope } from '@/features/history/useHistory';
import { SystemOverviewView } from '@/features/system/SystemOverviewView';
import { apiGet, apiRequest } from '@/lib/api/client';
import {
  type EvidenceLevel,
  type EvidenceLink,
  type FindingEntry,
  INCIDENT_SOURCES,
  type IncidentDetail,
  type ProcessesAtTime,
  type SeriesInfo,
  type SystemOverview,
  type TimelineEntry,
  USER_INCIDENT_TYPES,
  type UserIncidentType,
} from '@/lib/api/types';
import type { ChartOption } from '@/lib/charts/echarts';
import { useThemeColors } from '@/lib/charts/useThemeColors';
import { formatDateTime, formatTime } from '@/lib/format';
import { RichText } from '@/components/RichText';
import { cn } from '@/lib/utils';
import { DataTable, type Column } from '@/components/data/DataTable';
import { ExportMenu, INCIDENT_EXPORTS } from '@/components/ExportMenu';
import { TabUnderline } from '@/components/ui/tab-indicator';
import { STATE_TONE } from './IncidentStatusCard';

export interface IncidentSearch {
  /** The moment being looked at (Unix ms). */
  t?: number;
}

export function parseIncidentSearch(search: Record<string, unknown>): IncidentSearch {
  return typeof search.t === 'number' && Number.isFinite(search.t) && search.t > 0 ? { t: Math.round(search.t) } : {};
}

const INCIDENT_KEY = /^INC-\d{8}-\d{5}$/;

/** Timeline lanes, top to bottom. */
const LANES = [
  { kind: 'trigger', label: 'Reports' },
  { kind: 'anomaly', label: 'Anomalies' },
  { kind: 'windows-event', label: 'Windows' },
  { kind: 'display', label: 'Display' },
  { kind: 'process', label: 'Processes' },
  { kind: 'service', label: 'Services' },
  { kind: 'recorder', label: 'Recorder' },
] as const;

const FOCUS_MS = 30_000;
const DEFAULT_SERIES = ['cpu', 'memory', 'gpu', 'vram', 'disk', 'cpu.temp'];

type Tab = 'events' | 'processes' | 'findings' | 'system' | 'details';

/**
 * Timeline-first investigation. Clicking any timeline item (or the charts) picks that moment: the charts recenter
 * on it (±30 s), the process list shows that moment and the URL keeps it (?t=) so the view can be shared.
 */
const DETAIL_COLUMNS: Column<{ key: string; value: string | null }>[] = [
  { id: 'area', header: 'Area', value: (r) => (r.key.includes('.') ? r.key.slice(0, r.key.indexOf('.')) : 'general'), hidden: true },
  { id: 'key', header: 'Property', value: (r) => r.key, groupable: false, className: 'text-muted' },
  { id: 'value', header: 'Value', value: (r) => r.value, cell: (r) => r.value ?? '—', groupable: false, className: 'break-words' },
];

export function IncidentPage() {
  const { key } = useParams({ from: '/incidents/$key' });
  if (!INCIDENT_KEY.test(key)) {
    return (
      <div className="flex flex-col gap-2">
        <Link to="/incidents" className="text-sm text-accent hover:underline">
          ← All incidents
        </Link>
        <p className="text-sm text-muted">"{key}" is not a valid incident ID (expected INC-yyyyMMdd-nnnnn).</p>
      </div>
    );
  }
  return <Investigation incidentKey={key} />;
}

function Investigation({ incidentKey }: { incidentKey: string }) {
  const search = useSearch({ from: '/incidents/$key' });
  const navigate = useNavigate({ from: '/incidents/$key' });
  const scope = useMemo<DataScope>(() => ({ kind: 'incident', key: incidentKey }), [incidentKey]);
  const [tab, setTab] = useState<Tab>('events');
  const [focus, setFocus] = useState(search.t !== undefined);
  const [ids, setIds] = useState<string[] | null>(null);
  const [band, setBand] = useState(true);

  const detail = useQuery({
    queryKey: ['incident', incidentKey],
    queryFn: ({ signal }) => apiGet<IncidentDetail>(`/incidents/${incidentKey}`, signal),
    refetchInterval: (query) => (query.state.data?.summary.state === 'Recording' || query.state.data?.summary.state === 'Finalizing' ? 5_000 : false),
  });
  const timeline = useQuery({
    queryKey: ['incident', incidentKey, 'timeline', detail.data?.summary.state],
    queryFn: ({ signal }) => apiGet<TimelineEntry[]>(`/incidents/${incidentKey}/timeline`, signal),
    enabled: !!detail.data?.hasData,
  });
  const catalog = useCatalog(scope);
  const center = detail.data ? Date.parse(detail.data.summary.centerAt) : undefined;
  const selected = search.t ?? center;

  // Default series: the headline ones this incident has, plus GPU temperatures.
  const chosen = useMemo(() => {
    if (ids) return ids;
    const available = new Set((catalog.data ?? []).map((s) => s.id));
    const gpuTemps = (catalog.data ?? []).filter((s) => /^gpu\.\d+\.(hotspot|temp)$/.test(s.id)).map((s) => s.id).slice(0, 2);
    return [...DEFAULT_SERIES.filter((id) => available.has(id)), ...gpuTemps].slice(0, 12);
  }, [ids, catalog.data]);

  const windowFrom = focus && search.t !== undefined ? search.t - FOCUS_MS : detail.data?.from;
  const windowTo = focus && search.t !== undefined ? search.t + FOCUS_MS : detail.data?.to;
  const series = useSeries(scope, { ids: chosen, from: windowFrom, to: windowTo, points: 1200 });

  const pick = (ts: number) => {
    setFocus(true);
    void navigate({ search: { t: Math.round(ts) }, replace: true });
  };

  const markers = useMemo<ChartMarker[]>(() => {
    const list: ChartMarker[] = [];
    if (center !== undefined) list.push({ ts: center, label: 'Reported', tone: 'center' });
    if (search.t !== undefined) list.push({ ts: search.t, label: formatTime(new Date(search.t).toISOString()), tone: 'selected' });
    for (const e of timeline.data ?? []) if (e.importance >= 3) list.push({ ts: e.ts, label: e.title, tone: 'event' });
    return list;
  }, [center, search.t, timeline.data]);

  if (detail.isError) {
    return (
      <div className="flex flex-col gap-2">
        <Link to="/incidents" className="text-sm text-accent hover:underline">
          ← All incidents
        </Link>
        <p className="text-sm text-muted">Incident {incidentKey} does not exist (it may have been deleted).</p>
      </div>
    );
  }

  if (!detail.data) return <p className="text-sm text-muted">Loading {incidentKey}…</p>;
  const incident = detail.data;

  return (
    <div className="flex flex-col gap-4">
      <Link to="/incidents" className="text-sm text-accent hover:underline">
        ← All incidents
      </Link>

      <IncidentHeader detail={incident} />

      {!incident.hasData && <p className="rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-sm">The incident's folder is missing, so only its summary is available.</p>}

      <Card>
        <CardHeader className="flex-wrap">
          <CardTitle>Timeline</CardTitle>
          <span className="text-xs text-muted">Click an item or the band to look at that moment</span>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <TimelineBand entries={timeline.data ?? []} from={incident.from} to={incident.to} center={center} selected={search.t} onPick={pick} />
          <TimelineList entries={timeline.data ?? []} selected={search.t} onPick={pick} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex-wrap">
          <CardTitle>Charts {focus && search.t !== undefined ? `· ${formatTime(new Date(search.t).toISOString())} ±30 s` : '· whole incident'}</CardTitle>
          <div className="flex flex-wrap items-center gap-2">
            <label className="flex items-center gap-1.5 text-xs">
              <input type="checkbox" checked={band} onChange={(e) => setBand(e.target.checked)} className="size-3.5 accent-[var(--accent)]" />
              Min–max band
            </label>
            {focus && search.t !== undefined && (
              <Button size="sm" variant="outline" onClick={() => setFocus(false)}>
                <Maximize2 aria-hidden /> Whole incident
              </Button>
            )}
          </div>
        </CardHeader>
        <CardContent className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_16rem]">
          <TimeSeriesChart result={series.data} catalog={catalog.data ?? []} markers={markers} band={band} onPick={pick} label={`Charts for ${incidentKey}`} className="h-96" />
          <div className="max-h-96 overflow-y-auto">
            <SeriesPicker catalog={catalog.data ?? []} selected={chosen} onChange={setIds} />
          </div>
        </CardContent>
      </Card>

      <div role="tablist" aria-label="Incident details" className="flex flex-wrap gap-1 border-b border-border">
        {(
          [
            ['events', 'Windows events'],
            ['processes', 'Processes'],
            ['findings', 'Findings'],
            ['system', 'PC at the time'],
            ['details', 'Reports and details'],
          ] as [Tab, string][]
        ).map(([id, label]) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={tab === id}
            onClick={() => setTab(id)}
            className={cn('relative -mb-px border-b-2 border-transparent px-3 py-2 text-sm transition-colors', tab === id ? 'font-medium text-foreground' : 'text-muted hover:text-foreground')}
          >
            {tab === id && <TabUnderline group="incident-tabs" />}
            {label}
          </button>
        ))}
      </div>

      <div role="tabpanel">
        {tab === 'events' && <EventsTab entries={(timeline.data ?? []).filter((e) => e.kind === 'windows-event')} selected={search.t} onPick={pick} />}
        {tab === 'processes' && selected !== undefined && <ProcessesTab incidentKey={incidentKey} at={selected} />}
        {tab === 'findings' && (
          <FindingsTab
            incidentKey={incidentKey}
            detail={incident}
            catalog={catalog.data ?? []}
            onPick={pick}
            onShowSeries={(id) => setIds((current) => {
              const list = current ?? chosen;
              return list.includes(id) ? list : [...list, id].slice(-12);
            })}
          />
        )}
        {tab === 'system' && <SystemTab incidentKey={incidentKey} />}
        {tab === 'details' && <DetailsTab detail={incident} />}
      </div>
    </div>
  );
}

function useIncidentEdit(key: string) {
  const queryClient = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: (body: { title?: string; notes?: string; type?: UserIncidentType; pinned?: boolean }) => apiRequest('PATCH', `/incidents/${key}`, body),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['incident', key] });
      void queryClient.invalidateQueries({ queryKey: ['incidents'] });
    },
    onError: (error) => toast.show({ title: 'The incident could not be changed', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
}

function IncidentHeader({ detail }: { detail: IncidentDetail }) {
  const summary = detail.summary;
  const customItems = recordedItemCount(detail.info.recording_selection);
  const edit = useIncidentEdit(summary.key);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();
  const [editingTitle, setEditingTitle] = useState(false);
  const [title, setTitle] = useState(summary.title);
  const [notes, setNotes] = useState(summary.notes ?? '');
  const [confirmDelete, setConfirmDelete] = useState(false);

  useEffect(() => {
    setTitle(summary.title);
  }, [summary.title]);
  useEffect(() => {
    setNotes(summary.notes ?? '');
  }, [summary.notes]);

  const remove = useMutation({
    mutationFn: () => apiRequest('DELETE', `/incidents/${summary.key}`),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['incidents'] });
      toast.show({ title: `${summary.key} deleted`, tone: 'success' });
      void navigate({ to: '/incidents' });
    },
    onError: (error) => toast.show({ title: 'The incident could not be deleted', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });

  return (
    <Card>
      <CardContent className="flex flex-col gap-3 pt-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0 flex-1">
            {editingTitle ? (
              <form
                className="flex items-center gap-2"
                onSubmit={(e) => {
                  e.preventDefault();
                  if (title.trim() && title.trim() !== summary.title) edit.mutate({ title: title.trim() });
                  setEditingTitle(false);
                }}
              >
                <label className="sr-only" htmlFor="incident-title">
                  Title
                </label>
                <input
                  id="incident-title"
                  value={title}
                  maxLength={120}
                  onChange={(e) => setTitle(e.target.value)}
                  className="h-9 min-w-0 flex-1 rounded-md border border-border bg-background px-2 text-lg font-semibold"
                  autoFocus
                />
                <Button size="icon" type="submit" aria-label="Save title">
                  <Check aria-hidden />
                </Button>
                <Button size="icon" variant="ghost" type="button" aria-label="Cancel" onClick={() => setEditingTitle(false)}>
                  <X aria-hidden />
                </Button>
              </form>
            ) : (
              <h1 className="flex items-center gap-2 text-xl font-semibold">
                <span className="truncate">{summary.title}</span>
                <button type="button" aria-label="Edit title" className="rounded p-1 text-muted hover:text-foreground" onClick={() => setEditingTitle(true)}>
                  <Pencil className="size-4" aria-hidden />
                </button>
              </h1>
            )}
            <p className="mt-1 flex flex-wrap gap-x-3 text-sm text-muted">
              <span className="font-mono">{summary.key}</span>
              <span>{formatDateTime(summary.centerAt)}</span>
              <span>{INCIDENT_SOURCES.find((s) => s.id === summary.source)?.label ?? summary.source}</span>
              <span>
                {Math.round(summary.preSeconds / 60)} min before · {Math.round(summary.postSeconds / 60)} min after
              </span>
              {summary.snapshotOnly && <span>no data from before (the recorder was not running)</span>}
              {customItems !== null && (
                <span title="Recorded with a custom selection: data it left out was not stored, and the summary says nothing about it.">
                  custom recording ({customItems} items)
                </span>
              )}
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge tone={STATE_TONE[summary.state]}>{summary.state}</Badge>
            <label className="sr-only" htmlFor="incident-type">
              What happened
            </label>
            <select
              id="incident-type"
              value={summary.userType ?? ''}
              onChange={(e) => e.target.value && edit.mutate({ type: e.target.value as UserIncidentType })}
              className="h-8 rounded-md border border-border bg-background px-2 text-xs"
            >
              <option value="" disabled>
                What happened?
              </option>
              {USER_INCIDENT_TYPES.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.label}
                </option>
              ))}
            </select>
            <ExportMenu href={`/api/v1/incidents/${encodeURIComponent(summary.key)}/export`} items={INCIDENT_EXPORTS} keep disabled={!detail.hasData || summary.state === 'Recording' || summary.state === 'Finalizing'} />
            <Button size="sm" variant="outline" aria-pressed={summary.pinned} onClick={() => edit.mutate({ pinned: !summary.pinned })}>
              {summary.pinned ? <PinOff aria-hidden /> : <Pin aria-hidden />}
              {summary.pinned ? 'Unpin' : 'Pin'}
            </Button>
            <Button size="sm" variant="outline" className="hover:border-danger hover:text-danger" onClick={() => setConfirmDelete(true)}>
              <Trash2 aria-hidden /> Delete
            </Button>
          </div>
        </div>

        <div className="flex flex-col gap-1">
          <label htmlFor="incident-notes" className="text-xs text-muted">
            Notes
          </label>
          <textarea
            id="incident-notes"
            value={notes}
            maxLength={2000}
            rows={2}
            onChange={(e) => setNotes(e.target.value)}
            placeholder="What were you doing? What did you see?"
            className="rounded-md border border-border bg-background px-3 py-2 text-sm placeholder:text-muted"
          />
          {notes !== (summary.notes ?? '') && (
            <div className="flex gap-2">
              <Button size="sm" onClick={() => edit.mutate({ notes })} disabled={edit.isPending}>
                Save notes
              </Button>
              <Button size="sm" variant="ghost" onClick={() => setNotes(summary.notes ?? '')}>
                Discard
              </Button>
            </div>
          )}
        </div>
      </CardContent>

      <Dialog
        open={confirmDelete}
        title={`Delete ${summary.key}?`}
        description="The incident's folder and everything recorded for it are removed. This cannot be undone."
        onClose={() => setConfirmDelete(false)}
        footer={
          <>
            <Button variant="outline" onClick={() => setConfirmDelete(false)}>
              Keep it
            </Button>
            <Button variant="danger" disabled={remove.isPending} onClick={() => remove.mutate()}>
              <Trash2 aria-hidden /> Delete
            </Button>
          </>
        }
      >
        <p className="text-sm">{summary.title}</p>
      </Dialog>
    </Card>
  );
}

/** Timeline items on lanes along the incident window; click picks the moment. */
function TimelineBand({
  entries,
  from,
  to,
  center,
  selected,
  onPick,
}: {
  entries: TimelineEntry[];
  from: number;
  to: number;
  center: number | undefined;
  selected: number | undefined;
  onPick: (ts: number) => void;
}) {
  const colors = useThemeColors();
  const pickRef = useRef(onPick);
  pickRef.current = onPick;
  const option = useMemo<ChartOption>(() => {
    const lane = (kind: string) => Math.max(0, LANES.findIndex((l) => l.kind === kind));
    const color = (importance: number) => (importance >= 3 ? colors.danger : importance >= 2 ? colors.warning : importance >= 1 ? colors.accent : colors.muted);
    return {
      animation: false,
      grid: { left: 72, right: 12, top: 6, bottom: 22 },
      tooltip: { trigger: 'item', backgroundColor: colors.surface, borderColor: colors.border, textStyle: { color: colors.foreground, fontSize: 12 } },
      xAxis: { type: 'time', min: from, max: to, axisLabel: { color: colors.muted, fontSize: 10, hideOverlap: true }, axisLine: { lineStyle: { color: colors.border } }, splitLine: { show: false } },
      yAxis: {
        type: 'category',
        data: LANES.map((l) => l.label),
        inverse: true,
        axisLabel: { color: colors.muted, fontSize: 10 },
        axisTick: { show: false },
        axisLine: { show: false },
        splitLine: { show: true, lineStyle: { color: colors.border, opacity: 0.4 } },
      },
      series: [
        {
          type: 'scatter',
          symbolSize: (value: [number, number, number]) => 7 + value[2] * 2,
          data: entries.map((e) => ({ value: [e.ts, lane(e.kind), e.importance], name: e.title, itemStyle: { color: color(e.importance) } })),
          tooltip: { formatter: (p: { name: string; value: [number] }) => `${formatTime(new Date(p.value[0]).toISOString())}<br/>${escapeHtml(p.name)}` },
          markLine: {
            symbol: ['none', 'none'],
            silent: true,
            animation: false,
            label: { show: false },
            data: [
              ...(center !== undefined ? [{ xAxis: center, lineStyle: { color: colors.danger, type: 'dashed' as const } }] : []),
              ...(selected !== undefined ? [{ xAxis: selected, lineStyle: { color: colors.accent, width: 2, type: 'solid' as const } }] : []),
            ],
          },
        },
      ],
    } as ChartOption;
  }, [entries, from, to, center, selected, colors]);

  return (
    <EChart
      option={option}
      notMerge
      label="Incident timeline"
      className="h-44"
      onClick={(params) => {
        const value = (params as { value?: [number] }).value;
        if (value) onPick(value[0]);
      }}
      onReady={(chart) =>
        chart.getZr().on('click', (event: { offsetX: number; offsetY: number; target?: unknown }) => {
          if (event.target) return; // a point: handled by onClick
          const point = chart.convertFromPixel({ gridIndex: 0 }, [event.offsetX, event.offsetY]) as number[] | undefined;
          if (point && Number.isFinite(point[0])) pickRef.current(Math.round(point[0]!));
        })
      }
    />
  );
}

const IMPORTANCE = ['Everything', 'Notable', 'Warnings', 'Serious', 'Critical'];

/** What timeline entries can be grouped and sorted by. */
const ENTRY_COLUMNS: Column<TimelineEntry & { index: number }>[] = [
  { id: 'time', header: 'Time', value: (e) => e.ts, groupable: false },
  { id: 'kind', header: 'Lane', value: (e) => LANES.find((l) => l.kind === e.kind)?.label ?? e.kind },
  { id: 'importance', header: 'Importance', value: (e) => e.importance, group: (e) => IMPORTANCE[e.importance] ?? String(e.importance), firstDesc: true },
  { id: 'title', header: 'Title', value: (e) => e.title, groupable: false },
  { id: 'provider', header: 'Provider', value: (e) => (typeof e.detail?.provider === 'string' ? e.detail.provider : null) },
];

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
}

function TimelineList({ entries, selected, onPick }: { entries: TimelineEntry[]; selected: number | undefined; onPick: (ts: number) => void }) {
  const [minimum, setMinimum] = useState('1');
  const shown = entries.filter((e) => e.importance >= Number(minimum));
  const nearest = selected === undefined ? undefined : shown.reduce<TimelineEntry | undefined>((a, e) => (!a || Math.abs(e.ts - selected) < Math.abs(a.ts - selected) ? e : a), undefined);
  const selectedRef = useRef<HTMLButtonElement>(null);
  // A block body: browsers now return a Promise from scrollIntoView, which React would take for the clean-up.
  useEffect(() => {
    selectedRef.current?.scrollIntoView?.({ block: 'nearest' });
  }, [nearest]);

  return (
    <div className="flex flex-col gap-2">
      <Segmented
        label="Timeline importance"
        options={[
          { value: '0', label: 'Everything' },
          { value: '1', label: 'Notable' },
          { value: '2', label: 'Warnings' },
          { value: '3', label: 'Serious' },
        ]}
        value={minimum}
        onChange={setMinimum}
      />
      {shown.length === 0 ? (
        <p className="text-sm text-muted">Nothing at this importance.</p>
      ) : (
        <DataTable
          viewKey="incident.timeline"
          label="Timeline"
          rows={shown.map((e, index) => ({ ...e, index }))}
          columns={ENTRY_COLUMNS}
          rowKey={(e) => `${e.kind}-${e.sourceId ?? e.index}-${e.ts}`}
          defaultView={{ groupBy: [], sort: [{ id: 'time', desc: false }] }}
          fill
          listClassName="gap-0.5"
          compact
          renderRow={(e) => {
            const isSelected = nearest !== undefined && e.ts === nearest.ts && e.kind === nearest.kind && e.title === nearest.title;
            return (
              <button
                type="button"
                ref={isSelected ? selectedRef : undefined}
                onClick={() => onPick(e.ts)}
                aria-current={isSelected || undefined}
                className={cn(
                  'grid w-full grid-cols-[4.5rem_auto_minmax(0,1fr)] items-center gap-x-2.5 rounded px-1.5 py-1 text-left hover:bg-surface-muted',
                  isSelected && 'bg-accent/10 ring-1 ring-accent/40',
                )}
              >
                <time className="text-xs tabular-nums text-muted" dateTime={new Date(e.ts).toISOString()}>
                  {formatTime(new Date(e.ts).toISOString())}
                </time>
                <span className={cn('size-2 rounded-full', importanceColor(e.importance))} aria-hidden />
                <span className="truncate text-sm">
                  <span className="mr-1.5 text-xs text-muted">{LANES.find((l) => l.kind === e.kind)?.label ?? e.kind}</span>
                  {e.title}
                </span>
              </button>
            );
          }}
        />
      )}
    </div>
  );
}

function EventsTab({ entries, selected, onPick }: { entries: TimelineEntry[]; selected: number | undefined; onPick: (ts: number) => void }) {
  if (entries.length === 0) return <p className="text-sm text-muted">No classified Windows events in this incident's window.</p>;
  return (
    <DataTable
      viewKey="incident.events"
      label="Windows events"
      rows={entries.map((e, index) => ({ ...e, index }))}
      columns={ENTRY_COLUMNS.filter((c) => c.id !== 'kind')}
      rowKey={(e) => `${e.sourceId ?? e.index}-${e.ts}`}
      defaultView={{ groupBy: [], sort: [{ id: 'time', desc: false }] }}
      compact
      renderRow={(e) => (
        <button
          type="button"
          onClick={() => onPick(e.ts)}
          className={cn('mb-2 w-full rounded-md border border-border p-3 text-left hover:border-accent/60', selected !== undefined && Math.abs(selected - e.ts) < 1000 && 'border-accent')}
        >
          <span className="flex items-center gap-2 text-sm font-medium">
            <span className={cn('size-2 rounded-full', importanceColor(e.importance))} aria-hidden />
            {e.title}
          </span>
          <span className="mt-1 flex flex-wrap gap-x-3 text-xs text-muted">
            <time dateTime={new Date(e.ts).toISOString()}>{formatTime(new Date(e.ts).toISOString())}</time>
            {typeof e.detail?.provider === 'string' && <span>{e.detail.provider}</span>}
            {e.detail?.eventId != null && <span>Event {String(e.detail.eventId)}</span>}
            {typeof e.detail?.channel === 'string' && <span>{e.detail.channel}</span>}
            {typeof e.detail?.ruleId === 'string' && <span className="font-mono">{e.detail.ruleId}</span>}
          </span>
        </button>
      )}
    />
  );
}

function ProcessesTab({ incidentKey, at }: { incidentKey: string; at: number }) {
  const { data } = useQuery({
    queryKey: ['incident', incidentKey, 'processes', Math.round(at / 1000)],
    queryFn: ({ signal }) => apiGet<ProcessesAtTime>(`/incidents/${incidentKey}/processes?t=${Math.round(at)}`, signal),
  });

  if (!data) return <p className="text-sm text-muted">Loading…</p>;
  if (data.sampleTs === null) return <p className="text-sm text-muted">No process samples were recorded for this incident.</p>;
  return (
    <div className="flex flex-col gap-2">
      <p className="text-sm text-muted">
        {data.processes.length} processes at {formatTime(new Date(data.sampleTs).toISOString())} (the sample nearest the selected moment)
      </p>
      <ProcessTable processes={data.processes} filterable />
    </div>
  );
}

const LEVEL_TEXT: Record<EvidenceLevel, string> = {
  ConfirmedEvent: 'Confirmed event',
  Observation: 'Observation',
  Correlation: 'Correlation detected',
  PossibleExplanation: 'Possible explanation',
  NotProven: 'Not proven',
};

const LEVEL_TONE: Record<EvidenceLevel, 'neutral' | 'success' | 'warning' | 'danger' | 'accent'> = {
  ConfirmedEvent: 'danger',
  Observation: 'neutral',
  Correlation: 'accent',
  PossibleExplanation: 'warning',
  NotProven: 'neutral',
};

/** The summary's sections (PROJECT §19), in order. */
const SECTIONS: { title: string; levels: EvidenceLevel[]; hint: string }[] = [
  { title: 'Observations', levels: ['ConfirmedEvent', 'Observation'], hint: 'What was measured and what Windows logged, including what was not seen.' },
  { title: 'Possible correlations', levels: ['Correlation', 'PossibleExplanation'], hint: 'What happened close to the incident. Happening together does not mean one led to the other.' },
  { title: 'Not proven', levels: ['NotProven'], hint: 'What this data cannot establish.' },
];

/** What findings can be grouped and sorted by (within their evidence section). */
const FINDING_COLUMNS: Column<FindingEntry>[] = [
  { id: 'order', header: 'Order', value: (f) => f.ordinal, groupable: false },
  { id: 'level', header: 'Evidence', value: (f) => LEVEL_TEXT[f.level] ?? f.level },
  { id: 'links', header: 'Links', value: (f) => (f.evidence ?? []).length, groupable: false, firstDesc: true },
  { id: 'text', header: 'Statement', value: (f) => f.text, groupable: false },
];

function FindingsTab({
  incidentKey,
  detail,
  catalog,
  onPick,
  onShowSeries,
}: {
  incidentKey: string;
  detail: IncidentDetail;
  catalog: SeriesInfo[];
  onPick: (ts: number) => void;
  onShowSeries: (id: string) => void;
}) {
  const analyzed = detail.info.analyzed_utc;
  const { data } = useQuery({
    queryKey: ['incident', incidentKey, 'findings', analyzed],
    queryFn: ({ signal }) => apiGet<FindingEntry[]>(`/incidents/${incidentKey}/findings`, signal),
  });
  if (!data) return <p className="text-sm text-muted">Loading…</p>;
  if (data.length === 0) {
    return (
      <p className="text-sm text-muted">
        {analyzed
          ? 'The analysis found nothing to report.'
          : detail.summary.state === 'Recording' || detail.summary.state === 'Finalizing'
            ? 'The diagnostic summary is written when the incident is finished.'
            : 'Not analyzed yet. The recorder analyzes incidents when they finish, and older ones when it starts.'}
      </p>
    );
  }

  const follow = (link: EvidenceLink) => {
    if ((link.kind === 'metric' || link.kind === 'range') && link.sourceId && catalog.some((c) => c.id === link.sourceId)) onShowSeries(link.sourceId);
    if (link.kind !== 'range') onPick(link.ts);
  };

  return (
    <div className="flex flex-col gap-5">
      <p className="text-xs text-muted">
        Generated from the recorded data{analyzed ? ` on ${formatDateTime(analyzed)}` : ''}. Statements describe what was measured and when; they never claim a cause.
        Links show the data behind a statement.
      </p>
      {SECTIONS.map((section) => {
        const items = data.filter((f) => section.levels.includes(f.level));
        if (items.length === 0) return null;
        return (
          <section key={section.title} aria-label={section.title} className="flex flex-col gap-2">
            <header>
              <h3 className="text-xs font-semibold uppercase tracking-wide text-muted">{section.title}</h3>
              <p className="text-xs text-muted">{section.hint}</p>
            </header>
            <DataTable
              viewKey="incident.findings"
              label={section.title}
              rows={items}
              columns={FINDING_COLUMNS}
              rowKey={(f) => f.ordinal}
              defaultView={{ groupBy: [], sort: [{ id: 'order', desc: false }] }}
              compact
              renderRow={(f) => (
                <div className={cn('mb-2 rounded-md border border-border p-3', f.level === 'NotProven' && 'border-dashed')}>
                  <Badge tone={LEVEL_TONE[f.level] ?? 'neutral'}>{LEVEL_TEXT[f.level] ?? f.level}</Badge>
                  <RichText text={f.text} className="mt-1.5" />
                  {(f.evidence ?? []).length > 0 && (
                    <div className="mt-2 flex flex-wrap gap-1.5">
                      {(f.evidence ?? []).slice(0, 6).map((link, i) => (
                        <button
                          key={`${link.kind}-${link.sourceId ?? ''}-${link.ts}-${i}`}
                          type="button"
                          onClick={() => follow(link)}
                          className="rounded border border-border px-1.5 py-0.5 text-xs text-muted hover:border-accent hover:text-foreground"
                        >
                          {evidenceLabel(link, catalog)}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              )}
            />
          </section>
        );
      })}
    </div>
  );
}

function evidenceLabel(link: EvidenceLink, catalog: SeriesInfo[]): string {
  const time = formatTime(new Date(link.ts).toISOString());
  const series = link.sourceId ? (catalog.find((c) => c.id === link.sourceId)?.label ?? link.sourceId) : '';
  switch (link.kind) {
    case 'metric':
      return `${series} at ${time}`;
    case 'range':
      return `Chart ${series}`;
    case 'anomaly':
      return `Anomaly at ${time}`;
    case 'trigger':
      return `Trigger at ${time}`;
    default:
      return `${link.sourceId?.startsWith('WindowsEvents') ? 'Windows event' : 'Event'} at ${time}`;
  }
}

function SystemTab({ incidentKey }: { incidentKey: string }) {
  const { data } = useQuery({ queryKey: ['incident', incidentKey, 'system'], queryFn: ({ signal }) => apiGet<SystemOverview>(`/incidents/${incidentKey}/system`, signal) });
  return data ? <SystemOverviewView overview={data} /> : <p className="text-sm text-muted">Loading…</p>;
}

/** How many items the incident's custom recording selected; null when everything was recorded. */
function recordedItemCount(json: string | null | undefined): number | null {
  if (!json) return null;
  try {
    const items: unknown = JSON.parse(json);
    return Array.isArray(items) ? items.length : null;
  } catch {
    return null;
  }
}

function DetailsTab({ detail }: { detail: IncidentDetail }) {
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>Reports</CardTitle>
        </CardHeader>
        <CardContent>
          <DataTable
            viewKey="incident.reports"
            label="Reports"
            rows={detail.triggers.map((t, index) => ({ ...t, index }))}
            columns={[
              { id: 'time', header: 'Time', value: (t) => t.ts, groupable: false },
              { id: 'source', header: 'Captured by', value: (t) => INCIDENT_SOURCES.find((s) => s.id === t.source)?.label ?? t.source },
              { id: 'type', header: 'Type', value: (t) => (t.userType ? (USER_INCIDENT_TYPES.find((u) => u.id === t.userType)?.label ?? t.userType) : null) },
              { id: 'rule', header: 'Rule', value: (t) => t.ruleId },
            ]}
            rowKey={(t) => t.index}
            defaultView={{ groupBy: [], sort: [{ id: 'time', desc: false }] }}
            listClassName="gap-2"
            compact
            renderRow={(t) => (
              <div className="mb-2 text-sm">
                <span className="tabular-nums text-muted">{formatTime(new Date(t.ts).toISOString())}</span> · {INCIDENT_SOURCES.find((s) => s.id === t.source)?.label ?? t.source}
                {t.userType && ` · ${USER_INCIDENT_TYPES.find((u) => u.id === t.userType)?.label ?? t.userType}`}
                {t.detail && <span className="block text-xs text-muted">{t.detail}</span>}
                {t.note && <span className="block text-xs italic">&quot;{t.note}&quot;</span>}
              </div>
            )}
          />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Folder details</CardTitle>
        </CardHeader>
        <CardContent>
          <DataTable
            viewKey="incident.details"
            label="Folder details"
            rows={Object.entries(detail.info).map(([key, value]) => ({ key, value }))}
            columns={DETAIL_COLUMNS}
            rowKey={(r) => r.key}
            defaultView={{ groupBy: [], sort: [] }}
            compact
            emptyText="No details recorded."
          />
        </CardContent>
      </Card>
    </div>
  );
}
