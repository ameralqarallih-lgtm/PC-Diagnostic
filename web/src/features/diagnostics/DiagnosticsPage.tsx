import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { Activity, ArrowLeft, Loader2, RefreshCw, Siren, Square, Stethoscope, Trash2 } from 'lucide-react';
import { type FormEvent, type ReactNode, useEffect, useRef, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { measureValue } from '@/components/data/view';
import { ExportMenu, type ExportItem } from '@/components/ExportMenu';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { apiGet, apiPost, apiRequest } from '@/lib/api/client';
import type { DiagnosticJob, DiagnosticJobDetail, EvidenceLevel, IncidentPage, PeriodFinding, PeriodReport, PeriodSection, PeriodTable, Severity } from '@/lib/api/types';
import { formatDateTime } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { Marked, RichText } from '@/components/RichText';
import { cn } from '@/lib/utils';
import { historyFor, MAX_INCIDENTS, MAX_PERIOD_MS } from './search';

const PRESETS = [
  { label: 'Last hour', ms: 3_600_000 },
  { label: '6 hours', ms: 21_600_000 },
  { label: '24 hours', ms: 86_400_000 },
  { label: '7 days', ms: 604_800_000 },
];

const SEVERITIES: Severity[] = ['Critical', 'High', 'Medium', 'Low', 'Info'];
const SEVERITY_RANK: Record<Severity, number> = { Info: 0, Low: 1, Medium: 2, High: 3, Critical: 4 };

const SEVERITY_PILL: Record<Severity, string> = {
  Critical: 'bg-danger text-white',
  High: 'bg-danger/15 text-danger',
  Medium: 'bg-warning/15 text-warning',
  Low: 'bg-accent/15 text-accent',
  Info: 'bg-surface-muted text-muted',
};

const LEVEL: Record<EvidenceLevel, string> = {
  Observation: 'Observed',
  ConfirmedEvent: 'Logged event',
  PossibleExplanation: 'Possible explanation',
  Correlation: 'Correlation',
  NotProven: 'Not proven',
};

const STAGE: Record<DiagnosticJob['state'], string> = {
  Queued: 'Waiting for another diagnostic to finish',
  Reading: 'Reading everything recorded in the period',
  Analyzing: 'Analyzing',
  Completed: 'Completed',
  Failed: 'Failed',
  Cancelled: 'Cancelled',
};

const EXPORTS: readonly ExportItem[] = [
  { label: 'Report (HTML)', hint: 'One file that opens offline and prints well', query: 'format=html' },
  { label: 'Report (PDF)', hint: 'Printed by Microsoft Edge', query: 'format=pdf' },
  { label: 'Report (JSON)', hint: 'Every section, figure, table and finding', query: 'format=json' },
];

/** "2026-10-07T14:30" in local time, for a datetime-local input. */
function toInput(ms: number): string {
  const d = new Date(ms);
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function duration(minutes: number): string {
  if (minutes < 90) return `${Math.round(minutes)} min`;
  if (minutes < 2880) return `${Math.floor(minutes / 60)} h ${Math.round(minutes % 60)} min`;
  return `${Math.floor(minutes / 1440)} d ${Math.floor((minutes % 1440) / 60)} h`;
}

function elapsed(ms: number): string {
  const seconds = Math.max(0, Math.round(ms / 1000));
  return seconds < 60 ? `${seconds} s` : `${Math.floor(seconds / 60)} min ${seconds % 60} s`;
}

/** Recent diagnostics, polled while one runs. */
export function useDiagnosticJobs() {
  return useQuery({
    queryKey: ['diagnostics', 'jobs'],
    queryFn: ({ signal }) => apiGet<DiagnosticJob[]>('/diagnostics/jobs', signal),
    refetchInterval: (query) => (query.state.data?.some((j) => j.running) ? 2000 : 30_000),
  });
}

/**
 * The period diagnostic: everything recorded between two times analyzed aspect by aspect (recording, incidents, CPU,
 * memory, GPU, storage, network, processes, services, display, Windows events, sensors, anomalies) against the anomaly
 * rules, with findings ranked by severity. It runs in the background on the PC: leaving the page neither stops nor loses
 * it; the page follows it by its ID (?job=) and lists the running and recent ones. ?from=&to= starts one for a period.
 */
export function DiagnosticsPage() {
  const search = useSearch({ from: '/diagnostics' });
  const navigate = useNavigate({ from: '/diagnostics' });
  const queryClient = useQueryClient();
  const jobs = useDiagnosticJobs();

  const start = useMutation({
    mutationFn: (period: { from?: number; to?: number; incidents?: string[]; fresh: boolean }) => apiPost<DiagnosticJob>('/diagnostics/jobs', period),
    onSuccess: (job) => {
      queryClient.setQueryData<DiagnosticJobDetail>(['diagnostics', 'job', job.id], (current) => current ?? { job, report: null });
      void queryClient.invalidateQueries({ queryKey: ['diagnostics', 'jobs'] });
      void navigate({ search: { job: job.id }, replace: true });
    },
  });

  // A link with a period (History's "Diagnose this period") starts its diagnostic once; the server reuses a running or fresh one.
  const startedFor = useRef<string | null>(null);
  useEffect(() => {
    if (search.from === undefined || search.to === undefined) return;
    const key = `${search.from}-${search.to}`;
    if (startedFor.current === key) return;
    startedFor.current = key;
    start.mutate({ from: search.from, to: search.to, fresh: false });
  }, [search.from, search.to, start]);

  // A link with incidents (an incident's "Diagnose this incident") likewise.
  useEffect(() => {
    if (!search.incidents || startedFor.current === search.incidents) return;
    startedFor.current = search.incidents;
    start.mutate({ incidents: search.incidents.split(','), fresh: false });
  }, [search.incidents, start]);

  const run = (from: number, to: number, fresh = false) => start.mutate({ from: Math.round(from), to: Math.round(to), fresh });
  const runIncidents = (keys: string[], fresh = false) => start.mutate({ incidents: keys, fresh });

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="flex items-center gap-2 text-xl font-semibold">
          <Stethoscope className="size-5 text-accent" aria-hidden /> Period diagnostic
        </h1>
        {search.job && (
          <Link to="/diagnostics" search={{}} className="inline-flex items-center gap-1 text-sm text-accent hover:underline">
            <ArrowLeft className="size-4" aria-hidden /> All diagnostics
          </Link>
        )}
      </div>

      <PeriodChooser onRun={run} onRunIncidents={runIncidents} busy={start.isPending} />

      {start.error && <Problem error={start.error} />}

      {search.job ? (
        <JobView id={search.job} onRunAgain={(job) => (job.incidents?.length ? runIncidents(job.incidents, true) : run(Date.parse(job.from), Date.parse(job.to), true))} />
      ) : (
        <>
          {search.from === undefined && (jobs.data?.length ?? 0) === 0 && (
            <Card>
              <CardContent className="py-10 text-center text-sm text-muted">
                Choose a period above, or pick one in{' '}
                <Link to="/history" search={historyFor(Date.now() - 3_600_000, Date.now())} className="text-accent underline-offset-2 hover:underline">
                  History
                </Link>{' '}
                (zoom into the chart to narrow it) and choose <em>Diagnose this period</em>. Everything recorded in it is analyzed: recording
                coverage, incidents, CPU, memory, GPU, storage, network, processes, services, display, Windows events and hardware sensors. It
                runs in the background, so you can leave this page and come back to the report.
              </CardContent>
            </Card>
          )}
          {(jobs.data?.length ?? 0) > 0 && <RecentDiagnostics jobs={jobs.data!} />}
        </>
      )}
    </div>
  );
}

/** Why a diagnostic could not start or failed to load. */
function Problem({ error }: { error: unknown }) {
  return (
    <Card>
      <CardContent className="py-8 text-center text-sm text-danger" role="alert">
        {error instanceof Error ? error.message : 'The diagnostic failed.'}
      </CardContent>
    </Card>
  );
}

/** One diagnostic: its progress while it runs (with Cancel), then its report. */
function JobView({ id, onRunAgain }: { id: string; onRunAgain: (job: DiagnosticJob) => void }) {
  const queryClient = useQueryClient();
  const detail = useQuery({
    queryKey: ['diagnostics', 'job', id],
    queryFn: ({ signal }) => apiGet<DiagnosticJobDetail>(`/diagnostics/jobs/${id}`, signal),
    refetchInterval: (query) => (query.state.data?.job.running !== false ? 1000 : false),
    retry: false,
  });
  const job = detail.data?.job;
  const running = job?.running ?? false;
  const now = useNow(running, 1000);
  const wasRunning = useRef(false);
  useEffect(() => {
    // When it finishes, the list of recent diagnostics changes too.
    if (wasRunning.current && !running) void queryClient.invalidateQueries({ queryKey: ['diagnostics', 'jobs'] });
    wasRunning.current = running;
  }, [running, queryClient]);
  const cancel = useMutation({
    mutationFn: () => apiRequest('DELETE', `/diagnostics/jobs/${id}`),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['diagnostics'] }),
  });

  if (detail.error) return <Problem error={detail.error} />;
  if (!job) {
    return (
      <Card>
        <CardContent className="flex items-center justify-center gap-3 py-12 text-sm text-muted" role="status">
          <Loader2 className="size-5 animate-spin" aria-hidden /> Loading the diagnostic…
        </CardContent>
      </Card>
    );
  }

  const period = `${formatDateTime(job.from)} – ${formatDateTime(job.to)}`;
  if (job.running) {
    return (
      <Card>
        <CardContent className="flex flex-col items-center gap-3 py-10 text-center text-sm" role="status" aria-live="polite">
          <Loader2 className="size-6 animate-spin text-accent" aria-hidden />
          <span className="font-medium">
            {STAGE[job.state]}… <span className="text-muted">({elapsed(now - Date.parse(job.createdAt))})</span>
          </span>
          <span className="text-muted">{period}</span>
          <span className="max-w-md text-xs text-muted">You can leave this page: the diagnostic keeps running on this PC, and its report is here when you come back.</span>
          <Button type="button" variant="outline" size="sm" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
            <Square aria-hidden /> {cancel.isPending ? 'Cancelling…' : 'Cancel'}
          </Button>
        </CardContent>
      </Card>
    );
  }

  if (job.state !== 'Completed' || !detail.data?.report) {
    return (
      <Card>
        <CardContent className="flex flex-col items-center gap-3 py-10 text-center text-sm" role="alert">
          <span className="font-medium">
            {STAGE[job.state]}: {period}
          </span>
          {job.error && <span className="text-muted">{job.error}</span>}
          <Button type="button" size="sm" onClick={() => onRunAgain(job)}>
            <RefreshCw aria-hidden /> Run again
          </Button>
        </CardContent>
      </Card>
    );
  }

  return (
    <>
      <div className="flex flex-wrap items-center justify-end gap-2 print:hidden">
        <Button size="sm" variant="outline" onClick={() => onRunAgain(job)}>
          <RefreshCw aria-hidden /> Run again
        </Button>
        <ExportMenu href={`/api/v1/diagnostics/jobs/${job.id}/export`} items={EXPORTS} />
      </div>
      <Report report={detail.data.report} incidents={job.incidents ?? []} />
    </>
  );
}

