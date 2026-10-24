import { useQuery } from '@tanstack/react-query';
import { Search } from 'lucide-react';
import { type ReactNode, type RefObject, useCallback, useMemo, useRef, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { ProcessTable } from '@/components/ProcessTable';
import { Badge } from '@/components/ui/badge';
import { Segmented } from '@/features/dashboard/LiveChartCard';
import { apiGet } from '@/lib/api/client';
import type { HistoryMoment, MomentService, MomentValue, ProcessAtTime } from '@/lib/api/types';
import { formatDateTime, formatTime, formatUnit } from '@/lib/format';
import { cn } from '@/lib/utils';

/** Group order for the values; groups not listed follow in the order they arrive. */
const GROUP_ORDER = ['System', 'CPU', 'Memory', 'GPU', 'Disk', 'Network', 'Hardware sensors', 'Recorder'];

/** The values' columns: grouped by area (in the order above), each value with its unit. */

const VALUE_COLUMNS: Column<MomentValue>[] = [

  {

    id: 'group',

    header: 'Area',

    value: (v) => (GROUP_ORDER.indexOf(v.group) === -1 ? GROUP_ORDER.length : GROUP_ORDER.indexOf(v.group)),

    group: (v) => v.group,

    hidden: true,

  },

  { id: 'label', header: 'Value', value: (v) => v.label, groupable: false, className: 'max-w-80 truncate text-muted' },

  { id: 'value', header: 'Reading', value: (v) => v.value, cell: (v) => formatUnit(v.value, v.unit), groupable: false, align: 'right', firstDesc: true },

  { id: 'unit', header: 'Unit', value: (v) => v.unit || null, hidden: true },

];


/** The moment's values for the request: whole seconds, so nearby clicks share a cached answer. */
export function useMoment(at: number) {
  const second = Math.round(at / 1000) * 1000;
  return useQuery({
    queryKey: ['history', 'moment', second],
    queryFn: ({ signal }) => apiGet<HistoryMoment>(`/history/moment?t=${second}`, signal),
    staleTime: 30_000,
  });
}

/**
 * Everything recorded about one moment, incident or not: every chartable value and hardware sensor around it, and
 * the processes running then (the busiest with their figures, every other one known from its start and end time).
 */
/** A part of the page beside the moment's own sections (the chart and the events on History), reached from the same bar. */
export interface ExtraSection {
  id: string;
  label: string;
  onSelect: () => void;
}

/** Scrolls an element to the top of whatever scrolls it (the page, or a window's body), smoothly unless motion is reduced. */
export function scrollToSection(el: HTMLElement | null) {
  if (!el?.scrollIntoView) return;
  const reduce = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  el.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' });
}

export function MomentPanel({ at, actions, extraSections = [], panelRef }: { at: number; actions?: ReactNode; extraSections?: ExtraSection[]; panelRef?: RefObject<HTMLElement | null> }) {
  const { data, isLoading, isError } = useMoment(at);
  const [tab, setTab] = useState('processes');
  const valuesRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const [processFilter, setProcessFilter] = useState('');
  const [serviceFilter, setServiceFilter] = useState('');

  // Services and the processes they run in, both ways.
  const byProcess = useMemo(() => {
    const map = new Map<number, MomentService[]>();
    for (const s of data?.services ?? []) {
      if (s.procKey != null) map.set(s.procKey, [...(map.get(s.procKey) ?? []), s]);
    }
    return map;
  }, [data]);
  const processes = useMemo(() => new Map((data?.processes ?? []).map((p) => [p.procKey, p] as const)), [data]);
  const running = (data?.services ?? []).filter((s) => s.status === 'Running').length;

  const measured = data?.processes.filter((p) => p.measured !== false).length ?? 0;
  const valueCount = data?.values.length ?? 0;
  const hasValues = !!data?.recorded && valueCount > 0;
  const hasList = !!data?.recorded && data.processes.length > 0;

  // The moment's sections (and the page's own), each a button on a bar that stays in sight.
  const go = useCallback((section: 'values' | 'processes' | 'services') => {
    if (section !== 'values') setTab(section);
    scrollToSection(section === 'values' ? valuesRef.current : listRef.current);
  }, []);
  const sections = useMemo(
    () => [
      ...(hasValues ? [{ id: 'values', label: 'Values', onSelect: () => go('values') }] : []),
      ...(hasList
        ? [
            { id: 'processes', label: `Processes (${data!.processes.length})`, onSelect: () => go('processes') },
            { id: 'services', label: `Services (${running} running)`, onSelect: () => go('services') },
          ]
        : []),
      ...extraSections,
    ],
    [hasValues, hasList, data, running, extraSections, go],
  );

  return (
    <section ref={panelRef} aria-label="This moment" className="flex scroll-mt-4 flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-base font-semibold">
          <time dateTime={new Date(at).toISOString()}>{formatDateTime(new Date(at).toISOString())}</time>
        </h2>
        {actions}
      </div>

      {sections.length > 1 && (
        <nav aria-label="Sections of this moment" className="sticky top-0 z-10 -mx-1 flex flex-wrap items-center gap-1.5 rounded-md border border-border bg-surface px-1.5 py-1 text-sm shadow-sm">
          <span className="px-1 text-xs text-muted">Go to</span>
          {sections.map((s) => (
            <button
              key={s.id}
              type="button"
              onClick={s.onSelect}
              className="rounded px-2 py-0.5 hover:bg-surface-muted focus-visible:outline focus-visible:outline-2 focus-visible:outline-accent"
            >
              {s.label}
            </button>
          ))}
        </nav>
      )}

      {isLoading && <p className="text-sm text-muted">Loading…</p>}
      {isError && <p className="text-sm text-danger">This moment could not be read.</p>}
      {data && !data.recorded && (
        <p className="text-sm text-muted">The recorder was not recording at this moment, so nothing was stored. Record everything (or a custom selection) to keep this kind of detail.</p>
      )}
      {data?.recorded && valueCount === 0 && data.processes.length === 0 && (
        <p className="text-sm text-muted">Nothing is stored for this moment yet: recorded data reaches the database a few seconds later.</p>
      )}

      {hasValues && (
        <div ref={valuesRef} className="flex scroll-mt-14 flex-col gap-2">
          <h3 className="text-sm font-medium">Values</h3>
          <DataTable
            viewKey="moment.values"
            label="Values at this moment"
            rows={data.values}
            columns={VALUE_COLUMNS}
            rowKey={(v) => v.id}
            defaultView={{ groupBy: ['group'], sort: [] }}
            fill
            compact
          />
        </div>
      )}

      {hasList && (
        <div ref={listRef} className="flex scroll-mt-14 flex-col gap-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <Segmented
              label="Processes or services"
              options={[
                { value: 'processes', label: `Processes (${data.processes.length})` },
                { value: 'services', label: `Services (${running} running)` },
              ]}
              value={tab}
              onChange={setTab}
            />
            {data.processSampleTs !== null && <span className="text-xs text-muted">Sample at {formatTime(new Date(data.processSampleTs).toISOString())}</span>}
          </div>
          {tab === 'processes' ? (
            <>
              <p className={cn('text-xs text-muted')}>
                {data.processes.length} processes were running; {measured} with figures. The busiest are measured at the moment itself, every other process
                from its latest figures (stored every 30 s when they change).
                {measured < data.processes.length && ' Processes without figures ran before every process was stored this way.'}
              </p>
              <ProcessTable
                processes={data.processes}
                filterable
                filter={processFilter}
                onFilterChange={setProcessFilter}
                services={byProcess}
                onShowServices={(p) => {
                  setServiceFilter(String(p.pid));
                  setTab('services');
                }}
              />
            </>
          ) : (
            <ServiceTable
              services={data.services ?? []}
              processes={processes}
              filter={serviceFilter}
              onFilterChange={setServiceFilter}
              onShowProcess={(p) => {
                setProcessFilter(String(p.pid));
                setTab('processes');
              }}
            />
          )}
        </div>
      )}
      {data?.recorded && valueCount > 0 && data.processes.length === 0 && (
        <p className="text-sm text-muted">Process lists are kept for the last 24 hours; older moments have only one-minute summaries of the values.</p>
      )}
    </section>
  );
}

/** Services at a moment, running first, each linked to the process it ran in (and filtered by name or that process's ID). */
function ServiceTable({
  services,
  processes,
  filter,
  onFilterChange,
  onShowProcess,
}: {
  services: MomentService[];
  processes: Map<number, ProcessAtTime>;
  filter: string;
  onFilterChange: (filter: string) => void;
  onShowProcess: (process: ProcessAtTime) => void;
}) {
  const needle = filter.trim().toLowerCase();
  const rows = needle
    ? services.filter((s) => s.name.toLowerCase().includes(needle) || s.displayName.toLowerCase().includes(needle) || String(s.pid ?? '') === needle)
    : services;
  if (services.length === 0) return <p className="text-sm text-muted">No service list was recorded for this moment.</p>;
  const host = (s: MomentService) => (s.procKey != null ? processes.get(s.procKey) : undefined);
  const columns: Column<MomentService>[] = [
    {
      id: 'service',
      header: 'Service',
      value: (s) => s.displayName,
      groupable: false,
      className: 'max-w-72 truncate',
      cell: (s) => (
        <span title={`${s.displayName} (${s.name})`}>
          {s.displayName} <span className="text-xs text-muted">{s.name}</span>
        </span>
      ),
    },
    { id: 'status', header: 'Status', value: (s) => s.status, cell: (s) => (s.status === 'Running' ? <Badge tone="success">Running</Badge> : <span className="text-muted">{s.status}</span>) },
    { id: 'startType', header: 'Start type', value: (s) => s.startType, className: 'text-muted' },
    {
      id: 'host',
      header: 'Runs in',
      value: (s) => host(s)?.name ?? (s.pid != null ? `PID ${s.pid}` : null),
      group: (s) => (host(s) ? `${host(s)!.name} (${host(s)!.pid})` : s.pid != null ? `PID ${s.pid}` : '(not running)'),
      cell: (s) => {
        const p = host(s);
        return p ? (
          <button type="button" className="text-accent hover:underline" onClick={() => onShowProcess(p)} title="Show this process">
            {p.name} <span className="font-mono text-xs">{p.pid}</span>
          </button>
        ) : s.pid != null ? (
          <span className="font-mono text-xs text-muted">PID {s.pid}</span>
        ) : (
          <span className="text-muted">—</span>
        );
      },
    },
  ];
  return (
    <DataTable
      viewKey="moment.services"
      label="Services"
      rows={rows}
      columns={columns}
      rowKey={(s) => s.name}
      defaultView={{ groupBy: ['status'], sort: [{ id: 'service', desc: false }] }}
      emptyText="No service matches."
      fill
      compact
      toolbarStart={
        <label className="flex min-w-48 items-center gap-2 rounded-md border border-border px-2 py-1 text-sm sm:max-w-72">
          <Search className="size-3.5 text-muted" aria-hidden />
          <input
            type="search"
            value={filter}
            onChange={(e) => onFilterChange(e.target.value)}
            placeholder="Filter by name or process ID"
            aria-label="Filter services"
            className="w-full bg-transparent outline-none"
          />
        </label>
      }
    />
  );
}
