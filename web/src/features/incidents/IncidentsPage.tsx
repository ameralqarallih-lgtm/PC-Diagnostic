import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { ArrowRight, Pin, Search } from 'lucide-react';
import { useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Drawer } from '@/components/ui/drawer';
import { apiGet } from '@/lib/api/client';
import { INCIDENT_SOURCES, USER_INCIDENT_TYPES, type IncidentPage, type IncidentSummary, type Severity } from '@/lib/api/types';
import { formatDateTime } from '@/lib/format';
import { STATE_TONE } from './IncidentStatusCard';

const SEVERITY_RANK: Record<Severity, number> = { Info: 0, Low: 1, Medium: 2, High: 3, Critical: 4 };

/** What incidents can be grouped and sorted by. */
const INCIDENT_COLUMNS: Column<IncidentSummary>[] = [
  { id: 'when', header: 'When', value: (i) => Date.parse(i.centerAt), groupable: false, firstDesc: true },
  {
    id: 'day',
    header: 'Day',
    value: (i) => Date.parse(i.centerAt.slice(0, 10)),
    group: (i) => new Date(i.centerAt).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' }),
    sortable: false,
    firstDesc: true,
  },
  { id: 'type', header: 'Type', value: (i) => (i.userType ? (USER_INCIDENT_TYPES.find((t) => t.id === i.userType)?.label ?? i.userType) : null) },
  { id: 'source', header: 'Captured by', value: (i) => INCIDENT_SOURCES.find((s) => s.id === i.source)?.label ?? i.source },
  { id: 'severity', header: 'Severity', value: (i) => SEVERITY_RANK[i.severity], group: (i) => i.severity, firstDesc: true },
  { id: 'state', header: 'State', value: (i) => i.state },
  { id: 'pinned', header: 'Pinned', value: (i) => (i.pinned ? 'Pinned' : 'Not pinned'), firstDesc: false },
  { id: 'title', header: 'Title', value: (i) => i.title, groupable: false },
  { id: 'reports', header: 'Reports', value: (i) => i.triggerCount, groupable: false, firstDesc: true },
];

export const INCIDENT_PERIODS = [
  { id: 'all', label: 'Any time', ms: 0 },
  { id: '24h', label: 'Last 24 hours', ms: 86_400_000 },
  { id: '7d', label: 'Last 7 days', ms: 604_800_000 },
  { id: '30d', label: 'Last 30 days', ms: 2_592_000_000 },
] as const;

export interface IncidentsSearch {
  type?: string;
  source?: string;
  period?: string;
  q?: string;
}

/** Keeps only known filter values in the URL. */
export function parseIncidentsSearch(search: Record<string, unknown>): IncidentsSearch {
  const result: IncidentsSearch = {};
  if (USER_INCIDENT_TYPES.some((t) => t.id === search.type)) result.type = search.type as string;
  if (INCIDENT_SOURCES.some((s) => s.id === search.source)) result.source = search.source as string;
  if (INCIDENT_PERIODS.some((p) => p.id === search.period) && search.period !== 'all') result.period = search.period as string;
  if (typeof search.q === 'string' && search.q.trim()) result.q = search.q.trim().slice(0, 100);
  return result;
}

const PAGE = 50;