function RecentDiagnostics({ jobs }: { jobs: DiagnosticJob[] }) {
  const confirm = useConfirm();
  const queryClient = useQueryClient();
  const navigate = useNavigate({ from: '/diagnostics' });
  const remove = useMutation({
    mutationFn: (id: string) => apiRequest('DELETE', `/diagnostics/jobs/${id}`),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['diagnostics', 'jobs'] }),
  });
  const columns: Column<DiagnosticJob>[] = [
    {
      id: 'period',
      header: 'Period',
      value: (j) => Date.parse(j.from),
      group: (j) => new Date(j.from).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }),
      cell: (j) => (
        <Link to="/diagnostics" search={{ job: j.id }} className="text-accent hover:underline">
          {formatDateTime(j.from)} – {formatDateTime(j.to)}
          {j.incidents?.length ? <span className="ml-1.5 text-xs text-muted">({j.incidents.length === 1 ? j.incidents[0] : `${j.incidents.length} incidents`})</span> : null}
        </Link>
      ),
      firstDesc: true,
    },
    {
      id: 'length',
      header: 'Length',
      value: (j) => (Date.parse(j.to) - Date.parse(j.from)) / 60_000,
      cell: (j) => duration((Date.parse(j.to) - Date.parse(j.from)) / 60_000),
      align: 'right',
      firstDesc: true,
    },
    {
      id: 'state',
      header: 'State',
      value: (j) => j.state,
      cell: (j) =>
        j.running ? (
          <span className="inline-flex items-center gap-1 text-accent">
            <Loader2 className="size-3.5 animate-spin" aria-hidden /> {STAGE[j.state]}
          </span>
        ) : (
          STAGE[j.state]
        ),
    },
    {
      id: 'findings',
      header: 'Findings',
      value: (j) => (j.findingsBySeverity ? SEVERITIES.reduce((sum, s, i) => sum + (j.findingsBySeverity![s] ?? 0) * 10 ** (4 - i), 0) : null),
      groupable: false,
      firstDesc: true,
      cell: (j) =>
        j.findingsBySeverity ? (
          <span className="flex flex-wrap gap-1">
            {SEVERITIES.filter((s) => s !== 'Info' && (j.findingsBySeverity![s] ?? 0) > 0).map((s) => (
              <span key={s} className={cn('rounded-full px-1.5 text-[11px] font-medium', SEVERITY_PILL[s])}>
                {j.findingsBySeverity![s]} {s.toLowerCase()}
              </span>
            ))}
          </span>
        ) : (
          '—'
        ),
    },
    {
      id: 'created',
      header: 'Run at',
      value: (j) => Date.parse(j.createdAt),
      cell: (j) => formatDateTime(j.createdAt),
      group: (j) => new Date(j.createdAt).toLocaleDateString(),
      firstDesc: true,
    },
    {
      id: 'actions',
      header: '',
      value: () => null,
      groupable: false,
      sortable: false,
      align: 'right',
      cell: (j) => (
        <Button
          type="button"
          size="sm"
          variant="ghost"
          aria-label={j.running ? `Cancel the diagnostic of ${formatDateTime(j.from)}` : `Remove the diagnostic of ${formatDateTime(j.from)}`}
          title={j.running ? 'Cancel' : 'Remove'}
          onClick={async () => (j.running || (await confirm({ title: `Delete the diagnostic of ${formatDateTime(j.from)}?`, description: 'Its report is deleted; the recorded data it looked at stays.' }))) && remove.mutate(j.id)}
        >
          {j.running ? <Square aria-hidden /> : <Trash2 aria-hidden />}
        </Button>
      ),
    },
  ];
  const running = jobs.find((j) => j.running);
  return (
    <Card>
      <CardHeader>
        <CardTitle>Recent diagnostics</CardTitle>
      </CardHeader>
      <CardContent>
        <DataTable
          viewKey="diagnostics.recent"
          label="Recent diagnostics"
          rows={jobs}
          columns={columns}
          rowKey={(j) => j.id}
          defaultView={{ groupBy: [], sort: [{ id: 'created', desc: true }] }}
          compact
        />
        <p className="mt-2 text-xs text-muted">
          Finished reports are kept on this PC (the {20} most recent).{' '}
          {running && (
            <button type="button" className="text-accent hover:underline" onClick={() => void navigate({ search: { job: running.id } })}>
              Follow the running diagnostic
            </button>
          )}
        </p>
      </CardContent>
    </Card>
  );
}

