import { useQuery } from '@tanstack/react-query';
import { AlertTriangle, ChevronDown, Plug, Search, Unplug } from 'lucide-react';
import { useMemo, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { apiGet } from '@/lib/api/client';
import type { DeviceEventInfo, IoDevicePart, RegisteredDevice, SystemOverview } from '@/lib/api/types';
import { formatAgo, formatDate, formatDateTime } from '@/lib/format';
import { cn } from '@/lib/utils';
import { ExpectedHardwareCard } from './ExpectedHardware';
import { DISABLED, STATE_LABELS, failedToStart, hasFault, kindIcon, kindLabel, kindRank, kindsText, lastActivity, stateOf, summarize, type DeviceState } from './devices';

type Filter = 'all' | DeviceState;

const STATE_ORDER: Record<DeviceState, number> = { problem: 0, connected: 1, disconnected: 2 };

const COLUMNS: Column<RegisteredDevice>[] = [
  { id: 'state', header: 'Status', value: (r) => `${STATE_ORDER[stateOf(r.device)]} ${STATE_LABELS[stateOf(r.device)]}`, group: (r) => STATE_LABELS[stateOf(r.device)] },
  { id: 'kind', header: 'Kind', value: (r) => `${String(kindRank(r.device.kind)).padStart(2, '0')} ${kindLabel(r.device.kind)}`, group: (r) => kindLabel(r.device.kind) },
  { id: 'name', header: 'Name', value: (r) => r.device.name, groupable: false },
  { id: 'bus', header: 'Connection', value: (r) => r.device.bus },
  { id: 'maker', header: 'Maker', value: (r) => r.device.manufacturer },
  { id: 'where', header: 'Where', value: (r) => (r.device.external ? 'Attached' : 'Built in') },
  { id: 'last', header: 'Last connected or removed', value: lastActivity, groupable: false, firstDesc: true },
  { id: 'first', header: 'First installed', value: (r) => (r.device.firstInstalled ? Date.parse(r.device.firstInstalled) : r.firstSeen), groupable: false, firstDesc: true },
];

const EVENT_ICONS: Record<number, typeof Plug> = { 1: Plug, 2: Unplug, 3: Unplug, 4: AlertTriangle, 5: AlertTriangle, 6: AlertTriangle, 7: Unplug, 8: Plug };

const EVENT_KINDS: Record<number, string> = {
  1: 'Connected',
  2: 'Disconnected',
  3: 'Dropped out',
  4: 'Keeps disconnecting',
  5: 'Device of this PC disappeared',
  6: 'Stopped working',
  7: 'Disabled',
  8: 'Works again',
};

const EVENT_COLUMNS: Column<DeviceEventInfo>[] = [
  { id: 'time', header: 'Time', value: (e) => e.ts, groupable: false, firstDesc: true },
  { id: 'kind', header: 'What happened', value: (e) => EVENT_KINDS[e.eventId] ?? 'Other' },
  { id: 'device', header: 'Device', value: (e) => e.device },
  { id: 'severity', header: 'Severity', value: (e) => e.severity, group: (e) => (e.severity >= 2 ? 'Warnings' : 'Information'), firstDesc: true },
  { id: 'bus', header: 'Connection', value: (e) => e.data?.bus ?? null },
];

const PART_COLUMNS: Column<IoDevicePart>[] = [
  { id: 'name', header: 'Part', value: (p) => p.name, groupable: false },
  { id: 'class', header: 'Class', value: (p) => p.class },
  { id: 'state', header: 'State', value: (p) => (p.problemCode ? `Code ${p.problemCode}` : p.present ? 'Connected' : 'Not connected') },
  { id: 'technical', header: 'Role', value: (p) => (p.technical ? 'Technical' : 'Function') },
  { id: 'driver', header: 'Driver', value: (p) => p.driverProvider },
];

/**
 * The device register: every input and output device connected now or before (Windows remembers earlier
 * connections), grouped by status and kind, with problems first, and what changed recently.
 */
/** The expected-hardware check, then the device register. */
export function DevicesTab({ overview, live }: { overview: SystemOverview; live?: boolean }) {
  return (
    <div className="flex flex-col gap-4">
      <ExpectedHardwareCard />
      <DeviceRegisterView overview={overview} live={live} />
    </div>
  );
}

function DeviceRegisterView({ overview, live }: { overview: SystemOverview; live?: boolean }) {
  const rows = useMemo(() => overview.ioDevices ?? [], [overview.ioDevices]);
  const [filter, setFilter] = useState<Filter>('all');
  const [technical, setTechnical] = useState(false);
  const [text, setText] = useState('');
  const summary = useMemo(() => summarize(rows), [rows]);
  const { data: events } = useQuery({
    queryKey: ['device-events'],
    queryFn: ({ signal }) => apiGet<DeviceEventInfo[]>('/system/device-events?limit=100', signal),
    refetchInterval: 15_000,
    enabled: live === true,
  });

  const shown = useMemo(() => {
    const needle = text.trim().toLowerCase();
    return rows.filter(
      (r) =>
        (technical || !r.device.technical || hasFault(r.device)) &&
        (filter === 'all' || stateOf(r.device) === filter) &&
        (!needle || [r.device.name, r.device.manufacturer, r.device.type, r.device.bus, kindLabel(r.device.kind), r.device.vendorId && `${r.device.vendorId}:${r.device.productId}`].some((v) => v?.toLowerCase().includes(needle))),
    );
  }, [rows, filter, technical, text]);

  if (rows.length === 0) {
    return <p className="text-sm text-muted">No devices registered yet. The register is read with the inventory when recording starts.</p>;
  }

  const filters: { id: Filter; label: string; count: number; tone?: 'warning' }[] = [
    { id: 'all', label: 'All', count: summary.connected + summary.remembered },
    { id: 'connected', label: 'Connected', count: summary.connected },
    { id: 'disconnected', label: 'Not connected', count: summary.remembered },
    { id: 'problem', label: 'With a problem', count: summary.problems + summary.disabled, tone: summary.problems > 0 ? 'warning' : undefined },
  ];

  return (
    <div className="grid grid-cols-1 gap-4 2xl:grid-cols-[minmax(0,1fr)_24rem]">
      <Card>
        <CardHeader className="flex-wrap">
          <CardTitle>Input and output devices</CardTitle>
          <span className="text-xs text-muted">Connected now and earlier: Windows remembers each device with when it was first installed, last connected and last removed.</span>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <div className="flex flex-wrap items-center gap-2">
            <div role="group" aria-label="Show" className="flex flex-wrap gap-1.5">
              {filters.map((f) => (
                <button
                  key={f.id}
                  type="button"
                  aria-pressed={filter === f.id}
                  onClick={() => setFilter(f.id)}
                  className={cn('rounded-full border px-2.5 py-0.5 text-xs', filter === f.id ? 'border-accent bg-accent/10 text-foreground' : 'border-border text-muted hover:text-foreground')}
                >
                  {f.label} <span className={cn('tabular-nums', f.tone === 'warning' ? 'text-warning' : 'text-muted')}>{f.count}</span>
                </button>
              ))}
            </div>
            <label className="flex items-center gap-1.5 text-xs text-muted">
              <input type="checkbox" checked={technical} onChange={(e) => setTechnical(e.target.checked)} className="size-3.5 accent-[var(--accent)]" />
              Show hubs, controllers and programs' virtual devices ({summary.technical})
            </label>
          </div>
          <DataTable
            viewKey="system.iodevices"
            label="Input and output devices"
            rows={shown}
            columns={COLUMNS}
            rowKey={(r) => r.device.key}
            defaultView={{ groupBy: ['state', 'kind'], sort: [{ id: 'name', desc: false }] }}
            emptyText="Nothing matches."
            toolbarStart={
              <label className="relative block w-full max-w-sm">
                <span className="sr-only">Filter devices</span>
                <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
                <input
                  type="search"
                  value={text}
                  onChange={(e) => setText(e.target.value)}
                  placeholder="Filter, e.g. Logitech, Bluetooth, 046D"
                  className="h-8 w-full rounded-md border border-border bg-background pl-7 pr-2 text-sm placeholder:text-muted"
                />
              </label>
            }
            renderRow={(r) => <DeviceCard row={r} events={events?.filter((e) => belongsTo(e, r)) ?? []} />}
            fill
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Recent changes</CardTitle>
        </CardHeader>
        <CardContent>
          {!live ? (
            <p className="text-sm text-muted">The timeline lists the device changes recorded in this incident.</p>
          ) : !events?.length ? (
            <p className="text-sm text-muted">No device was connected, disconnected or failed while recording.</p>
          ) : (
            <EventList viewKey="system.device-events" label="Recent device changes" events={events.slice(0, 100)} />
          )}
        </CardContent>
      </Card>
    </div>
  );
}

/** Events about a device: by its instance ID, its IDs, or its name. */
function belongsTo(event: DeviceEventInfo, row: RegisteredDevice): boolean {
  const d = row.device;
  const data = event.data ?? {};
  if (data.instanceId && (data.instanceId === d.instanceId || d.parts.some((p) => p.instanceId === data.instanceId))) return true;
  if (d.vendorId && data.vendorId === d.vendorId && data.productId === d.productId) return true;
  return event.device === d.name;
}

/** Device changes, grouped and sorted like every list (newest first). */
function EventList({ viewKey, label, events }: { viewKey: string; label: string; events: DeviceEventInfo[] }) {
  return (
    <DataTable
      viewKey={viewKey}
      label={label}
      rows={events.map((e, index) => ({ ...e, index }))}
      columns={EVENT_COLUMNS}
      rowKey={(e) => `${e.ts}-${e.index}`}
      defaultView={{ groupBy: [], sort: [{ id: 'time', desc: true }] }}
      compact
      listClassName="gap-2"
      fill
      renderRow={(e) => <EventLine event={e} />}
    />
  );
}

function EventLine({ event }: { event: DeviceEventInfo }) {
  const Icon = EVENT_ICONS[event.eventId] ?? Plug;
  const warn = event.severity >= 2;
  return (
    <div className="flex gap-2">
      <Icon className={cn('mt-0.5 size-4 shrink-0', warn ? 'text-warning' : 'text-muted')} aria-hidden />
      <div className="min-w-0">
        <p className="text-sm">{event.title}</p>
        <p className="text-xs text-muted" title={formatDateTime(new Date(event.ts).toISOString())}>
          {formatAgo(event.ts)}
          {warn && event.message ? ` · ${event.message}` : ''}
        </p>
      </div>
    </div>
  );
}

function DeviceCard({ row, events }: { row: RegisteredDevice; events: DeviceEventInfo[] }) {
  const d = row.device;
  const [open, setOpen] = useState(false);
  const Icon = kindIcon(d.kind);
  const state = stateOf(d);
  const failure = failedToStart(d);
  const also = kindsText(d);
  const ids = d.vendorId ? `${d.vendorId}:${d.productId}` : null;

  return (
    <div className={cn('mb-2 rounded-md border p-2.5', state === 'problem' && d.problemCode !== DISABLED ? 'border-warning/50 bg-warning/5' : 'border-border', state === 'disconnected' && 'border-dashed')}>
      <button type="button" className="flex w-full items-start gap-2.5 text-left" aria-expanded={open} onClick={() => setOpen(!open)}>
        <Icon className={cn('mt-0.5 size-4 shrink-0', d.present ? 'text-accent' : 'text-muted')} aria-hidden />
        <div className="min-w-0 flex-1">
          <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
            {d.name}
            {d.connected > 1 && <Badge tone="accent">{d.connected} connected</Badge>}
            {d.present && d.problemCode === 0 && d.connected <= 1 && <Badge tone="success">connected</Badge>}
            {hasFault(d) && <Badge tone="warning">code {d.problemCode}</Badge>}
            {d.present && d.problemCode === DISABLED && <Badge>disabled</Badge>}
            {!d.present && <Badge>not connected</Badge>}
            {failure && <Badge tone="warning">failed to start</Badge>}
          </p>
          <p className="text-xs text-muted">
            {[also || null, d.bus, d.external ? null : 'built in', d.manufacturer, ids].filter(Boolean).join(' · ')}
          </p>
          {d.problem && <p className="text-xs text-warning">{d.problem}</p>}
          {failure && <p className="text-xs text-warning">Windows could not read this device when it was connected ({failure}): usually a faulty cable, port or device, or not enough power on the port.</p>}
          <p className="text-xs text-muted">
            {d.present ? `connected ${formatAgo(d.lastArrival ?? row.lastSeen)}` : `last connected ${d.lastArrival ? formatDate(d.lastArrival) : 'unknown'}${d.lastRemoval ? `, removed ${formatAgo(d.lastRemoval)}` : ''}`}
            {d.firstInstalled ? ` · first installed ${formatDate(d.firstInstalled)}` : ''}
          </p>
        </div>
        <ChevronDown className={cn('mt-0.5 size-4 shrink-0 text-muted transition-transform', open && 'rotate-180')} aria-hidden />
      </button>
      {open && (
        <div className="mt-2 flex flex-col gap-2 border-t border-border pt-2 text-xs">
          <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-0.5">
            {(
              [
                ['Type', d.type],
                ['Class', d.class],
                ['Driver', d.driverVersion ? `${d.driverProvider ?? 'unknown'} ${d.driverVersion}${d.driverDate ? ` (${d.driverDate})` : ''}` : null],
                ['Driver file', d.driverInf],
                ['Service', d.service],
                ['Port', d.location],
                ['Hardware ID', d.hardwareId],
                ['Instance ID', d.instanceId],
                ['Registered here', formatDateTime(new Date(row.firstSeen).toISOString())],
              ] as [string, string | null][]
            )
              .filter(([, v]) => v)
              .map(([k, v]) => (
                <div key={k} className="contents">
                  <dt className="text-muted">{k}</dt>
                  <dd className="break-all">{v}</dd>
                </div>
              ))}
          </dl>
          {d.parts.length > 1 && (
            <div>
              <p className="font-medium">Parts Windows created for it ({d.parts.length})</p>
              <div className="mt-0.5">
                <DataTable
                  viewKey="system.device-parts"
                  label={`Parts of ${d.name}`}
                  rows={d.parts}
                  columns={PART_COLUMNS}
                  rowKey={(p) => p.instanceId}
                  defaultView={{ groupBy: [], sort: [] }}
                  compact
                  listClassName="gap-0.5"
                  rowTitle={(p) => p.instanceId}
                  renderRow={(p) => (
                    <p className={cn('flex flex-wrap gap-x-2', (p.technical || !p.present) && 'text-muted')}>
                      <span>{p.name}</span>
                      <span className="text-muted">{[p.class, p.technical ? 'technical' : null, p.present ? null : 'not connected', p.problemCode ? `code ${p.problemCode}` : null].filter(Boolean).join(' · ')}</span>
                    </p>
                  )}
                />
              </div>
            </div>
          )}
          {events.length > 0 && (
            <div>
              <p className="font-medium">Recorded changes</p>
              <div className="mt-0.5">
                <EventList viewKey="system.device-history" label={`Recorded changes of ${d.name}`} events={events.slice(0, 50)} />
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
