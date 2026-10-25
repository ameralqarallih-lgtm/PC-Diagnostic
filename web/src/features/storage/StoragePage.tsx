import { Link } from '@tanstack/react-router';
import { Database, HardDrive, Pin, PinOff, Sparkles, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { ConfirmAction, type PendingAction } from '@/features/tuneup/ConfirmAction';
import { INCIDENT_SOURCES, USER_INCIDENT_TYPES, type StorageAction, type StorageActionKind, type StorageIncident, type StorageKindId, type StorageKindUsage, type StorageResponse } from '@/lib/api/types';
import { formatBytes } from '@/lib/format';
import { cn } from '@/lib/utils';
import { LayoutBackups, WhereFilesAreKept } from './FileLocations';
import { useCleanStorage, useDeleteIncidents, usePinIncident, useSetSpaceLimit, useStorage } from './useStorage';

const GB = 1024 ** 3;

/** What each kind of data is, and how it is kept. */
const KINDS: Record<StorageKindId, { name: string; color: string; kept: (r: StorageResponse) => string; automatic: boolean }> = {
  incidents: {
    name: 'Incidents',
    color: 'var(--accent)',
    kept: (r) => (r.limits.maxIncidents > 0 ? `Until you delete them, at most ${r.limits.maxIncidents} (pinned ones always stay)` : 'Until you delete them (or the space limit needs room); pinned ones always stay'),
    automatic: false,
  },
  telemetry: {
    name: 'Recording (full detail and summaries)',
    color: '#a78bfa',
    kept: (r) => `Full detail ${r.limits.telemetryRetentionHours} h, one-minute summaries ${r.limits.rollupRetentionDays} days`,
    automatic: true,
  },
  index: { name: 'Incident list', color: '#f472b6', kept: () => 'With the incidents', automatic: false },
  logs: { name: 'Logs', color: '#fbbf24', kept: () => 'The newest 14 files of each program, automatically', automatic: true },
  diagnostics: { name: 'Diagnoses', color: '#34d399', kept: () => 'The 20 most recent, automatically', automatic: true },
  reports: { name: 'Problem reports', color: '#fb923c', kept: () => 'Until sent', automatic: true },
  characters: {
    name: 'Character generator (its AI models, its Python and the characters made)',
    color: '#22d3ee',
    kept: () => 'Until you remove them; not counted in the space limit',
    automatic: false,
  },
  other: { name: 'Settings and layouts', color: '#94a3b8', kept: () => 'Always', automatic: false },
};

const ACTIONS: Record<StorageActionKind, string> = {
  'incidents-removed': 'Incidents removed',
  'full-detail-shortened': 'Full detail shortened',
  'summaries-shortened': 'Summaries shortened',
  compacted: 'Database compacted',
  'writes-paused': 'Recording to disk paused',
  'writes-resumed': 'Recording to disk resumed',
};

const SEVERITY_RANK = { Info: 0, Low: 1, Medium: 2, High: 3, Critical: 4 } as const;

interface KindRow extends StorageKindUsage {
  name: string;
  color: string;
  kept: string;
  automatic: boolean;
  share: number;
}

function kindColumns(): Column<KindRow>[] {
  return [
    { id: 'name', header: 'What', value: (k) => k.name, groupable: false },
    { id: 'size', header: 'Size', value: (k) => k.bytes, groupable: false, firstDesc: true },
    { id: 'files', header: 'Files', value: (k) => k.files, groupable: false, firstDesc: true },
    { id: 'kept', header: 'Removed', value: (k) => (k.automatic ? 'Automatically' : 'By you (or the limits)') },
  ];
}

const INCIDENT_COLUMNS: Column<StorageIncident>[] = [
  { id: 'size', header: 'Size', value: (i) => i.bytes, groupable: false, firstDesc: true },
  { id: 'when', header: 'When', value: (i) => Date.parse(i.centerAt), groupable: false, firstDesc: true },
  {
    id: 'month',
    header: 'Month',
    value: (i) => Date.parse(i.centerAt.slice(0, 7) + '-01'),
    group: (i) => new Date(i.centerAt).toLocaleDateString(undefined, { month: 'long', year: 'numeric' }),
    sortable: false,
    firstDesc: true,
  },
  { id: 'type', header: 'Type', value: (i) => (i.userType ? (USER_INCIDENT_TYPES.find((t) => t.id === i.userType)?.label ?? i.userType) : null) },
  { id: 'source', header: 'Captured by', value: (i) => INCIDENT_SOURCES.find((s) => s.id === i.source)?.label ?? i.source },
  { id: 'severity', header: 'Severity', value: (i) => SEVERITY_RANK[i.severity], group: (i) => i.severity, firstDesc: true },
  { id: 'pinned', header: 'Pinned', value: (i) => (i.pinned ? 'Pinned' : 'Not pinned') },
  { id: 'title', header: 'Title', value: (i) => i.title, groupable: false },
];

const ACTION_COLUMNS: Column<StorageAction>[] = [
  { id: 'when', header: 'When', value: (a) => Date.parse(a.at), groupable: false, firstDesc: true },
  { id: 'kind', header: 'What', value: (a) => ACTIONS[a.kind] ?? a.kind },
  { id: 'freed', header: 'Given back', value: (a) => a.freedBytes, groupable: false, firstDesc: true },
  { id: 'by', header: 'By', value: (a) => (a.automatic ? 'The recorder' : 'You') },
];

/** "23 h 40 min", "12 days". */
function span(fromIso: string | null | undefined): string | null {
  if (!fromIso) return null;
  const minutes = Math.max(0, (Date.now() - Date.parse(fromIso)) / 60_000);
  if (minutes >= 2 * 1440) return `${Math.round(minutes / 1440)} days`;
  if (minutes >= 90) return `${Math.floor(minutes / 60)} h ${Math.round(minutes % 60)} min`;
  return `${Math.round(minutes)} min`;
}

const when = (iso: string) => new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

/**
 * The storage manager: what PC Diagnostic keeps on this PC and how much space each part uses, the limits that keep
 * it in check (applied by the recorder every 10 minutes), the incidents by size to delete or pin, and what was
 * cleaned up lately.
 */
export function StoragePage() {
  const storage = useStorage();
  const clean = useCleanStorage();
  const remove = useDeleteIncidents();
  const pin = usePinIncident();
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [pending, setPending] = useState<PendingAction | null>(null);
  const [editingLimit, setEditingLimit] = useState(false);
  const data = storage.data;

  const kinds = useMemo<KindRow[]>(
    () =>
      (data?.kinds ?? []).map((k) => {
        const kind = KINDS[k.id] ?? KINDS.other;
        return { ...k, name: kind.name, color: kind.color, kept: data ? kind.kept(data) : '', automatic: kind.automatic, share: data && data.totalBytes > 0 ? k.bytes / data.totalBytes : 0 };
      }),
    [data],
  );

  if (storage.error) return <p className="text-sm text-danger">The storage could not be measured.</p>;
  if (!data) return <p className="text-sm text-muted">Measuring…</p>;

  const incidents = data.incidents;
  const chosen = incidents.filter((i) => selected.has(i.key));
  const chosenBytes = chosen.reduce((sum, i) => sum + i.bytes, 0);
  const pinned = incidents.filter((i) => i.pinned).length;
  const recorder = data.recorder;
  const toggle = (key: string, on: boolean) =>
    setSelected((s) => {
      const next = new Set(s);
      if (on) next.add(key);
      else next.delete(key);
      return next;
    });

  const askDelete = () =>
    setPending({
      title: chosen.length === 1 ? `Delete ${chosen[0]!.key}?` : `Delete ${chosen.length} incidents?`,
      confirmLabel: 'Delete',
      danger: true,
      body: (
        <>
          <p>
            {formatBytes(chosenBytes)} go back to the drive. Deleted incidents cannot be brought back{chosen.some((i) => i.pinned) ? ', and pinned ones are among them' : ''}.
          </p>
          <ul className="max-h-48 list-disc overflow-auto pl-5 text-xs">
            {chosen.slice(0, 50).map((i) => (
              <li key={i.key}>
                {i.key} · {i.title} · {formatBytes(i.bytes)}
                {i.pinned && <span className="text-warning"> (pinned)</span>}
              </li>
            ))}
            {chosen.length > 50 && <li>and {chosen.length - 50} more</li>}
          </ul>
        </>
      ),
      run: () => remove.mutate({ keys: chosen.map((i) => i.key), bytes: chosenBytes }, { onSuccess: () => setSelected(new Set()) }),
    });

  return (
    <div className="flex flex-col gap-4">
      <div>
        <h1 className="text-xl font-semibold">Storage</h1>
        <p className="text-sm text-muted">
          What PC Diagnostic keeps on this PC, the limits that keep it in check, and room to clean up. Everything is in{' '}
          <span className="break-all font-mono text-xs text-foreground">{data.folder}</span>.
        </p>
      </div>

      {recorder && recorder.condition !== 'Normal' && recorder.message && (
        <div
          role={recorder.condition === 'WritesPaused' ? 'alert' : 'status'}
          className={cn('rounded-md border px-3 py-2 text-sm', recorder.condition === 'WritesPaused' ? 'border-danger/50 bg-danger/10' : 'border-warning/50 bg-warning/10')}
        >
          <strong>{recorder.condition === 'WritesPaused' ? 'Not recording to disk. ' : recorder.condition === 'LowSpace' ? 'The drive is getting full. ' : 'Over the space limit. '}</strong>
          {recorder.message}
          {recorder.condition === 'OverLimit' && !editingLimit && (
            <Button size="sm" variant="outline" className="ml-2" onClick={() => setEditingLimit(true)}>
              Raise the limit
            </Button>
          )}
        </div>
      )}
      {data.recorderProblem && (
        <p className="rounded-md border border-border bg-surface px-3 py-2 text-sm text-muted">
          {data.recorderProblem} The sizes below are measured anyway; the limits are applied and clean-ups done by the recorder.
        </p>
      )}

      <div className="grid gap-4 xl:grid-cols-3">
        <Card className="xl:col-span-2">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <HardDrive className="size-4" aria-hidden /> Space
            </CardTitle>
            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="outline" disabled={clean.isPending || !recorder} onClick={() => clean.mutate()} title="Applies the limits now and gives the recording database's free space back to the drive">
                <Sparkles aria-hidden />
                {clean.isPending ? 'Cleaning up…' : 'Clean up now'}
              </Button>
            </div>
          </CardHeader>
          <CardContent className="flex flex-col gap-4">
            <UsageSummary data={data} />
            <DriveBar data={data} kinds={kinds} />
            <dl className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
              <Fact label="Full detail goes back" value={span(data.telemetry?.fullDetailFrom) ?? 'Nothing yet'} note={`kept ${data.limits.telemetryRetentionHours} h`} />
              <Fact label="Summaries go back" value={span(data.telemetry?.summariesFrom) ?? 'Nothing yet'} note={`kept ${data.limits.rollupRetentionDays} days`} />
              <Fact label="Incidents" value={incidents.length.toLocaleString()} note={`${pinned} pinned`} />
              <Fact
                label="Free inside the database"
                value={formatBytes(data.telemetry?.freeBytes ?? 0)}
                note={(data.telemetry?.freeBytes ?? 0) > 0 ? 'Clean up now gives it back' : 'Nothing to give back'}
              />
            </dl>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Database className="size-4" aria-hidden /> Limits
            </CardTitle>
            <Link to="/settings" hash="storage" className="text-sm text-accent hover:underline">
              All limits
            </Link>
          </CardHeader>
          <CardContent>
            <Limits data={data} editing={editingLimit} onEditing={setEditingLimit} />
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>What uses the space</CardTitle>
          <span className="text-sm tabular-nums text-muted">{formatBytes(data.totalBytes)}</span>
        </CardHeader>
        <CardContent>
          <DataTable
            viewKey="storage.kinds"
            label="What uses the space"
            rows={kinds}
            columns={kindColumns()}
            rowKey={(k) => k.id}
            defaultView={{ groupBy: [], sort: [{ id: 'size', desc: true }] }}
            listClassName="divide-y divide-border rounded-md border border-border"
            renderRow={(k) => (
              <div className="flex flex-col gap-1.5 p-3">
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                  <span className="flex items-center gap-2 text-sm font-medium">
                    <span aria-hidden className="size-2.5 rounded-sm" style={{ background: k.color }} />
                    {k.name}
                  </span>
                  <span className="text-sm tabular-nums">
                    {formatBytes(k.bytes)} <span className="text-xs text-muted">· {Math.round(k.share * 100)} % · {k.files.toLocaleString()} {k.id === 'incidents' ? 'incidents' : k.files === 1 ? 'file' : 'files'}</span>
                  </span>
                </div>
                <div className="h-1.5 overflow-hidden rounded-full bg-surface-muted" aria-hidden>
                  <div className="h-full rounded-full" style={{ width: `${Math.max(k.share * 100, k.bytes > 0 ? 0.5 : 0)}%`, background: k.color }} />
                </div>
                <span className="text-xs text-muted">{k.kept}</span>
              </div>
            )}
          />
          {data.leftoverBytes > 0 && (
            <p className="mt-2 text-xs text-muted">{formatBytes(data.leftoverBytes)} are files of deleted incidents that were still open; the recorder removes them once nothing holds them.</p>
          )}
        </CardContent>
      </Card>

      <div className="grid items-start gap-4 xl:grid-cols-3">
        <div className="xl:col-span-2">
          <WhereFilesAreKept />
        </div>
        <LayoutBackups />
      </div>

      <Card>
        <CardHeader className="flex-wrap">
          <CardTitle>Incidents by size</CardTitle>
          <div className="flex flex-wrap items-center gap-2">
            <Button size="sm" variant="ghost" disabled={incidents.length === 0} onClick={() => setSelected(new Set(incidents.filter((i) => !i.pinned && i.state !== 'Recording').map((i) => i.key)))}>
              Select unpinned
            </Button>
            <Button size="sm" variant="ghost" disabled={selected.size === 0} onClick={() => setSelected(new Set())}>
              Clear
            </Button>
            <Button size="sm" variant="danger" disabled={chosen.length === 0 || remove.isPending || !recorder} onClick={askDelete}>
              <Trash2 aria-hidden />
              {remove.isPending ? 'Deleting…' : chosen.length > 0 ? `Delete ${chosen.length} (${formatBytes(chosenBytes)})` : 'Delete selected'}
            </Button>
          </div>
        </CardHeader>
        <CardContent>
          <p className="mb-3 text-xs text-muted">Pinned incidents are never removed by the limits. The largest are listed first; group them by month, type or how they were captured.</p>
          <DataTable
            viewKey="storage.incidents"
            label="Incidents by size"
            rows={incidents}
            columns={INCIDENT_COLUMNS}
            rowKey={(i) => i.key}
            defaultView={{ groupBy: [], sort: [{ id: 'size', desc: true }] }}
            emptyText="No incidents are kept."
            fill
            listClassName="divide-y divide-border rounded-md border border-border"
            renderRow={(i) => (
              <div className={cn('flex items-center gap-3 p-3', selected.has(i.key) && 'bg-accent/5')}>
                <input
                  type="checkbox"
                  aria-label={`Select ${i.key}`}
                  checked={selected.has(i.key)}
                  disabled={i.state === 'Recording' || i.state === 'Finalizing'}
                  onChange={(e) => toggle(i.key, e.target.checked)}
                  className="size-4 shrink-0 accent-[var(--accent)]"
                />
                <span className="min-w-0 flex-1">
                  <span className="flex flex-wrap items-baseline gap-x-2">
                    <Link to="/incidents/$key" params={{ key: i.key }} className="truncate text-sm font-medium hover:underline">
                      {i.title}
                    </Link>
                    {i.pinned && <Badge tone="accent">Pinned</Badge>}
                    {i.state !== 'Complete' && <Badge tone={i.state === 'Recording' ? 'danger' : 'neutral'}>{i.state}</Badge>}
                  </span>
                  <span className="block text-xs text-muted">
                    {i.key} · {when(i.centerAt)}
                    {i.userType ? ` · ${USER_INCIDENT_TYPES.find((t) => t.id === i.userType)?.label ?? i.userType}` : ''}
                  </span>
                </span>
                <span className="shrink-0 text-sm tabular-nums">{formatBytes(i.bytes)}</span>
                <Button
                  size="icon"
                  variant="ghost"
                  aria-label={i.pinned ? `Unpin ${i.key}` : `Pin ${i.key}`}
                  aria-pressed={i.pinned}
                  title={i.pinned ? 'Unpin: the limits may remove it' : 'Pin: the limits never remove it'}
                  disabled={pin.isPending || !recorder}
                  onClick={() => pin.mutate({ key: i.key, pinned: !i.pinned })}
                >
                  {i.pinned ? <PinOff aria-hidden /> : <Pin aria-hidden />}
                </Button>
              </div>
            )}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Recent clean-ups</CardTitle>
        </CardHeader>
        <CardContent>
          <DataTable
            viewKey="storage.actions"
            label="Recent clean-ups"
            rows={data.recent}
            columns={ACTION_COLUMNS}
            rowKey={(a) => `${a.at}-${a.kind}-${a.detail}`}
            defaultView={{ groupBy: [], sort: [{ id: 'when', desc: true }] }}
            emptyText="Nothing has needed cleaning up yet."
            fill
            listClassName="divide-y divide-border rounded-md border border-border"
            renderRow={(a) => (
              <div className="flex flex-col gap-0.5 p-3">
                <span className="flex flex-wrap items-baseline justify-between gap-2 text-sm">
                  <span className="font-medium">
                    {ACTIONS[a.kind] ?? a.kind}
                    {a.count > 1 && a.kind !== 'incidents-removed' ? <span className="text-xs font-normal text-muted"> · {a.count} times</span> : null}
                  </span>
                  <span className="tabular-nums text-muted">
                    {a.freedBytes > 0 ? `${formatBytes(a.freedBytes)} back · ` : ''}
                    {when(a.at)}
                  </span>
                </span>
                <span className="text-xs text-muted">
                  {a.detail} {a.automatic ? '' : '(asked for on this page)'}
                </span>
              </div>
            )}
          />
        </CardContent>
      </Card>

      <ConfirmAction pending={pending} onClose={() => setPending(null)} />
    </div>
  );
}

/**
 * The space limit, changed in place: in GB (0 = no limit), up to what the drive could still hold, with what the
 * recorded data uses now beside it. Saved with the other settings and applied by the recorder a few seconds later.
 */
function SpaceLimitEditor({ data, onDone }: { data: StorageResponse; onDone: () => void }) {
  const save = useSetSpaceLimit();
  const [value, setValue] = useState(String(data.limits.dataLimitGb));
  const gb = Number(value);
  const valid = value.trim() !== '' && Number.isInteger(gb) && gb >= 0 && gb <= 10_000;
  const used = data.limitedBytes ?? data.totalBytes;
  const room = data.drive ? Math.floor((used + data.drive.freeBytes) / GB) : null;
  return (
    <form
      className="flex flex-col gap-2 rounded-md border border-border p-2.5"
      onSubmit={(e) => {
        e.preventDefault();
        if (valid) save.mutate(gb, { onSuccess: onDone });
      }}
    >
      <label htmlFor="space-limit" className="font-medium">
        Most space the recordings may use
      </label>
      <span className="flex items-center gap-2">
        <input
          id="space-limit"
          type="number"
          min={0}
          max={10_000}
          step={1}
          value={value}
          autoFocus
          onChange={(e) => setValue(e.target.value)}
          aria-invalid={!valid || undefined}
          className="h-8 w-28 rounded-md border border-border bg-surface px-2 text-sm tabular-nums aria-[invalid=true]:border-danger"
        />
        <span className="text-xs text-muted">GB (0 = no limit)</span>
      </span>
      <p className="text-xs text-muted">
        Recordings use <span className="tabular-nums">{formatBytes(used)}</span> now
        {room !== null && (
          <>
            ; {data.drive!.name} could hold about <span className="tabular-nums">{room} GB</span> of them
          </>
        )}
        .
      </p>
      <span className="flex gap-2">
        <Button size="sm" type="submit" disabled={!valid || save.isPending}>
          {save.isPending ? 'Saving…' : 'Save'}
        </Button>
        <Button size="sm" variant="ghost" type="button" onClick={onDone}>
          Cancel
        </Button>
      </span>
    </form>
  );
}

/** "680 MB of 10 GB allowed", with a bar of how much of the limit is used. */
function UsageSummary({ data }: { data: StorageResponse }) {
  const limit = data.limits.dataLimitGb * GB;
  // The character generator's models and creations are not recordings: the limit does not count them.
  const counted = data.limitedBytes ?? data.totalBytes;
  const notCounted = data.totalBytes - counted;
  const share = limit > 0 ? Math.min(1, counted / limit) : null;
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap items-baseline gap-x-2">
        <span className="text-3xl font-semibold tabular-nums">{formatBytes(counted)}</span>
        <span className="text-sm text-muted">{limit > 0 ? `of ${data.limits.dataLimitGb} GB allowed` : 'used, with no space limit'}</span>
      </div>
      {notCounted > 0 && (
        <p className="text-xs text-muted">
          Plus <span className="tabular-nums">{formatBytes(notCounted)}</span> for the character generator (its AI models and the characters made), which the limit does not count.
        </p>
      )}
      {share !== null && (
        <div
          role="meter"
          aria-label="Share of the space limit used"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round(share * 100)}
          className="h-2 overflow-hidden rounded-full bg-surface-muted"
        >
          <div className={cn('h-full rounded-full', share > 0.95 ? 'bg-danger' : share > 0.8 ? 'bg-warning' : 'bg-accent')} style={{ width: `${Math.max(share * 100, 0.5)}%` }} />
        </div>
      )}
    </div>
  );
}