function PeriodChooser({ onRun, onRunIncidents, busy }: { onRun: (from: number, to: number) => void; onRunIncidents: (keys: string[]) => void; busy: boolean }) {
  const [mode, setMode] = useState<'period' | 'incidents'>('period');
  const [start, setStart] = useState(() => toInput(Date.now() - 3_600_000));
  const [end, setEnd] = useState(() => toInput(Date.now()));
  const startMs = new Date(start).getTime();
  const endMs = new Date(end).getTime();
  const problem =
    !Number.isFinite(startMs) || !Number.isFinite(endMs)
      ? 'Enter a start and an end.'
      : endMs - startMs < 60_000
        ? 'The end must be at least a minute after the start.'
        : endMs - startMs > MAX_PERIOD_MS
          ? 'A period can be at most 31 days long.'
          : null;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!problem) onRun(startMs, Math.min(endMs, Date.now()));
  };

  return (
    <Card className="print:hidden">
      <CardContent className="flex flex-col gap-3 pt-4">
        <div role="radiogroup" aria-label="Diagnose" className="flex w-fit gap-0.5 rounded-md bg-surface-muted p-0.5 text-sm">
          {(
            [
              ['period', 'A period'],
              ['incidents', 'Incidents'],
            ] as const
          ).map(([id, text]) => (
            <button key={id} type="button" role="radio" aria-checked={mode === id} onClick={() => setMode(id)} className={cn('rounded px-3 py-1', mode === id ? 'bg-surface font-medium shadow-sm' : 'text-muted hover:text-foreground')}>
              {text}
            </button>
          ))}
        </div>
        {mode === 'incidents' ? (
          <IncidentChooser onRun={onRunIncidents} busy={busy} />
        ) : (
          <>
        <form onSubmit={submit} className="flex flex-wrap items-end gap-3">
          <label className="flex flex-col gap-1 text-xs text-muted">
            From
            <input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} className="rounded-md border border-border bg-background px-2 py-1.5 text-sm text-foreground" />
          </label>
          <label className="flex flex-col gap-1 text-xs text-muted">
            To
            <input type="datetime-local" value={end} onChange={(e) => setEnd(e.target.value)} className="rounded-md border border-border bg-background px-2 py-1.5 text-sm text-foreground" />
          </label>
          <Button type="submit" disabled={problem !== null || busy}>
            {busy ? <Loader2 className="animate-spin" aria-hidden /> : <Stethoscope aria-hidden />} Run diagnostic
          </Button>
          <div className="flex flex-wrap gap-1.5" role="group" aria-label="Recent periods">
            {PRESETS.map((p) => (
              <Button key={p.label} type="button" size="sm" variant="ghost" disabled={busy} onClick={() => onRun(Date.now() - p.ms, Date.now())}>
                {p.label}
              </Button>
            ))}
          </div>
        </form>
        {problem && <p className="text-xs text-warning">{problem}</p>}
          </>
        )}
      </CardContent>
    </Card>
  );
}

