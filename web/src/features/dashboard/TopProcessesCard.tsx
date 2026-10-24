import { useQueryClient } from '@tanstack/react-query';
import { motion, useReducedMotion } from 'motion/react';
import { MoreHorizontal, Search } from 'lucide-react';
import { useId, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Menu, type MenuItem } from '@/components/ui/menu';
import { ConfirmAction, type PendingAction } from '@/features/tuneup/ConfirmAction';
import { useProcessMenu } from '@/features/tuneup/ProcessesTab';
import { apiGet } from '@/lib/api/client';
import type { ControlProcess, LiveProcess, LiveProcesses } from '@/lib/api/types';
import { formatBytes, formatBytesPerSecond, formatPercent } from '@/lib/format';
import { cn } from '@/lib/utils';

const TABS = [
  { id: 'cpu', label: 'CPU', list: (p: LiveProcesses) => p.byCpu, raw: (p: LiveProcess) => p.cpuPercent, value: (p: LiveProcess) => formatPercent(p.cpuPercent, 1), bar: 'bg-[#38bdf8]' },
  {
    id: 'memory',
    label: 'Memory',
    list: (p: LiveProcesses) => p.byMemory,
    raw: (p: LiveProcess) => p.privateBytes ?? p.workingSetBytes,
    value: (p: LiveProcess) => formatBytes(p.privateBytes ?? p.workingSetBytes),
    bar: 'bg-[#a78bfa]',
  },
  { id: 'gpu', label: 'GPU', list: (p: LiveProcesses) => p.byGpu, raw: (p: LiveProcess) => p.gpuPercent, value: (p: LiveProcess) => formatPercent(p.gpuPercent, 1), bar: 'bg-[#34d399]' },
  {
    id: 'vram',
    label: 'GPU memory',
    list: (p: LiveProcesses) => p.byGpuMemory ?? [],
    raw: (p: LiveProcess) => p.gpuMemoryBytes ?? null,
    value: (p: LiveProcess) => (p.gpuMemoryBytes == null ? '—' : formatBytes(p.gpuMemoryBytes)),
    bar: 'bg-[#2dd4bf]',
  },
  {
    id: 'io',
    label: 'Disk I/O',
    list: (p: LiveProcesses) => p.byIo,
    raw: (p: LiveProcess) => p.ioBytesPerSec,
    // In Enhanced mode the disk share is known; otherwise this is every transfer together.
    value: (p: LiveProcess) => formatBytesPerSecond(p.ioBytesPerSec),
    bar: 'bg-[#fbbf24]',
  },
  {
    id: 'network',
    label: 'Network',
    list: (p: LiveProcesses) => p.byNetwork ?? [],
    raw: (p: LiveProcess) => p.netBytesPerSec ?? null,
    value: (p: LiveProcess) => (p.netBytesPerSec == null ? '—' : formatBytesPerSecond(p.netBytesPerSec)),
    bar: 'bg-[#f472b6]',
  },
] as const;

type TabId = (typeof TABS)[number]['id'];

export interface TopProcessesCardProps {
  processes: LiveProcesses | null | undefined;
  /** Tab shown first. */
  initialTab?: string;
  /** Rows per list (the Agent sends up to 8). */
  rows?: number;
  bars?: boolean;
  showFilter?: boolean;
  title?: string;
  className?: string;
}

/**
 * The busiest processes by CPU, memory, GPU, disk I/O (and network in Enhanced mode), with live bars; rows glide as the
 * order changes. Each row shows all of its measures; the tab picks which list, the bar and the sort.
 */
