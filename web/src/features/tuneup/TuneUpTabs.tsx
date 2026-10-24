import { AlertTriangle, CheckCircle2, Info, MoreHorizontal, Search, Shield, ShieldAlert, ChevronRight } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Menu, type MenuItem } from '@/components/ui/menu';
import { CleanupTree } from './CleanupTree';
import type { CleanupTarget, ControlService, StartupEntry, TuneUpReport } from '@/lib/api/types';
import { formatBytes } from '@/lib/format';
import { cn } from '@/lib/utils';
import type { PendingAction } from './ConfirmAction';
import type { Recommendation, TuneUpTab } from './recommend';
import { useControlAction } from './useControl';

const STATUS_TONE: Record<string, 'success' | 'neutral' | 'warning'> = { Running: 'success', Stopped: 'neutral', Paused: 'warning' };
const START_TEXT: Record<string, string> = {
  Automatic: 'Automatic',
  AutomaticDelayed: 'Automatic (delayed)',
  Manual: 'Manual',
  Disabled: 'Disabled',
  Boot: 'Boot',
  System: 'System',
};

// Services.

/** Every service, Windows' own and others', with start, stop, restart and how it starts. */
export function ServicesTab({ services, ask }: { services: ControlService[] | undefined; ask: (action: PendingAction) => void }) {
  const control = useControlAction();
  const [filter, setFilter] = useState('');
  const [selected, setSelected] = useState<Set<string>>(() => new Set());
  const text = filter.trim().toLowerCase();
  const rows = useMemo(
    () => (services ?? []).filter((s) => !text || `${s.displayName} ${s.name} ${s.publisher ?? ''} ${s.description ?? ''}`.toLowerCase().includes(text)),
    [services, text],
  );
  const chosen = (services ?? []).filter((s) => selected.has(s.name));

  const act = (s: ControlService, action: string, startType?: string) => {
    const run = (confirmed: boolean) =>
      control.mutate({ path: `/control/services/${encodeURIComponent(s.name)}/${action}`, body: { startType, confirmed }, label: `${s.displayName}` });
    if (action === 'start') {
      run(false);
      return;
    }

    ask({
      title: action === 'start-type' ? `Start ${s.displayName} ${START_TEXT[startType!]?.toLowerCase()}?` : `${action === 'stop' ? 'Stop' : 'Restart'} ${s.displayName}?`,
      confirmLabel: action === 'start-type' ? 'Change' : action === 'stop' ? 'Stop service' : 'Restart service',
      danger: action === 'stop' || startType === 'Disabled',
      body: (
        <>
          {s.description && <p className="text-muted">{s.description}</p>}
          <p>
            {action === 'stop'
              ? 'What uses it stops working until it starts again (it may start again by itself when needed).'
              : action === 'restart'
                ? 'It stops and starts again; what uses it pauses for a moment.'
                : startType === 'Disabled'
                  ? 'It will not start at all, even when something needs it.'
                  : startType === 'Manual'
                    ? 'It starts only when something needs it.'
                    : 'It starts with Windows.'}
          </p>
        </>
      ),
      acknowledge: s.protection === 'Caution' ? `${s.protectionReason} I understand.` : undefined,
      run,
    });
  };

  const menu = (s: ControlService): MenuItem[] => {
    const locked = s.protection === 'Protected';
    const why = locked ? `Protected: ${s.protectionReason}` : undefined;
    return [
      { label: 'Start', onSelect: () => act(s, 'start'), disabled: s.status === 'Running' || s.startType === 'Disabled', title: s.startType === 'Disabled' ? 'Disabled: change how it starts first' : undefined },
      { label: 'Stop', onSelect: () => act(s, 'stop'), danger: true, disabled: locked || s.status !== 'Running' || !s.canStop, title: why ?? (!s.canStop ? 'It does not accept a stop request now' : undefined) },
      { label: 'Restart', onSelect: () => act(s, 'restart'), disabled: locked || s.status !== 'Running' || !s.canStop, title: why },
      ...(['Automatic', 'AutomaticDelayed', 'Manual', 'Disabled'] as const).map((type, i) => ({
        label: `${START_TEXT[type]}${s.startType === type ? ' ✓' : ''}`,
        section: i === 0 ? 'Starts' : undefined,
        disabled: locked || s.startType === type,
        title: why,
        onSelect: () => act(s, 'start-type', type),
      })),
    ];
  };

  const columns: Column<ControlService>[] = [
    {
      id: 'select',
      header: '',
      value: () => null,
      sortable: false,
      groupable: false,
      className: 'w-7',
      cell: (s) => (
        <input
          type="checkbox"
          aria-label={`Select ${s.displayName}`}
          disabled={s.protection === 'Protected' || s.status !== 'Running' || !s.canStop}
          checked={selected.has(s.name)}
          onChange={(e) =>
            setSelected((all) => {
              const next = new Set(all);
              if (e.target.checked) next.add(s.name);
              else next.delete(s.name);
              return next;
            })
          }
          className="size-3.5 accent-[var(--accent)]"
        />
      ),
    },
    {
      id: 'name',
      header: 'Service',
      value: (s) => s.displayName,
      groupable: false,
      className: 'max-w-80',
      cell: (s) => (
        <span className="flex min-w-0 items-center gap-1.5" title={`${s.name}${s.description ? `: ${s.description}` : ''}`}>
          {s.protection === 'Protected' ? <Shield className="size-3.5 shrink-0 text-muted" aria-label="Protected" /> : s.protection === 'Caution' ? <ShieldAlert className="size-3.5 shrink-0 text-warning" aria-label="Windows service" /> : null}
          <span className="truncate">{s.displayName}</span>
          <span className="truncate text-xs text-muted">{s.name}</span>
        </span>
      ),
    },
    { id: 'origin', header: 'From', value: (s) => (s.windows ? 'Windows' : 'Other programs'), hidden: true },
    { id: 'status', header: 'Status', value: (s) => s.status, cell: (s) => <Badge tone={STATUS_TONE[s.status] ?? 'neutral'}>{s.status}</Badge> },
    { id: 'start', header: 'Starts', value: (s) => START_TEXT[s.startType] ?? s.startType, className: 'text-xs text-muted' },
    { id: 'publisher', header: 'Publisher', value: (s) => s.publisher, className: 'max-w-48 truncate text-xs text-muted', cell: (s) => s.publisher ?? '—' },
    { id: 'pid', header: 'PID', value: (s) => s.processId, groupable: false, align: 'right', className: 'w-16 font-mono text-xs text-muted', cell: (s) => s.processId ?? '' },
    {
      id: 'actions',
      header: '',
      value: () => null,
      sortable: false,
      groupable: false,
      className: 'w-8',
      cell: (s) => (
        <Menu label={`Actions for ${s.displayName}`} items={menu(s)}>
          <MoreHorizontal aria-hidden />
        </Menu>
      ),
    },
  ];

  const others = (services ?? []).filter((s) => !s.windows && s.status === 'Running' && s.canStop && s.protection === 'None');
  const stopSelected = (then: 'keep' | 'Manual' | 'Disabled') =>
    ask({
      title: `Stop ${chosen.length} service${chosen.length === 1 ? '' : 's'}?`,
      confirmLabel: 'Stop',
      danger: true,
      body: (
        <>
          <ul className="max-h-40 list-disc overflow-auto pl-5 text-xs text-muted">
            {chosen.map((s) => (
              <li key={s.name}>{s.displayName}</li>
            ))}
          </ul>
          <p>{then === 'keep' ? 'They may start again with Windows or when something needs them.' : then === 'Manual' ? 'Afterwards they start only when something needs them.' : 'Afterwards they never start, even when something needs them.'}</p>
        </>
      ),
      acknowledge: chosen.some((s) => s.protection === 'Caution') ? 'Some are Windows services: features that use them stop working. I understand.' : undefined,
      run: (confirmed) =>
        control.mutate(
          { path: '/tuneup/stop-services', body: { items: chosen.map((s) => s.name), value: then === 'keep' ? null : then, confirmed }, label: 'Stopping services' },
          { onSuccess: () => setSelected(new Set()) },
        ),
    });

  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-muted">
        Windows' services and those of installed programs. Updaters and helpers of other programs can usually start only when needed (Manual); Windows' own ask for
        confirmation, and those Windows needs are protected.
      </p>
      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" variant="danger" disabled={chosen.length === 0} onClick={() => stopSelected('keep')}>
          Stop selected ({chosen.length})
        </Button>
        <Button size="sm" variant="outline" disabled={chosen.length === 0} onClick={() => stopSelected('Manual')}>
          Stop and set to Manual
        </Button>
        <Button size="sm" variant="outline" disabled={others.length === 0} onClick={() => setSelected(new Set(others.map((s) => s.name)))}>
          Select running services not from Windows ({others.length})
        </Button>
      </div>
      {!services ? (
        <p className="text-sm text-muted">Reading the services…</p>
      ) : (
        <DataTable
          viewKey="tuneup.services"
          label="Services"
          rows={rows}
          columns={columns}
          rowKey={(s) => s.name}
          defaultView={{ groupBy: ['origin', 'status'], sort: [{ id: 'name', desc: false }] }}
          emptyText="No service matches."
          fill
          toolbarStart={
            <label className="relative block w-full max-w-sm">
              <span className="sr-only">Filter services</span>
              <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
              <input type="search" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter by name or publisher" className="h-8 w-full rounded-md border border-border bg-background pl-7 pr-2 text-sm placeholder:text-muted" />
            </label>
          }
        />
      )}
    </div>
  );
}