/**
 * Incidents to diagnose together: the recent ones (searchable), checked one by one (at most 20). The diagnostic covers
 * the period of their recordings (an incident captured at one moment, a minute around it).
 */
function IncidentChooser({ onRun, busy }: { onRun: (keys: string[]) => void; busy: boolean }) {
  const [text, setText] = useState('');
  const [chosen, setChosen] = useState<string[]>([]);
  const list = useQuery({
    queryKey: ['incidents', 'diagnose', text],
    queryFn: ({ signal }) => apiGet<IncidentPage>(`/incidents?limit=50${text.trim() ? `&q=${encodeURIComponent(text.trim())}` : ''}`, signal),
  });
  const items = list.data?.items ?? [];
  const toggle = (key: string) => setChosen((c) => (c.includes(key) ? c.filter((k) => k !== key) : c.length >= MAX_INCIDENTS ? c : [...c, key]));
  const span = (() => {
    const picked = items.filter((i) => chosen.includes(i.key));
    if (picked.length !== chosen.length || picked.length === 0) return null;
    const from = Math.min(...picked.map((i) => Date.parse(i.centerAt) - i.preSeconds * 1000));
    const to = Math.max(...picked.map((i) => Date.parse(i.centerAt) + i.postSeconds * 1000));
    return to - from;
  })();
  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <input type="search" value={text} onChange={(e) => setText(e.target.value)} placeholder="Find incidents by title, notes or ID" aria-label="Find incidents" className="h-9 min-w-56 flex-1 rounded-md border border-border bg-background px-2 text-sm" />
        <Button type="button" disabled={chosen.length === 0 || busy || (span !== null && span > MAX_PERIOD_MS)} onClick={() => onRun(chosen)}>
          {busy ? <Loader2 className="animate-spin" aria-hidden /> : <Stethoscope aria-hidden />} Diagnose {chosen.length === 1 ? 'this incident' : `${chosen.length || ''} incidents`}
        </Button>
        {chosen.length > 0 && (
          <Button type="button" size="sm" variant="ghost" onClick={() => setChosen([])}>
            Clear
          </Button>
        )}
      </div>
      {span !== null && span > MAX_PERIOD_MS && <p className="text-xs text-warning">These incidents are more than 31 days apart: choose incidents closer together.</p>}
      <ul aria-label="Incidents to diagnose" className="flex max-h-72 flex-col divide-y divide-border overflow-y-auto rounded-md border border-border">
        {list.isLoading ? (
          <li className="px-3 py-2 text-sm text-muted">Loading…</li>
        ) : items.length === 0 ? (
          <li className="px-3 py-2 text-sm text-muted">No incidents{text.trim() ? ` match “${text.trim()}”` : ' yet'}.</li>
        ) : (
          items.map((i) => (
            <li key={i.key}>
              <label className={cn('flex cursor-pointer items-center gap-3 px-3 py-2 text-sm hover:bg-surface-muted', chosen.includes(i.key) && 'bg-accent/10')}>
                <input type="checkbox" checked={chosen.includes(i.key)} onChange={() => toggle(i.key)} disabled={!chosen.includes(i.key) && chosen.length >= MAX_INCIDENTS} className="accent-[var(--accent)]" />
                <span className="min-w-0 flex-1 truncate">{i.title}</span>
                <span className="shrink-0 font-mono text-xs text-muted">{i.key}</span>
                <span className="shrink-0 text-xs text-muted">{formatDateTime(i.centerAt)}</span>
              </label>
            </li>
          ))
        )}
      </ul>
      <p className="text-xs text-muted">The diagnostic covers the period of the chosen incidents’ recordings; at most {MAX_INCIDENTS}.</p>
    </div>
  );
}

