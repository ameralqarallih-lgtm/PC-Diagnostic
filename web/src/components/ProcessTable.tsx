import { Search } from 'lucide-react';
import { useMemo, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import type { MomentService, ProcessAtTime } from '@/lib/api/types';
import { formatBytes, formatBytesPerSecond, formatDateTime, formatPercent, formatTime } from '@/lib/format';

/** The program file's signature in words, for the row's tooltip. */
export function signatureText(p: ProcessAtTime): string | null {
  switch (p.signature) {
    case 'Embedded':
      return `Signed by ${p.signer ?? 'an unnamed publisher'}`;
    case 'Catalog':
      return `Signed by ${p.signer ?? 'Windows'} (Windows catalog)`;
    case 'Package':
      return `Signed app package by ${p.signer ?? 'an unnamed publisher'}`;
    case 'NotSigned':
      return 'Not signed';
    case 'NotTrusted':
      return `Signature not valid${p.signer ? ` (claims ${p.signer})` : ''}: the file may have been changed after signing`;
    default:
      return null;
  }
}

const SIGNATURE: Record<string, string> = {
  Embedded: 'Signed',
  Catalog: 'Signed (Windows catalog)',
  Package: 'App package',
  NotSigned: 'Not signed',
  NotTrusted: 'Signature not valid',
};

const NOT_MEASURED = 'Running, but not among the busiest processes at this moment, so its figures were not stored';

export interface ProcessTableProps {
  processes: ProcessAtTime[];
  filterable?: boolean;
  /** The filter text, when the caller controls it (e.g. to show the process a service runs in). */
  filter?: string;
  onFilterChange?: (filter: string) => void;
  /** The services each process hosts, by process key. */
  services?: Map<number, MomentService[]>;
  /** Shows a process's services (e.g. on the Services tab). */
  onShowServices?: (process: ProcessAtTime) => void;
  /** Where the user's grouping and sorting is remembered (one per place the table appears). */
  viewKey?: string;
}

/**
 * Processes at a moment, grouped and sorted as the user likes (by default sorted by CPU; group by program, signature,
 * publisher or state) and (optionally) filtered by name, path, process ID or a service it hosts. Processes known only
 * to have been running (no stored figures) have no figures to sort by, so they follow the measured ones.
 */
export function ProcessTable({ processes, filterable = false, filter: controlled, onFilterChange, services, onShowServices, viewKey = 'processes' }: ProcessTableProps) {
  const [own, setOwn] = useState('');
  const filter = controlled ?? own;
  const setFilter = onFilterChange ?? setOwn;
  // With GPU figures for this moment, a measured process without any used no GPU (rather than unknown).
  const gpuMeasured = processes.some((p) => p.gpuPercent != null);
  // Per-process I/O measured disk and network separately for this sample.
  const trueIo = processes.some((p) => p.netBytesPerSec != null);
  const vramMeasured = processes.some((p) => p.gpuMemoryBytes != null);
  const rows = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return processes;
    const hosts = (p: ProcessAtTime) =>
      services?.get(p.procKey)?.some((s) => s.name.toLowerCase().includes(needle) || s.displayName.toLowerCase().includes(needle)) ?? false;
    return processes.filter((p) => p.name.toLowerCase().includes(needle) || (p.path?.toLowerCase().includes(needle) ?? false) || String(p.pid) === needle || hosts(p));
  }, [processes, filter, services]);

  const measured = (p: ProcessAtTime) => p.measured !== false;
  const io = (p: ProcessAtTime) => (p.ioReadBytesPerSec == null && p.ioWriteBytesPerSec == null ? null : (p.ioReadBytesPerSec ?? 0) + (p.ioWriteBytesPerSec ?? 0));
  const columns: Column<ProcessAtTime>[] = [
    {
      id: 'name',
      header: 'Process',
      value: (p) => p.name,
      className: 'max-w-64',
      cell: (p) => <ProcessCell process={p} services={services?.get(p.procKey) ?? []} onShowServices={onShowServices} />,
    },
    { id: 'pid', header: 'PID', value: (p) => p.pid, groupable: false, align: 'right', className: 'font-mono text-xs text-muted' },
    { id: 'cpu', header: 'CPU', value: (p) => (measured(p) ? p.cpuPercent : null), cell: (p) => (!measured(p) ? '—' : p.cpuPercent == null ? <JustStarted /> : formatPercent(p.cpuPercent, 1)), groupable: false, align: 'right', firstDesc: true },
    {
      id: 'memory',
      header: 'Memory',
      value: (p) => (measured(p) ? (p.privateBytes ?? p.workingSetBytes) : null),
      cell: (p) => (measured(p) ? formatBytes(p.privateBytes ?? p.workingSetBytes) : '—'),
      groupable: false,
      align: 'right',
      firstDesc: true,
    },
    {
      id: 'gpu',
      header: 'GPU',
      value: (p) => (!measured(p) ? null : (p.gpuPercent ?? (gpuMeasured ? 0 : null))),
      cell: (p) => (!measured(p) ? '—' : p.gpuPercent != null ? formatPercent(p.gpuPercent, 1) : gpuMeasured ? formatPercent(0, 1) : '—'),
      groupable: false,
      align: 'right',
      firstDesc: true,
    },
    {
      id: 'vram',
      header: 'GPU memory',
      title: 'Dedicated GPU memory in use (all adapters)',
      value: (p) => (!measured(p) ? null : (p.gpuMemoryBytes ?? (vramMeasured ? 0 : null))),
      cell: (p) => (!measured(p) ? '—' : p.gpuMemoryBytes ? formatBytes(p.gpuMemoryBytes) : vramMeasured ? formatBytes(0) : '—'),
      groupable: false,
      align: 'right',
      firstDesc: true,
    },
    {
      id: 'io',
      header: 'Disk I/O',
      title: trueIo ? 'Every transfer; the disk share below it' : undefined,
      value: (p) => (measured(p) ? io(p) : null),
      cell: (p) =>
        measured(p) ? (
          <>
            {io(p) == null ? '—' : formatBytesPerSecond(io(p))}
            {p.diskBytesPerSec != null && <div className="text-xs text-muted">disk {formatBytesPerSecond(p.diskBytesPerSec)}</div>}
          </>
        ) : (
          '—'
        ),
      groupable: false,
      align: 'right',
      firstDesc: true,
    },
    {
      id: 'network',
      header: 'Network',
      title: trueIo ? 'Received and sent' : 'Per-process network is measured with per-process I/O (Settings › Enhanced mode, on by default; needs a Pro license and the recorder running as administrator).',
      value: (p: ProcessAtTime) => (measured(p) ? (p.netBytesPerSec ?? null) : null),
      cell: (p: ProcessAtTime) => (measured(p) && p.netBytesPerSec != null ? formatBytesPerSecond(p.netBytesPerSec) : '—'),
      groupable: false,
      align: 'right' as const,
      firstDesc: true,
    },
    { id: 'signature', header: 'Signature', value: (p) => (p.signature ? (SIGNATURE[p.signature] ?? p.signature) : null), hidden: true },
    { id: 'publisher', header: 'Publisher', value: (p) => p.signer ?? null, hidden: true },
    { id: 'state', header: 'State', value: (p) => p.state, hidden: true },
    { id: 'figures', header: 'Figures', value: (p) => (measured(p) ? 'Stored' : 'Not stored'), hidden: true },
  ];

  return (
    <DataTable
      viewKey={viewKey}
      label="Processes"
      rows={rows}
      columns={columns}
      rowKey={(p) => p.procKey}
      defaultView={{ groupBy: [], sort: [{ id: 'cpu', desc: true }] }}
      emptyText="No process matches."
      fill
      compact
      toolbarStart={
        filterable ? (
          <label className="flex min-w-48 flex-1 items-center gap-2 rounded-md border border-border px-2 py-1 text-sm sm:max-w-72">
            <Search className="size-3.5 text-muted" aria-hidden />
            <input
              type="search"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              placeholder={services ? 'Filter by name, path, PID or service' : 'Filter by name, path or PID'}
              aria-label="Filter processes"
              className="w-full bg-transparent outline-none"
            />
          </label>
        ) : undefined
      }
    />
  );
}