// Startup.

/** Programs that start at sign-in, switched on and off like Task Manager does. */
export function StartupTab({ entries }: { entries: StartupEntry[] | undefined }) {
  const control = useControlAction();
  const columns: Column<StartupEntry>[] = [
    { id: 'name', header: 'Program', value: (e) => e.name, groupable: false, className: 'max-w-64 truncate', cell: (e) => <span title={e.command}>{e.name}</span> },
    { id: 'publisher', header: 'Publisher', value: (e) => e.publisher, className: 'max-w-48 truncate text-xs text-muted', cell: (e) => e.publisher ?? '—' },
    { id: 'location', header: 'Starts from', value: (e) => e.location, className: 'text-xs text-muted' },
    { id: 'state', header: 'Starts', value: (e) => (e.enabled ? 'Starts at sign-in' : 'Turned off') },
    {
      id: 'toggle',
      header: 'At sign-in',
      value: (e) => (e.enabled ? 1 : 0),
      groupable: false,
      cell: (e) => (
        <label className="relative inline-flex cursor-pointer items-center" title={e.enabled ? 'Turn off' : 'Turn on'}>
          <input
            type="checkbox"
            checked={e.enabled}
            aria-label={`${e.name} starts at sign-in`}
            onChange={(ev) => control.mutate({ path: '/tuneup/startup', body: { items: [e.id], value: ev.target.checked ? 'on' : 'off' }, label: e.name })}
            className="peer sr-only"
          />
          <span aria-hidden className="h-5 w-9 rounded-full bg-border transition-colors peer-checked:bg-accent peer-focus-visible:outline-2 peer-focus-visible:outline-accent" />
          <span aria-hidden className="absolute left-0.5 top-0.5 size-4 rounded-full bg-surface shadow transition-transform peer-checked:translate-x-4" />
        </label>
      ),
    },
  ];
  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-muted">
        Each program here starts when you sign in and keeps running. Turning one off keeps it installed; it simply no longer starts by itself (the next sign-in).
      </p>
      {!entries ? (
        <p className="text-sm text-muted">Reading the startup programs…</p>
      ) : (
        <DataTable
          viewKey="tuneup.startup"
          label="Startup programs"
          rows={entries}
          columns={columns}
          rowKey={(e) => e.id}
          defaultView={{ groupBy: ['state'], sort: [{ id: 'name', desc: false }] }}
          emptyText="Nothing starts at sign-in."
        />
      )}
    </div>
  );
}