function Report({ report, incidents = [] }: { report: PeriodReport; incidents?: string[] }) {
  const from = Date.parse(report.from);
  const to = Date.parse(report.to);
  const s = report.summary;
  const titles = new Map(report.sections.map((x) => [x.id, x.title]));
  const recordedShare = s.periodMinutes > 0 ? Math.round((100 * s.recordedMinutes) / s.periodMinutes) : 0;

  return (
    <>
      <Card>
        <CardHeader className="flex-wrap">
          <CardTitle>
            {formatDateTime(report.from)} – {formatDateTime(report.to)}
          </CardTitle>
          <span className="text-xs text-muted">
            {duration(s.periodMinutes)}, {recordedShare} % recorded · generated {formatDateTime(report.generatedAt)}
          </span>
          {incidents.length > 0 && (
            <p className="flex basis-full flex-wrap items-center gap-1.5 text-xs" aria-label="Incidents diagnosed">
              <span className="text-muted">The period of</span>
              {incidents.map((key) => (
                <Link key={key} to="/incidents/$key" params={{ key }} search={{}} className="inline-flex items-center gap-1 rounded-full border border-border px-2 py-0.5 font-mono text-accent hover:bg-surface-muted">
                  <Siren className="size-3" aria-hidden /> {key}
                </Link>
              ))}
            </p>
          )}
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <ul className="flex flex-wrap gap-2" aria-label="Findings by severity">
            {SEVERITIES.map((severity) => (
              <li key={severity} className={cn('rounded-full px-2.5 py-0.5 text-xs font-medium', SEVERITY_PILL[severity], !s.findingsBySeverity[severity] && 'opacity-50')}>
                {s.findingsBySeverity[severity] ?? 0} {severity.toLowerCase()}
              </li>
            ))}
          </ul>
          {s.note && <RichText text={s.note} className="rounded-md border-l-2 border-accent bg-surface-muted px-3 py-2 text-xs" />}
          <h2 className="text-sm font-semibold">Most important findings</h2>
          {s.top.length === 0 ? (
            <p className="text-sm text-muted">Nothing in this period crossed a threshold or was logged as a problem.</p>
          ) : (
            <Findings viewKey="diagnostics.top" label="Most important findings" findings={s.top} sections={titles} />
          )}
        </CardContent>
      </Card>

      <nav aria-label="Report sections" className="flex flex-wrap gap-1.5 print:hidden">
        {report.sections.map((x) => {
          const count = x.findings.filter((f) => f.severity !== 'Info').length;
          return (
            <a key={x.id} href={`#section-${x.id}`} className="rounded-full border border-border px-2.5 py-0.5 text-xs hover:bg-surface-muted">
              {x.title}
              {count > 0 && <span className="ml-1.5 font-semibold text-warning">{count}</span>}
            </a>
          );
        })}
      </nav>

      {report.sections.map((x) => (
        <SectionCard key={x.id} section={x} from={from} to={to} />
      ))}
    </>
  );
}

