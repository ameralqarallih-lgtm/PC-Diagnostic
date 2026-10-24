import { MoreHorizontal, Search, Shield, ShieldAlert } from 'lucide-react';
import { useMemo, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Menu, type MenuItem } from '@/components/ui/menu';
import type { ControlProcess } from '@/lib/api/types';
import { formatBytes, formatBytesPerSecond, formatPercent } from '@/lib/format';
import type { PendingAction } from './ConfirmAction';
import { useControlAction } from './useControl';

export const KIND_TEXT: Record<ControlProcess['kind'], string> = {
  app: 'Apps (with a window)',
  background: 'Background programs',
  service: 'Running services',
  windows: 'Windows',
};

const KIND_ORDER: Record<ControlProcess['kind'], number> = { app: 0, background: 1, service: 2, windows: 3 };

const PRIORITIES = [
  { value: 'High', label: 'High' },
  { value: 'AboveNormal', label: 'Above normal' },
  { value: 'Normal', label: 'Normal' },
  { value: 'BelowNormal', label: 'Below normal' },
  { value: 'Idle', label: 'Low (idle)' },
];

const keyOf = (p: ControlProcess) => `${p.pid}:${p.startTicks}`;

/** Confirmations for ending processes: what is lost, and parts of Windows ticked off explicitly. */
export function endAction(processes: ControlProcess[], run: (confirmed: boolean) => void, tree = false): PendingAction {
  const windows = processes.filter((p) => p.protection === 'Caution');
  const one = processes.length === 1 ? processes[0]! : null;
  return {
    title: one ? `End ${one.name}${tree ? ' and what it started' : ''}?` : `End ${processes.length} processes?`,
    confirmLabel: one ? 'End process' : `End ${processes.length} processes`,
    danger: true,
    body: (
      <>
        <p>Unsaved work in {one ? 'it' : 'them'} is lost. Programs can be started again; a service host or helper may be started again by Windows or its program.</p>
        {!one && (
          <ul className="max-h-40 list-disc overflow-auto pl-5 text-xs text-muted">
            {processes.map((p) => (
              <li key={keyOf(p)}>
                {p.name} (PID {p.pid}){p.workingSetBytes ? `, ${formatBytes(p.workingSetBytes)}` : ''}
              </li>
            ))}
          </ul>
        )}
        {windows.map((p) => (
          <p key={keyOf(p)} className="text-xs text-warning">
            {p.name}: {p.protectionReason}
          </p>
        ))}
      </>
    ),
    acknowledge: windows.length > 0 ? 'I understand that ending a part of Windows can make Windows misbehave until it restarts.' : undefined,
    run,
  };
}

/** The actions on one process (also used by the dashboard's process lists). */
export function useProcessMenu(ask: (action: PendingAction) => void) {
  const control = useControlAction();
  return (process: ControlProcess): MenuItem[] => {
    const locked = process.protection === 'Protected';
    const why = locked ? `Protected: ${process.protectionReason}` : undefined;
    const end = (tree: boolean) =>
      ask(
        endAction(
          [process],
          (confirmed) => control.mutate({ path: `/control/processes/${process.pid}/end`, body: { start: process.startTicks, tree, confirmed }, label: `Ending ${process.name}` }),
          tree,
        ),
      );
    return [
      { label: 'End process', onSelect: () => end(false), danger: true, disabled: locked, title: why },
      { label: 'End it and what it started', onSelect: () => end(true), danger: true, disabled: locked, title: why },
      ...PRIORITIES.map((p, i) => ({
        label: `${p.label}${process.priority === p.value ? ' ✓' : ''}`,
        section: i === 0 ? 'Priority' : undefined,
        disabled: locked || process.priority === p.value,
        title: why,
        onSelect: () => control.mutate({ path: `/control/processes/${process.pid}/priority`, body: { start: process.startTicks, priority: p.value }, label: `Changing ${process.name}'s priority` }),
      })),
    ];
  };
}