/**
 * A process with figures but no CPU use: seen in one sample only. CPU use is the CPU time used between two samples (2 s
 * apart), so a process has none in the sample it first appears in, and one that runs for less than that never gets one.
 */
function JustStarted() {
  return (
    <span className="text-xs text-muted" title="Seen in one sample only. CPU use is measured between two samples (2 seconds apart), so a process has none in the sample it first appears in; a program that runs for less than that never gets one.">
      Just started
    </span>
  );
}

function ProcessCell({ process: p, services, onShowServices }: { process: ProcessAtTime; services: MomentService[]; onShowServices?: (process: ProcessAtTime) => void }) {
  const measured = p.measured !== false;
  const started = p.startTs != null && p.startTs > 0 ? `Started ${formatDateTime(new Date(p.startTs).toISOString())}` : null;
  const asOf = p.figuresTs != null ? formatTime(new Date(p.figuresTs).toISOString()) : null;
  return (
    <div
      className="truncate"
      title={[p.path ?? p.name, started, signatureText(p), asOf ? `Figures measured at ${asOf} (unchanged until then, or the last 30 s)` : null, measured ? null : NOT_MEASURED]
        .filter(Boolean)
        .join('\n')}
    >
      <span className={measured ? undefined : 'text-muted'}>{p.name}</span> {p.state !== 'Running' && <Badge tone="warning">{p.state}</Badge>}{' '}
      {p.signature === 'NotSigned' && <Badge tone="neutral">Not signed</Badge>}
      {p.signature === 'NotTrusted' && <Badge tone="danger">Signature not valid</Badge>}
      {asOf && <span className="ml-1 text-xs text-muted">as of {asOf}</span>}
      {!measured && <span className="ml-1 text-xs text-muted">running · no figures stored</span>}
      {services.length > 0 && (
        <button
          type="button"
          className="block max-w-full truncate text-left text-xs text-accent hover:underline"
          title={services.map((h) => `${h.displayName} (${h.name})`).join('\n')}
          onClick={() => onShowServices?.(p)}
        >
          {services.length === 1 ? 'Service' : `${services.length} services`}: {services.map((h) => h.displayName).join(', ')}
        </button>
      )}
    </div>
  );
}