type IndexedFinding = PeriodFinding & { index: number };

/** Findings, sorted by severity by default and groupable by severity, evidence level or section. */
function Findings({ viewKey, label, findings, sections }: { viewKey: string; label: string; findings: PeriodFinding[]; sections?: Map<string, string> }) {
  const columns: Column<IndexedFinding>[] = [
    { id: 'severity', header: 'Severity', value: (f) => SEVERITY_RANK[f.severity], group: (f) => f.severity, firstDesc: true },
    { id: 'occurrences', header: 'Times', value: (f) => f.occurrences, groupable: false, firstDesc: true },
    { id: 'title', header: 'Title', value: (f) => f.title, groupable: false },
    { id: 'level', header: 'Evidence', value: (f) => LEVEL[f.level] },
    { id: 'when', header: 'When', value: (f) => (f.from ? Date.parse(f.from) : null), groupable: false },
  ];
  if (sections) columns.push({ id: 'section', header: 'Section', value: (f) => (f.section ? (sections.get(f.section) ?? f.section) : null) });
  return (
    <DataTable
      viewKey={viewKey}
      label={label}
      rows={findings.map((f, index) => ({ ...f, index }))}
      columns={columns}
      rowKey={(f) => f.index}
      defaultView={{ groupBy: [], sort: [{ id: 'severity', desc: true }, { id: 'occurrences', desc: true }] }}
      renderRow={(f) => <FindingItem finding={f} section={f.section && sections ? { id: f.section, title: sections.get(f.section) ?? f.section } : undefined} />}
      listClassName="divide-y divide-border"
      compact
    />
  );
}