export function TopProcessesCard({ processes, initialTab = 'cpu', rows: rowCount = 8, bars = true, showFilter = true, title, className }: TopProcessesCardProps) {
  const [tab, setTab] = useState<TabId>(TABS.some((t) => t.id === initialTab) ? (initialTab as TabId) : 'cpu');
  const [filter, setFilter] = useState('');
  // While the pointer is on the list it holds still (so rows don't move under it); it catches up afterwards.
  const [held, setHeld] = useState<LiveProcesses | null | undefined>(undefined);
  const shown = held !== undefined ? held : processes;
  const baseId = useId();
  const reduced = useReducedMotion() ?? false;
  // Every list is shown; network figures need per-process I/O (kernel events), and say so when they are missing.
  const tabs = TABS;
  const networkMeasured = (shown?.byNetwork ?? null) !== null;
  const active = tabs.find((t) => t.id === tab) ?? TABS[0];
  const all = shown ? active.list(shown) : [];
  const rows = (filter.trim() ? all.filter((p) => p.name.toLowerCase().includes(filter.trim().toLowerCase())) : all).slice(0, rowCount);
  const top = Math.max(...all.map((p) => active.raw(p) ?? 0), active.id === 'cpu' || active.id === 'gpu' ? 100 : 1);
  const queryClient = useQueryClient();
  const [pending, setPending] = useState<PendingAction | null>(null);
  const menuFor = useProcessMenu(setPending);
  // The live list names processes by PID only: their start time and protection come from the recorder when a menu opens.
  const actionsFor = (p: LiveProcess) => async (): Promise<MenuItem[]> => {
    const list = await queryClient.fetchQuery({ queryKey: ['control', 'processes'], queryFn: ({ signal }) => apiGet<ControlProcess[]>('/control/processes', signal), staleTime: 3000 });
    const match = list.find((x) => x.pid === p.pid && x.name.toLowerCase() === p.name.toLowerCase());
    return match ? menuFor(match) : [{ label: 'The process has ended', onSelect: () => {}, disabled: true }];
  };

  return (
    <Card className={className}>
      <CardHeader>
        <CardTitle>{title ?? 'Top processes'}</CardTitle>
        {processes && <span className="text-xs text-muted">{processes.count} running</span>}
      </CardHeader>
      <CardContent>
        <div className="mb-3 flex flex-col gap-2 sm:flex-row sm:items-center">
          <div role="tablist" aria-label="Sort processes by" className="flex flex-1 gap-1 rounded-md bg-surface-muted p-1">
            {tabs.map((t) => (
              <button
                key={t.id}
                id={`${baseId}-${t.id}`}
                type="button"
                role="tab"
                aria-selected={tab === t.id}
                aria-controls={`${baseId}-panel`}
                onClick={() => setTab(t.id)}
                className={cn(
                  'relative flex-1 rounded px-2 py-1 text-xs font-medium',
                  tab === t.id ? 'text-foreground' : 'text-muted hover:text-foreground',
                )}
              >
                {tab === t.id && (
                  <motion.span layoutId={`${baseId}-tab`} className="absolute inset-0 rounded bg-surface shadow-sm" transition={{ duration: reduced ? 0 : 0.25 }} />
                )}
                <span className="relative">{t.label}</span>
              </button>
            ))}
          </div>
          {showFilter && (
          <label className="relative sm:w-40">
            <span className="sr-only">Filter processes</span>
            <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
            <input
              type="search"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              placeholder="Filter"
              className="h-8 w-full rounded-md border border-border bg-background pl-7 pr-2 text-xs placeholder:text-muted"
            />
          </label>
          )}
        </div>

        <div
          id={`${baseId}-panel`}
          role="tabpanel"
          aria-labelledby={`${baseId}-${tab}`}
          onPointerEnter={() => setHeld(processes)}
          onPointerLeave={() => setHeld(undefined)}
        >
          {held !== undefined && <p className="mb-1 text-xs text-muted">Paused while you point at the list</p>}
          {rows.length === 0 ? (
            <p className="text-sm text-muted">
              {!processes
                ? 'Process details appear while the recorder samples.'
                : filter
                  ? `No process matching "${filter}".`
                  : active.id === 'network' && !networkMeasured
                    ? 'Per-process network is measured with per-process I/O (Settings › Enhanced mode, on by default; needs a Pro license and the recorder running as administrator).'
                    : `No process is using the ${active.label.toLowerCase()} right now.`}
            </p>
          ) : (
            <DataTable
              viewKey={`top-processes.all.${active.id}`}
              label={`Top processes by ${active.label}`}
              rows={rows}
              columns={[
                {
                  id: 'name',
                  header: 'Process',
                  value: (p) => p.name,
                  cell: (p) => (
                    <>
                      <div className="flex items-center gap-2 truncate" title={p.name}>
                        <span className="truncate">{p.name}</span>
                        {p.notResponding && (
                          <Badge tone="warning" className="shrink-0">
                            Not responding
                          </Badge>
                        )}
                      </div>
                      {bars && (
                        <div className="mt-1 h-1 rounded-full bg-surface-muted">
                          <motion.div
                            className={cn('h-1 rounded-full', active.bar)}
                            initial={false}
                            animate={{ width: `${Math.min(100, ((active.raw(p) ?? 0) / top) * 100)}%` }}
                            transition={{ duration: reduced ? 0 : 0.6, ease: 'easeOut' }}
                          />
                        </div>
                      )}
                    </>
                  ),
                },
                { id: 'pid', header: 'PID', value: (p) => p.pid, groupable: false, align: 'right', className: 'w-14 font-mono text-xs text-muted' },
                // Every measure of each process, the tab's own stressed (and the list sorted by it).
                ...tabs.map(
                  (t): Column<LiveProcess> => ({
                    id: t.id,
                    header: t.id === 'io' ? 'Disk I/O' : t.label,
                    value: (p) => t.raw(p),
                    groupable: false,
                    align: 'right',
                    firstDesc: true,
                    className: cn('w-20 whitespace-nowrap tabular-nums', t.id === active.id ? 'font-medium text-foreground' : 'text-xs text-muted'),
                    cell: (p) => (
                      <>
                        {t.value(p)}
                        {t.id === 'io' && t.id === active.id && p.diskBytesPerSec != null && <div className="text-xs text-muted">disk {formatBytesPerSecond(p.diskBytesPerSec)}</div>}
                      </>
                    ),
                  }),
                ),
                { id: 'responding', header: 'Responding', value: (p) => (p.notResponding ? 'Not responding' : 'Responding'), hidden: true },
                {
                  id: 'actions',
                  header: '',
                  value: () => null,
                  sortable: false,
                  groupable: false,
                  className: 'w-7',
                  cell: (p) => (
                    <Menu label={`Actions for ${p.name} (${p.pid})`} items={actionsFor(p)}>
                      <MoreHorizontal aria-hidden />
                    </Menu>
                  ),
                },
              ] satisfies Column<LiveProcess>[]}
              rowKey={(p) => p.pid}
              defaultView={{ groupBy: [], sort: [{ id: active.id, desc: true }] }}
              compact
            />
          )}
        </div>
      </CardContent>
      <ConfirmAction pending={pending} onClose={() => setPending(null)} />
    </Card>
  );
}