/** Every running process, grouped (apps, background programs, services, Windows), with actions and bulk ending. */
export function ProcessesTab({ processes, ask }: { processes: ControlProcess[] | undefined; ask: (action: PendingAction) => void }) {
  const [filter, setFilter] = useState('');
  const [selected, setSelected] = useState<Set<string>>(() => new Set());
  const control = useControlAction();
  const menu = useProcessMenu(ask);
  const text = filter.trim().toLowerCase();
  const rows = useMemo(
    () => (processes ?? []).filter((p) => !text || `${p.name} ${p.pid} ${p.publisher ?? ''} ${p.services.join(' ')}`.toLowerCase().includes(text)),
    [processes, text],
  );
  const chosen = (processes ?? []).filter((p) => selected.has(keyOf(p)));
  const toggle = (p: ControlProcess, on: boolean) =>
    setSelected((s) => {
      const next = new Set(s);
      if (on) next.add(keyOf(p));
      else next.delete(keyOf(p));
      return next;
    });

  const columns: Column<ControlProcess>[] = [
    {
      id: 'select',
      header: '',
      value: () => null,
      sortable: false,
      groupable: false,
      className: 'w-7',
      cell: (p) => (
        <input
          type="checkbox"
          aria-label={`Select ${p.name} (${p.pid})`}
          disabled={p.protection === 'Protected'}
          checked={selected.has(keyOf(p))}
          onChange={(e) => toggle(p, e.target.checked)}
          className="size-3.5 accent-[var(--accent)]"
        />
      ),
    },
    {
      id: 'name',
      header: 'Process',
      value: (p) => p.name,
      groupable: false,
      className: 'max-w-72',
      cell: (p) => (
        <span className="flex min-w-0 items-center gap-1.5" title={p.path ?? p.name}>
          {p.protection === 'Protected' ? (
            <Shield className="size-3.5 shrink-0 text-muted" aria-label="Protected" />
          ) : p.protection === 'Caution' ? (
            <ShieldAlert className="size-3.5 shrink-0 text-warning" aria-label="Part of Windows" />
          ) : null}
          <span className="truncate">{p.name}</span>
          {p.notResponding && <Badge tone="warning">Not responding</Badge>}
          {p.services.length > 0 && <span className="truncate text-xs text-muted">{p.services.slice(0, 3).join(', ')}{p.services.length > 3 ? '…' : ''}</span>}
        </span>
      ),
    },
    { id: 'kind', header: 'Kind', value: (p) => KIND_ORDER[p.kind], group: (p) => KIND_TEXT[p.kind], hidden: true },
    { id: 'pid', header: 'PID', value: (p) => p.pid, groupable: false, align: 'right', className: 'w-16 font-mono text-xs text-muted' },
    { id: 'cpu', header: 'CPU', value: (p) => p.cpuPercent, groupable: false, align: 'right', firstDesc: true, className: 'w-16', cell: (p) => (p.cpuPercent == null ? '—' : `${p.cpuPercent.toFixed(1)}%`) },
    { id: 'memory', header: 'Memory', value: (p) => p.workingSetBytes, groupable: false, align: 'right', firstDesc: true, className: 'w-24', cell: (p) => formatBytes(p.workingSetBytes) },
    { id: 'gpu', header: 'GPU', value: (p) => p.gpuPercent ?? null, groupable: false, align: 'right', firstDesc: true, className: 'w-16', cell: (p) => (p.gpuPercent == null ? '—' : formatPercent(p.gpuPercent, 1)) },
    { id: 'vram', header: 'GPU memory', value: (p) => p.gpuMemoryBytes ?? null, groupable: false, align: 'right', firstDesc: true, className: 'w-24', cell: (p) => (p.gpuMemoryBytes == null ? '—' : formatBytes(p.gpuMemoryBytes)) },
    {
      id: 'network',
      header: 'Network',
      title: 'Received and sent (per-process I/O, Settings › Enhanced mode)',
      value: (p) => p.netBytesPerSec ?? null,
      groupable: false,
      align: 'right',
      firstDesc: true,
      className: 'w-24',
      cell: (p) => (p.netBytesPerSec == null ? '—' : formatBytesPerSecond(p.netBytesPerSec)),
    },
    { id: 'publisher', header: 'Publisher', value: (p) => p.publisher, className: 'max-w-48 truncate text-xs text-muted', cell: (p) => p.publisher ?? '—' },
    { id: 'priority', header: 'Priority', value: (p) => p.priority, className: 'text-xs text-muted', cell: (p) => PRIORITIES.find((x) => x.value === p.priority)?.label ?? p.priority ?? '—' },
    { id: 'state', header: 'Responding', value: (p) => (p.notResponding ? 'Not responding' : 'Responding'), hidden: true },
    {
      id: 'actions',
      header: '',
      value: () => null,
      sortable: false,
      groupable: false,
      className: 'w-8',
      cell: (p) => (
        <Menu label={`Actions for ${p.name} (${p.pid})`} items={menu(p)}>
          <MoreHorizontal aria-hidden />
        </Menu>
      ),
    },
  ];

  const otherBackground = (processes ?? []).filter((p) => p.kind === 'background' && p.protection === 'None' && p.sessionId > 0);

  return (
    <div className="flex flex-col gap-3">
      <p className="text-sm text-muted">
        Everything running now, grouped by what it is. Apps have a window; background programs run without one; Windows and services are best left alone, so Windows' own
        parts ask for confirmation and those it needs are protected (shield).
      </p>
      <div className="flex flex-wrap items-center gap-2">
        <Button
          size="sm"
          variant="danger"
          disabled={chosen.length === 0}
          onClick={() =>
            ask(
              endAction(chosen, (confirmed) =>
                control.mutate({ path: '/tuneup/end-processes', body: { items: chosen.map(keyOf), confirmed }, label: 'Ending processes' }, { onSuccess: () => setSelected(new Set()) }),
              ),
            )
          }
        >
          End selected ({chosen.length})
        </Button>
        <Button
          size="sm"
          variant="outline"
          disabled={otherBackground.length === 0}
          onClick={() => setSelected(new Set(otherBackground.map(keyOf)))}
          title="Background programs in your session that are not part of Windows (review before ending them)"
        >
          Select background programs that are not Windows ({otherBackground.length})
        </Button>
        {selected.size > 0 && (
          <Button size="sm" variant="ghost" onClick={() => setSelected(new Set())}>
            Clear selection
          </Button>
        )}
      </div>
      {!processes ? (
        <p className="text-sm text-muted">Reading the processes…</p>
      ) : (
        <DataTable
          viewKey="tuneup.processes"
          label="Processes"
          rows={rows}
          columns={columns}
          rowKey={keyOf}
          defaultView={{ groupBy: ['kind'], sort: [{ id: 'cpu', desc: true }] }}
          rowClassName={(p) => (p.protection === 'Protected' ? 'text-muted' : undefined)}
          emptyText="No process matches."
          fill
          toolbarStart={
            <label className="relative block w-full max-w-sm">
              <span className="sr-only">Filter processes</span>
              <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
              <input
                type="search"
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder="Filter by name, PID, publisher or service"
                className="h-8 w-full rounded-md border border-border bg-background pl-7 pr-2 text-sm placeholder:text-muted"
              />
            </label>
          }
        />
      )}
    </div>
  );
}