/** Every incident, filterable by what was reported, how it was captured, when, and text. */
export function IncidentsPage() {
  const search = useSearch({ from: '/incidents' });
  const navigate = useNavigate({ from: '/incidents' });
  const [limit, setLimit] = useState(PAGE);
  const [text, setText] = useState(search.q ?? '');
  const period = INCIDENT_PERIODS.find((p) => p.id === search.period) ?? INCIDENT_PERIODS[0];
  // An incident clicked opens in a drawer over the list, with the way into its investigation.
  const [peek, setPeek] = useState<IncidentSummary | null>(null);

  const params = new URLSearchParams({ limit: String(limit) });
  if (search.type) params.set('type', search.type);
  if (search.source) params.set('source', search.source);
  if (search.q) params.set('q', search.q);
  if (period.ms > 0) params.set('from', String(Math.floor((Date.now() - period.ms) / 60_000) * 60_000));
  const { data, error, isFetching } = useQuery({
    queryKey: ['incidents', 'list', search, limit],
    queryFn: ({ signal }) => apiGet<IncidentPage>(`/incidents?${params}`, signal),
    refetchInterval: 15_000,
  });

  const set = (patch: Partial<IncidentsSearch>) => {
    setLimit(PAGE);
    void navigate({ search: (s) => parseIncidentsSearch({ ...s, ...patch }), replace: true });
  };

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h1 className="text-xl font-semibold">Incidents</h1>
        {data && <span className="text-sm text-muted">{data.total} found</span>}
      </div>

      <form
        role="search"
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          set({ q: text });
        }}
      >
        <FilterSelect label="What happened" value={search.type ?? ''} onChange={(v) => set({ type: v || undefined })} options={USER_INCIDENT_TYPES.map((t) => ({ value: t.id, label: t.label }))} />
        <FilterSelect label="Captured by" value={search.source ?? ''} onChange={(v) => set({ source: v || undefined })} options={INCIDENT_SOURCES.map((s) => ({ value: s.id, label: s.label }))} />
        <FilterSelect label="When" value={period.id} onChange={(v) => set({ period: v })} options={INCIDENT_PERIODS.map((p) => ({ value: p.id, label: p.label }))} all={false} />
        <label className="flex min-w-48 flex-1 flex-col gap-1 text-xs text-muted">
          Title, notes or ID
          <span className="relative">
            <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2" aria-hidden />
            <input
              type="search"
              value={text}
              onChange={(e) => setText(e.target.value)}
              onBlur={() => text !== (search.q ?? '') && set({ q: text })}
              placeholder="e.g. flicker, INC-2026"
              className="h-9 w-full rounded-md border border-border bg-background pl-7 pr-2 text-sm text-foreground placeholder:text-muted"
            />
          </span>
        </label>
      </form>

      {error ? (
        <p className="text-sm text-danger">The incidents could not be loaded.</p>
      ) : !data ? (
        <p className="text-sm text-muted">Loading…</p>
      ) : data.items.length === 0 ? (
        <Card>
          <CardContent className="py-10 text-center text-sm text-muted">
            {Object.keys(search).length > 0 ? 'No incident matches these filters.' : 'No incidents yet. Capture one from the dashboard, the tray or the hotkey when something goes wrong.'}
          </CardContent>
        </Card>
      ) : (
        <DataTable
          viewKey="incidents"
          label="Incidents"
          rows={data.items}
          columns={INCIDENT_COLUMNS}
          rowKey={(i) => i.key}
          defaultView={{ groupBy: [], sort: [{ id: 'when', desc: true }] }}
          listClassName="gap-2"
          compact
          renderRow={(incident) => (
            <button type="button" className={ROW} onClick={() => setPeek(incident)}>
              <span className="flex min-w-0 items-center gap-2">
                {incident.pinned && <Pin className="size-3.5 shrink-0 text-accent" aria-label="Pinned" />}
                <span className="truncate font-medium">{incident.title}</span>
              </span>
              <Badge tone={STATE_TONE[incident.state]}>{incident.state}</Badge>
              <span className="col-span-2 flex flex-wrap gap-x-3 text-xs text-muted">
                <span className="font-mono">{incident.key}</span>
                <span>{formatDateTime(incident.centerAt)}</span>
                <span>{INCIDENT_SOURCES.find((s) => s.id === incident.source)?.label ?? incident.source}</span>
                {incident.triggerCount > 1 && <span>{incident.triggerCount} reports</span>}
                {incident.snapshotOnly && <span>no data from before</span>}
              </span>
              {incident.notes && <span className="col-span-2 line-clamp-2 text-sm text-muted">{incident.notes}</span>}
            </button>
          )}
        />
      )}

      {data && data.items.length < data.total && (
        <Button variant="outline" className="self-center" disabled={isFetching} onClick={() => setLimit((l) => l + PAGE)}>
          Show more
        </Button>
      )}

      <Drawer
        open={peek !== null}
        title={peek?.title ?? ''}
        onClose={() => setPeek(null)}
        footer={
          peek && (
            <>
              <Button asChild variant="outline">
                <Link to="/diagnostics" search={{ incidents: peek.key }}>
                  Diagnose this incident
                </Link>
              </Button>
              <Button asChild>
                <Link to="/incidents/$key" params={{ key: peek.key }}>
                  Investigate <ArrowRight aria-hidden />
                </Link>
              </Button>
            </>
          )
        }
      >
        {peek && (
          <dl className="grid grid-cols-[8rem_minmax(0,1fr)] gap-x-3 gap-y-2">
            <dt className="text-muted">State</dt>
            <dd>
              <Badge tone={STATE_TONE[peek.state]}>{peek.state}</Badge>
            </dd>
            <dt className="text-muted">ID</dt>
            <dd className="font-mono">{peek.key}</dd>
            <dt className="text-muted">When</dt>
            <dd>{formatDateTime(peek.centerAt)}</dd>
            <dt className="text-muted">Recorded</dt>
            <dd>{peek.snapshotOnly ? 'From the moment it was reported (no data from before)' : `${Math.round(peek.preSeconds / 60)} min before, ${Math.round(peek.postSeconds / 60)} min after`}</dd>
            <dt className="text-muted">Captured by</dt>
            <dd>{INCIDENT_SOURCES.find((s) => s.id === peek.source)?.label ?? peek.source}</dd>
            {peek.triggerCount > 1 && (
              <>
                <dt className="text-muted">Reports</dt>
                <dd>{peek.triggerCount}</dd>
              </>
            )}
            <dt className="text-muted">Pinned</dt>
            <dd>{peek.pinned ? 'Yes: kept whatever the space limit' : 'No'}</dd>
            {peek.notes && (
              <>
                <dt className="text-muted">Notes</dt>
                <dd className="whitespace-pre-wrap">{peek.notes}</dd>
              </>
            )}
          </dl>
        )}
      </Drawer>
    </div>
  );
}

const ROW = 'mb-2 grid w-full grid-cols-[minmax(0,1fr)_auto] gap-x-3 gap-y-1 rounded-lg border border-border bg-surface p-3 text-left transition-colors duration-[120ms] hover:border-accent/60';

function FilterSelect({
  label,
  value,
  onChange,
  options,
  all = true,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  options: { value: string; label: string }[];
  all?: boolean;
}) {
  return (
    <label className="flex flex-col gap-1 text-xs text-muted">
      {label}
      <select value={value} onChange={(e) => onChange(e.target.value)} className="h-9 rounded-md border border-border bg-background px-2 text-sm text-foreground">
        {all && <option value="">All</option>}
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </label>
  );
}