/** The drive: PC Diagnostic's data by kind, everything else, and what is free (with the free space to keep). */
function DriveBar({ data, kinds }: { data: StorageResponse; kinds: KindRow[] }) {
  const drive = data.drive;
  if (!drive || drive.totalBytes <= 0) return null;
  const used = drive.totalBytes - drive.freeBytes;
  const others = Math.max(0, used - data.totalBytes);
  const pct = (bytes: number) => (bytes / drive.totalBytes) * 100;
  const keep = data.limits.keepFreeGb * GB;
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap items-baseline justify-between gap-2 text-sm">
        <span>
          Drive {drive.name} · <span className="tabular-nums">{formatBytes(drive.freeBytes)}</span> free of <span className="tabular-nums">{formatBytes(drive.totalBytes)}</span>
        </span>
        {keep > 0 && <span className={cn('text-xs', drive.freeBytes < keep ? 'text-warning' : 'text-muted')}>{data.limits.keepFreeGb} GB kept free</span>}
      </div>
      <div className="relative flex h-3 overflow-hidden rounded-full bg-surface-muted" role="img" aria-label={`PC Diagnostic ${formatBytes(data.totalBytes)}, other files ${formatBytes(others)}, free ${formatBytes(drive.freeBytes)}`}>
        {kinds.map((k) => (
          <div key={k.id} style={{ width: `${pct(k.bytes)}%`, background: k.color }} className="min-w-px" />
        ))}
        <div style={{ width: `${pct(others)}%` }} className="bg-muted/40" />
        {keep > 0 && keep < drive.totalBytes && <div aria-hidden className="absolute inset-y-0 w-0.5 bg-warning" style={{ right: `${pct(keep)}%` }} title="Free space to keep" />}
      </div>
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted">
        <span className="flex items-center gap-1.5">
          <span aria-hidden className="size-2 rounded-sm bg-accent" /> PC Diagnostic {formatBytes(data.totalBytes)}
        </span>
        <span className="flex items-center gap-1.5">
          <span aria-hidden className="size-2 rounded-sm bg-muted/40" /> Other files {formatBytes(others)}
        </span>
        <span className="flex items-center gap-1.5">
          <span aria-hidden className="size-2 rounded-sm border border-border" /> Free {formatBytes(drive.freeBytes)}
        </span>
      </div>
    </div>
  );
}