function SectionCard({ section, from, to }: { section: PeriodSection; from: number; to: number }) {
  const notable = section.findings.some((f) => f.severity !== 'Info');
  return (
    <Card id={`section-${section.id}`} className="scroll-mt-20">
      <details open={notable || section.findings.length > 0} className="group">
        <summary className="flex cursor-pointer list-none items-center justify-between gap-3 px-4 py-3 [&::-webkit-details-marker]:hidden">
          <h2 className="text-sm font-semibold">{section.title}</h2>
          <span className="text-xs text-muted">
            {section.findings.length === 0 ? 'nothing stood out' : `${section.findings.length} finding${section.findings.length === 1 ? '' : 's'}`}
            <span className="ml-2 inline-block transition-transform group-open:rotate-90" aria-hidden>
              ›
            </span>
          </span>
        </summary>
        <div className="flex flex-col gap-4 px-4 pb-4">
          {section.stats.length > 0 && (
            <dl className="grid gap-x-4 gap-y-1 text-sm sm:grid-cols-[minmax(10rem,max-content)_minmax(0,1fr)]">
              {section.stats.map((stat) => (
                <div key={stat.label} className="contents">
                  <dt className="text-muted">{stat.label}</dt>
                  <dd className="min-w-0 break-words">
                    <Marked text={stat.value} />
                    {stat.seriesId && (
                      <Link to="/history" search={historyFor(from, to, [stat.seriesId])} className="ml-2 text-xs text-accent hover:underline print:hidden">
                        chart
                      </Link>
                    )}
                  </dd>
                </div>
              ))}
            </dl>
          )}
          {section.findings.length > 0 && <Findings viewKey="diagnostics.findings" label={`${section.title} findings`} findings={section.findings} />}
          {section.tables.map((table) => (
            <ReportTable key={table.title} section={section.id} table={table} />
          ))}
        </div>
      </details>
    </Card>
  );
}