// Clean-up.

const CLEANUP_COLUMNS: Column<CleanupTarget>[] = [
  { id: 'name', header: 'Name', value: (t) => t.name, groupable: false },
  { id: 'size', header: 'Size', value: (t) => t.bytes, groupable: false, firstDesc: true },
  { id: 'files', header: 'Files', value: (t) => t.files, groupable: false, firstDesc: true },
  { id: 'choice', header: 'Selected first', value: (t) => (t.recommended ? 'Safe to clean' : 'Your choice') },
  { id: 'empty', header: 'Contents', value: (t) => (t.bytes > 0 ? 'Something to remove' : 'Already empty') },
];

/** What can be removed, measured; the safe ones are selected; one button cleans. */
export function CleanupTab({ targets, ask, onRescan, scanning }: { targets: CleanupTarget[] | undefined; ask: (action: PendingAction) => void; onRescan: () => void; scanning: boolean }) {
  const control = useControlAction();
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [selected, setSelected] = useState<Set<string> | null>(null);
  useEffect(() => {
    if (targets && selected === null) setSelected(new Set(targets.filter((t) => t.recommended && t.bytes > 0).map((t) => t.id)));
  }, [targets, selected]);
  const chosen = (targets ?? []).filter((t) => selected?.has(t.id));
  const total = chosen.reduce((sum, t) => sum + t.bytes, 0);

  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-muted">
        Temporary files and caches that Windows and programs make again by themselves. Files in use are left; your documents, settings and sign-ins are never touched.
      </p>
      {!targets ? (
        <p className="text-sm text-muted">Measuring…</p>
      ) : (
        <DataTable
          viewKey="tuneup.cleanup"
          label="Clean-up targets"
          rows={targets}
          columns={CLEANUP_COLUMNS}
          rowKey={(t) => t.id}
          defaultView={{ groupBy: [], sort: [] }}
          listClassName="divide-y divide-border rounded-md border border-border"
          renderRow={(t) => (
            <div>
              <div className="flex items-start">
              <button
                type="button"
                aria-expanded={expanded.has(t.id)}
                aria-label={`${expanded.has(t.id) ? 'Hide' : 'Show'} the files of ${t.name}`}
                onClick={() =>
                  setExpanded((x) => {
                    const next = new Set(x);
                    if (next.has(t.id)) next.delete(t.id);
                    else next.add(t.id);
                    return next;
                  })
                }
                className="mt-3 ml-2 rounded p-0.5 text-muted hover:bg-accent/10 hover:text-foreground"
              >
                <ChevronRight className={cn('size-4 transition-transform', expanded.has(t.id) && 'rotate-90')} aria-hidden />
              </button>
              <label className={cn('flex flex-1 cursor-pointer items-start gap-3 p-3 pl-2', t.bytes === 0 && 'opacity-60')}>
                <input
                  type="checkbox"
                  checked={selected?.has(t.id) ?? false}
                  disabled={t.bytes === 0}
                  onChange={(e) =>
                    setSelected((s) => {
                      const next = new Set(s ?? []);
                      if (e.target.checked) next.add(t.id);
                      else next.delete(t.id);
                      return next;
                    })
                  }
                  className="mt-1 size-4 accent-[var(--accent)]"
                />
                <span className="min-w-0 flex-1">
                  <span className="flex flex-wrap items-baseline justify-between gap-2">
                    <span className="text-sm font-medium">{t.name}</span>
                    <span className="text-sm tabular-nums">
                      {formatBytes(t.bytes)} <span className="text-xs text-muted">· {t.files.toLocaleString()} {t.id === 'recycle-bin' ? 'items' : 'files'}</span>
                    </span>
                  </span>
                  <span className="block text-xs text-muted">{t.description}</span>
                  {t.caution && <span className="mt-0.5 block text-xs text-warning">{t.caution}</span>}
                </span>
              </label>
              </div>
              {expanded.has(t.id) && (
                <div className="px-3 pb-3">
                  <CleanupTree target={t.id} />
                </div>
              )}
            </div>
          )}
        />
      )}
      <div className="flex flex-wrap items-center gap-2">
        <Button
          disabled={chosen.length === 0 || control.isPending}
          onClick={() =>
            ask({
              title: `Remove ${formatBytes(total)}?`,
              confirmLabel: 'Clean up',
              danger: chosen.some((t) => !t.recommended),
              body: (
                <ul className="list-disc pl-5 text-xs">
                  {chosen.map((t) => (
                    <li key={t.id}>
                      {t.name}: {formatBytes(t.bytes)}
                      {t.caution ? <span className="text-warning"> ({t.caution})</span> : null}
                    </li>
                  ))}
                </ul>
              ),
              run: () => control.mutate({ path: '/tuneup/clean', body: { items: chosen.map((t) => t.id) }, label: 'The clean-up' }, { onSuccess: () => onRescan() }),
            })
          }
        >
          {control.isPending ? 'Cleaning…' : `Clean up ${formatBytes(total)}`}
        </Button>
        <Button variant="outline" disabled={scanning} onClick={onRescan}>
          {scanning ? 'Measuring…' : 'Measure again'}
        </Button>
      </div>
    </div>
  );
}