function Fact({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div className="rounded-md border border-border p-2.5">
      <dt className="text-xs text-muted">{label}</dt>
      <dd className="text-base font-semibold tabular-nums">{value}</dd>
      {note && <dd className="text-xs text-muted">{note}</dd>}
    </div>
  );
}

/** The rules in plain words, in the order the recorder applies them. */
function Limits({ data, editing, onEditing }: { data: StorageResponse; editing: boolean; onEditing: (on: boolean) => void }) {
  const l = data.limits;
  return (
    <div className="flex flex-col gap-3 text-sm">
      {editing ? (
        <SpaceLimitEditor data={data} onDone={() => onEditing(false)} />
      ) : (
        <div className="flex items-center justify-between gap-3">
          <span className="text-muted">Space limit</span>
          <span className="flex items-center gap-2">
            <span className="font-medium tabular-nums">{l.dataLimitGb > 0 ? `${l.dataLimitGb} GB` : 'None'}</span>
            <Button size="sm" variant="outline" onClick={() => onEditing(true)} aria-label="Change the space limit">
              Change
            </Button>
          </span>
        </div>
      )}
      <dl className="grid grid-cols-[1fr_auto] gap-x-3 gap-y-1.5">
        <dt className="text-muted">Keep free on {data.drive?.name ?? 'the drive'}</dt>
        <dd className="text-right font-medium tabular-nums">{l.keepFreeGb > 0 ? `${l.keepFreeGb} GB` : 'Only 1 GB'}</dd>
        <dt className="text-muted">Most incidents kept</dt>
        <dd className="text-right font-medium tabular-nums">{l.maxIncidents > 0 ? l.maxIncidents.toLocaleString() : 'All'}</dd>
        <dt className="text-muted">Full detail kept</dt>
        <dd className="text-right font-medium tabular-nums">{l.telemetryRetentionHours} h</dd>
        <dt className="text-muted">Summaries kept</dt>
        <dd className="text-right font-medium tabular-nums">{l.rollupRetentionDays} days</dd>
      </dl>
      <div className="rounded-md bg-surface-muted p-2.5 text-xs text-muted">
        <p className="mb-1 font-medium text-foreground">When space is needed, the recorder gives back, in order:</p>
        <ol className="list-decimal space-y-0.5 pl-4">
          <li>older full detail, keeping at least {l.fullDetailFloorHours} h;</li>
          <li>older summaries and events, keeping at least {l.summariesFloorDays} days;</li>
          <li>the oldest unpinned incidents, for the space limit only.</li>
        </ol>
        <p className="mt-1.5">
          Below {formatBytes(l.pauseBelowBytes, 0)} free nothing new is written (the last 30 minutes wait in memory) until {formatBytes(l.resumeAboveBytes)} are free.
        </p>
      </div>
    </div>
  );
}