interface TableRow {
  cells: string[];
  index: number;
}

/** A report table: in the report's order by default; every column sorts by its figure and groups by its text. */
function ReportTable({ section, table }: { section: string; table: PeriodTable }) {
  const columns: Column<TableRow>[] = table.columns.map((name, c) => ({
    id: `c${c}`,
    header: name,
    value: (row) => measureValue(row.cells[c]),
    group: (row) => row.cells[c] || '(none)',
    cell: (row): ReactNode => row.cells[c],
    className: c === 0 ? 'break-all' : 'whitespace-nowrap',
    align: c > 0 && typeof measureValue(table.rows[0]?.[c]) === 'number' ? 'right' : 'left',
    firstDesc: c > 0,
  }));
  return (
    <div className="flex flex-col gap-1.5">
      <h3 className="text-xs font-medium uppercase tracking-wide text-muted">{table.title}</h3>
      <DataTable
        viewKey={`diagnostics.${section}.${table.title}`}
        label={table.title}
        rows={table.rows.map((cells, index) => ({ cells, index }))}
        columns={columns}
        rowKey={(row) => row.index}
        defaultView={{ groupBy: [], sort: [] }}
        compact
      />
    </div>
  );
}

function FindingItem({ finding: f, section }: { finding: PeriodFinding; section?: { id: string; title: string } }) {
  const start = f.from ? Date.parse(f.from) : undefined;
  const end = f.to ? Date.parse(f.to) : start;
  return (
    <div className="flex flex-col gap-1 py-2.5">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
        <span className={cn('rounded-full px-2 py-0.5 text-[11px] font-semibold', SEVERITY_PILL[f.severity])}>{f.severity}</span>
        <span className="text-sm font-medium">{f.title}</span>
        {f.occurrences > 1 && <span className="text-xs text-muted">×{f.occurrences.toLocaleString()}</span>}
        {section && (
          <a href={`#section-${section.id}`} className="text-xs text-muted hover:text-foreground hover:underline">
            · {section.title}
          </a>
        )}
      </div>
      <RichText text={f.text} prefix={<span className="mr-1.5 rounded border border-border px-1.5 text-[11px] text-muted">{LEVEL[f.level]}</span>} />
      {(start !== undefined || f.incidentKey) && (
        <div className="flex flex-wrap gap-3 text-xs print:hidden">
          {start !== undefined && end !== undefined && (
            <Link to="/history" search={historyFor(start, end, f.seriesIds, start)} className="inline-flex items-center gap-1 text-accent hover:underline">
              <Activity className="size-3.5" aria-hidden /> Show in History
            </Link>
          )}
          {f.incidentKey && (
            <Link to="/incidents/$key" params={{ key: f.incidentKey }} search={{}} className="inline-flex items-center gap-1 text-accent hover:underline">
              <Siren className="size-3.5" aria-hidden /> Open {f.incidentKey}
            </Link>
          )}
        </div>
      )}
    </div>
  );
}