// System.

type Volume = TuneUpReport['system']['volumes'][number];

const freeShare = (v: Volume) => (v.totalBytes ? v.freeBytes / v.totalBytes : 1);

const DRIVE_COLUMNS: Column<Volume>[] = [
  { id: 'name', header: 'Drive', value: (v) => v.name, groupable: false },
  { id: 'free', header: 'Free', value: (v) => v.freeBytes, groupable: false, firstDesc: true },
  { id: 'share', header: 'Free share', value: freeShare, groupable: false },
  { id: 'size', header: 'Size', value: (v) => v.totalBytes, groupable: false, firstDesc: true },
  { id: 'state', header: 'Space', value: (v) => (freeShare(v) < 0.1 ? 'Nearly full' : freeShare(v) < 0.2 ? 'Getting full' : 'Enough space') },
];

/** Power plan, hibernation, Game Mode, GPU scheduling, memory, DNS and WSL, each with what it does. */
export function SystemTab({ report, ask }: { report: TuneUpReport | undefined; ask: (action: PendingAction) => void }) {
  const control = useControlAction();
  const s = report?.system;
  if (!s) return <p className="text-sm text-muted">Reading the settings…</p>;
  const apply = (action: string, value?: string, label?: string) => control.mutate({ path: `/tuneup/${action}`, body: { value }, label });
  const toggle = (title: string, action: string, on: boolean, body: string, restart = false) =>
    ask({ title, confirmLabel: on ? 'Turn on' : 'Turn off', body: <p>{body}{restart ? ' It takes effect when Windows restarts.' : ''}</p>, run: () => apply(action, on ? 'on' : 'off', title) });

  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>Power plan</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-2">
          <p className="text-xs text-muted">How fast the processor may run. Balanced gives full speed when needed; High performance keeps it ready all the time (more power and heat); Power saver holds it back.</p>
          <div role="radiogroup" aria-label="Power plan" className="flex flex-col gap-1.5">
            {s.powerPlans.map((plan) => (
              <label key={plan.id} className="flex items-center gap-2 text-sm">
                <input type="radio" name="power-plan" checked={plan.active} onChange={() => apply('power-plan', plan.id, `The ${plan.name} power plan`)} className="accent-[var(--accent)]" />
                {plan.name}
              </label>
            ))}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Memory</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-2 text-sm">
          <p>
            Standby cache <span className="font-medium tabular-nums">{formatBytes(s.standbyBytes)}</span> · free <span className="font-medium tabular-nums">{formatBytes(s.freeBytes)}</span>
          </p>
          <p className="text-xs text-muted">Standby memory holds recently used files; Windows gives it to any program that needs memory, so it is not wasted. Emptying it helps only with stutter right after a game starts.</p>
          <Button
            size="sm"
            variant="outline"
            className="self-start"
            onClick={() => ask({ title: 'Empty the standby memory?', confirmLabel: 'Empty it', body: <p>Programs read their files from the disk again until the cache refills.</p>, run: () => apply('free-standby', undefined, 'Emptying standby memory') })}
          >
            Empty standby memory
          </Button>
          {s.wslRunning && (
            <div className="mt-2 flex flex-col gap-1.5 border-t border-border pt-2">
              <p>
                WSL / virtual machine holds <span className="font-medium tabular-nums">{formatBytes(s.wslBytes)}</span>
              </p>
              <Button
                size="sm"
                variant="outline"
                className="self-start"
                onClick={() =>
                  ask({
                    title: 'Shut down WSL?',
                    confirmLabel: 'Shut down',
                    danger: true,
                    body: <p>Linux programs and Docker containers running in WSL stop; unsaved work in them is lost. It starts again when needed.</p>,
                    run: () => apply('shutdown-wsl', undefined, 'Shutting down WSL'),
                  })
                }
              >
                Shut down WSL
              </Button>
            </div>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Gaming</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3 text-sm">
          <Setting
            label="Game Mode"
            help="Windows gives a running game priority and holds back updates and notifications while you play."
            on={s.gameMode}
            onChange={(on) => toggle(`Turn ${on ? 'on' : 'off'} Game Mode?`, 'game-mode', on, on ? 'Windows favors games while they run.' : 'Windows treats games like other programs.')}
          />
          <Setting
            label="Hardware-accelerated GPU scheduling"
            help="The graphics card schedules its own work, which can lower latency. Some older games or capture tools misbehave with it."
            on={s.gpuScheduling}
            onChange={(on) => toggle(`Turn ${on ? 'on' : 'off'} hardware-accelerated GPU scheduling?`, 'gpu-scheduling', on, 'The graphics card schedules its own work.', true)}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Windows</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3 text-sm">
          <Setting
            label={`Hibernation${s.hibernationFileBytes ? ` (${formatBytes(s.hibernationFileBytes)} reserved)` : ''}`}
            help="Saves memory to the disk to power off fully; also used by Fast Startup. A desktop rarely needs it; turning it off frees the space and makes each start a full start."
            on={s.hibernationEnabled}
            onChange={(on) => toggle(`Turn ${on ? 'on' : 'off'} hibernation?`, 'hibernation', on, on ? 'Windows can hibernate and use Fast Startup again.' : 'Hibernation and Fast Startup turn off; hiberfil.sys is removed. Sleep still works.')}
          />
          <div className="flex items-start justify-between gap-4">
            <span>
              <span className="font-medium">DNS cache</span>
              <span className="block text-xs text-muted">Clear it when a site moved or a name resolves wrongly.</span>
            </span>
            <Button size="sm" variant="outline" onClick={() => apply('flush-dns', undefined, 'Clearing the DNS cache')}>
              Clear
            </Button>
          </div>
          {s.volumes.length > 0 && (
            <div className="border-t border-border pt-2">
              <p className="mb-1 font-medium">Drives</p>
              <DataTable
                viewKey="tuneup.drives"
                label="Drives"
                rows={s.volumes}
                columns={DRIVE_COLUMNS}
                rowKey={(v) => v.name}
                defaultView={{ groupBy: [], sort: [] }}
                compact
                renderRow={(v) => {
                  const share = freeShare(v);
                  return (
                    <p className={cn('flex justify-between text-xs', share < 0.1 ? 'text-danger' : share < 0.2 ? 'text-warning' : 'text-muted')}>
                      <span>
                        {v.name} {v.label ?? ''}
                      </span>
                      <span className="tabular-nums">
                        {formatBytes(v.freeBytes)} free of {formatBytes(v.totalBytes)} ({Math.round(share * 100)}%)
                      </span>
                    </p>
                  );
                }}
              />
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function Setting({ label, help, on, onChange }: { label: string; help: string; on: boolean | null; onChange: (on: boolean) => void }) {
  return (
    <label className="flex cursor-pointer items-start justify-between gap-4">
      <span>
        <span className="font-medium">{label}</span>
        <span className="block text-xs text-muted">{help}</span>
        {on === null && <span className="block text-xs text-muted">Windows' default (not set).</span>}
      </span>
      <span className="relative mt-0.5 inline-flex shrink-0">
        <input type="checkbox" checked={on === true} onChange={(e) => onChange(e.target.checked)} aria-label={label} className="peer sr-only" />
        <span aria-hidden className="h-5 w-9 rounded-full bg-border transition-colors peer-checked:bg-accent peer-focus-visible:outline-2 peer-focus-visible:outline-accent" />
        <span aria-hidden className="absolute left-0.5 top-0.5 size-4 rounded-full bg-surface shadow transition-transform peer-checked:translate-x-4" />
      </span>
    </label>
  );
}

// Advice.

const LEVEL_ICON = { high: AlertTriangle, medium: Info, low: Info } as const;

const LEVEL_TEXT = { high: 'Matters now', medium: 'Worth doing', low: 'Small gain' } as const;

const ADVICE_COLUMNS: Column<Recommendation>[] = [
  { id: 'level', header: 'Importance', value: (r) => ({ high: 0, medium: 1, low: 2 })[r.level], group: (r) => LEVEL_TEXT[r.level] },
  { id: 'title', header: 'Advice', value: (r) => r.title, groupable: false },
  { id: 'step', header: 'Step', value: (r) => (!r.action ? 'For information' : r.action.kind === 'apply' ? 'Can be done here' : 'Look at a tab') },
];

/** What would help most now, each with why and the step that deals with it. */
export function AdviceTab({ advice, loading, onTab, ask }: { advice: Recommendation[]; loading: boolean; onTab: (tab: TuneUpTab) => void; ask: (action: PendingAction) => void }) {
  const control = useControlAction();
  if (loading) return <p className="text-sm text-muted">Looking at the PC…</p>;
  if (advice.length === 0) {
    return (
      <p className="flex items-center gap-2 rounded-lg border border-success/40 bg-success/10 px-3 py-2 text-sm">
        <CheckCircle2 className="size-4 text-success" aria-hidden /> Nothing to improve right now: memory, drives, startup and settings look fine.
      </p>
    );
  }

  return (
    <DataTable
      viewKey="tuneup.advice"
      label="Advice"
      rows={advice}
      columns={ADVICE_COLUMNS}
      rowKey={(r) => r.id}
      defaultView={{ groupBy: [], sort: [] }}
      listClassName="gap-2"
      renderRow={(r) => {
        const Icon = LEVEL_ICON[r.level];
        return (
          <div className={cn('flex flex-wrap items-start gap-3 rounded-lg border px-3 py-2.5', r.level === 'high' ? 'border-danger/40 bg-danger/5' : r.level === 'medium' ? 'border-warning/40 bg-warning/5' : 'border-border')}>
            <Icon className={cn('mt-0.5 size-4 shrink-0', r.level === 'high' ? 'text-danger' : r.level === 'medium' ? 'text-warning' : 'text-muted')} aria-hidden />
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium">{r.title}</p>
              <p className="text-xs text-muted">{r.detail}</p>
            </div>
            {r.action && (
              <Button
                size="sm"
                variant={r.action.kind === 'apply' ? 'default' : 'outline'}
                onClick={() => {
                  const action = r.action!;
                  if (action.kind === 'tab') onTab(action.tab);
                  else
                    ask({
                      title: `${action.label}?`,
                      confirmLabel: action.label,
                      body: <p>{action.confirm}</p>,
                      danger: action.action === 'end-processes' || action.action === 'shutdown-wsl',
                      run: (confirmed) => control.mutate({ path: `/tuneup/${action.action}`, body: { items: action.items, value: action.value, confirmed }, label: action.label }),
                    });
                }}
              >
                {r.action.label}
              </Button>
            )}
          </div>
        );
      }}
    />
  );
}
