import { useQuery } from '@tanstack/react-query';
import { AlertTriangle, CircuitBoard, Fan, MemoryStick, ShieldCheck, Thermometer, Zap } from 'lucide-react';
import type { ReactNode } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { apiGet } from '@/lib/api/client';
import type { SensorInfo, SystemOverview } from '@/lib/api/types';
import { formatBytes } from '@/lib/format';
import { cn } from '@/lib/utils';
import { BOARD_SENSOR_PREFIX, boardMonitor, memoryNotes, type BoardFan, type BoardReading, type BoardStatus } from './board';

type Json = Record<string, unknown>;
const list = (value: unknown): Json[] => (Array.isArray(value) ? (value.filter((v) => v && typeof v === 'object') as Json[]) : []);
const str = (value: unknown): string | null => (typeof value === 'string' && value.trim() ? value.trim() : typeof value === 'number' ? String(value) : null);
const num = (value: unknown): number | null => (typeof value === 'number' && Number.isFinite(value) ? value : typeof value === 'string' && value.trim() && Number.isFinite(Number(value)) ? Number(value) : null);

const STATUS: Record<BoardStatus, { label: string; tone: 'success' | 'warning' | 'danger' | 'neutral' }> = {
  ok: { label: 'Normal', tone: 'success' },
  warning: { label: 'Check', tone: 'warning' },
  critical: { label: 'Act now', tone: 'danger' },
  info: { label: 'Info', tone: 'neutral' },
  doubtful: { label: 'Not a real reading', tone: 'neutral' },
};

/** The motherboard: what it is (board, chipset, firmware, security, slots) and what its monitoring chip measures. */
export function MotherboardTab({ overview, live }: { overview: SystemOverview; live?: boolean }) {
  // On the System page the board's readings refresh every few seconds; an incident shows what was recorded.
  const { data: fresh } = useQuery({
    queryKey: ['system-sensors', BOARD_SENSOR_PREFIX],
    queryFn: ({ signal }) => apiGet<SensorInfo[]>(`/system/sensors?prefix=${encodeURIComponent(BOARD_SENSOR_PREFIX)}`, signal),
    refetchInterval: 5000,
    enabled: live === true,
  });
  const monitor = boardMonitor(fresh ?? overview.sensors);
  const info = overview.information;
  const value = (key: string) => info[key] ?? null;
  const hardware = (overview.hardware ?? {}) as Json;
  const modules = list(hardware.memory_modules);
  const slots = list(hardware.expansion_slots);
  const board = [value('board.manufacturer'), value('board.product')].filter(Boolean).join(' ');
  const notes = memoryNotes(modules.map((m, i) => ({ slot: str(m.DeviceLocator) ?? `Module ${i + 1}`, capacity: num(m.Capacity), configured: num(m.ConfiguredClockSpeed) ?? num(m.Speed), part: str(m.PartNumber) })));

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <CircuitBoard className="size-4 text-accent" aria-hidden /> Motherboard
          </CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-lg font-semibold">{board || 'Not recorded'}</p>
          <Facts
            rows={[
              ['Revision', value('board.version')],
              ['Chipset', value('board.chipset')],
              ['Chipset bridge', value('board.chipset_bridge')],
              ['Processor socket', value('cpu.0.socket')],
              ['Monitoring chip', monitor.chips.join(', ') || null],
            ]}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <ShieldCheck className="size-4 text-accent" aria-hidden /> Firmware and security
          </CardTitle>
        </CardHeader>
        <CardContent>
          <Facts
            rows={[
              ['BIOS', [value('bios.vendor'), value('bios.version')].filter(Boolean).join(' ') || null],
              ['BIOS date', value('bios.date')],
              ['Firmware', value('firmware.type')],
              ['Secure Boot', value('firmware.secure_boot')],
              ['TPM', value('tpm.version') ? `${value('tpm.version')}${value('tpm.maker') ? ` (${value('tpm.maker')})` : ''}${value('tpm.state') ? `, ${value('tpm.state')}` : ''}` : null],
              ['Virtualization', value('cpu.virtualization')],
              ['SMBIOS', value('bios.smbios')],
            ]}
          />
          {value('firmware.secure_boot') === 'off' && <p className="mt-2 text-xs text-muted">Secure Boot is off: Windows 11 and some games' anti-cheat expect it on (Boot settings in the firmware).</p>}
        </CardContent>
      </Card>

      <Card className="xl:col-span-2">
        <CardHeader className="flex-wrap">
          <CardTitle className="flex items-center gap-2">
            <Thermometer className="size-4 text-accent" aria-hidden /> Board monitoring
          </CardTitle>
          <span className="text-xs text-muted">
            {monitor.chips.length ? `${monitor.chips.join(', ')} · ` : ''}
            {live ? 'refreshes every 5 seconds' : 'as recorded'}
          </span>
        </CardHeader>
        <CardContent className="flex flex-col gap-4">
          {monitor.temperatures.length + monitor.fans.length + monitor.voltages.length === 0 ? (
            <p className="text-sm text-muted">
              No readings from the board's monitoring chip. It needs the PawnIO driver (installed with PC Diagnostic) and a chip that LibreHardwareMonitor supports; laptops usually keep these readings in their embedded controller.
            </p>
          ) : (
            <>
              {monitor.attention > 0 && (
                <p className="flex items-center gap-2 rounded-lg border border-warning/40 bg-warning/10 px-3 py-2 text-sm">
                  <AlertTriangle className="size-4 shrink-0 text-warning" aria-hidden /> {monitor.attention} reading{monitor.attention === 1 ? ' needs' : 's need'} a look.
                </p>
              )}
              <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
                <Readings title="Temperatures" icon={Thermometer} rows={monitor.temperatures} format={(r) => (r.value == null ? '—' : `${Math.round(r.value)} °C`)} />
                <Readings
                  title="Fans"
                  icon={Fan}
                  rows={monitor.fans}
                  format={(r) => (r.value == null ? '—' : `${Math.round(r.value)} RPM`)}
                  extra={(r) => ((r as BoardFan).controlPercent != null ? `${Math.round((r as BoardFan).controlPercent!)} %` : null)}
                  extraHeader="Control"
                />
                <Readings title="Voltages" icon={Zap} rows={monitor.voltages} format={(r) => (r.value == null ? '—' : `${r.value.toFixed(3)} V`)} extra={(r) => r.nominal ?? null} extraHeader="Nominal" />
              </div>
              <p className="text-xs text-muted">
                Voltages are judged against the ATX limits (± 5 % of nominal). A reading far from any sensible value is marked as not real: the chip's input is wired or scaled differently on this board.
              </p>
            </>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <MemoryStick className="size-4 text-accent" aria-hidden /> Memory slots
          </CardTitle>
        </CardHeader>
        <CardContent>
          <Facts
            rows={[
              ['Slots used', value('memory.slots') ? `${value('memory.slots_used') ?? modules.length} of ${value('memory.slots')}` : modules.length ? String(modules.length) : null],
              ['Most memory the board takes', num(value('memory.max_bytes')) ? formatBytes(num(value('memory.max_bytes')), 0) : null],
              ['Installed', num(value('memory.total_bytes')) ? formatBytes(num(value('memory.total_bytes'))) : null],
            ]}
          />
          {modules.length > 0 && (
            <div className="mt-2">
              <DataTable
                viewKey="board.memory"
                label="Memory modules"
                rows={modules.map((m, i) => ({ m, i }))}
                columns={MODULE_COLUMNS}
                rowKey={(r) => r.i}
                defaultView={{ groupBy: [], sort: [] }}
                compact
                listClassName="gap-1"
                renderRow={({ m, i }) => (
                  <p className="flex flex-wrap gap-x-2 text-xs">
                    <span className="font-mono">{str(m.DeviceLocator) ?? str(m.BankLabel) ?? `Module ${i + 1}`}</span>
                    <span>{formatBytes(num(m.Capacity), 0)}</span>
                    <span className="text-muted">{[num(m.ConfiguredClockSpeed) ? `${num(m.ConfiguredClockSpeed)} MT/s` : null, str(m.Manufacturer), str(m.PartNumber)].filter(Boolean).join(' · ')}</span>
                  </p>
                )}
              />
            </div>
          )}
          {notes.map((n, i) => (
            <p key={i} className={cn('mt-2 flex items-start gap-1.5 text-xs', n.tone === 'warning' ? 'text-warning' : 'text-muted')}>
              {n.tone === 'warning' && <AlertTriangle className="mt-0.5 size-3.5 shrink-0" aria-hidden />}
              {n.text}
            </p>
          ))}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <CircuitBoard className="size-4 text-accent" aria-hidden /> Expansion slots
          </CardTitle>
        </CardHeader>
        <CardContent>
          {slots.length === 0 ? (
            <p className="text-sm text-muted">The board's firmware does not list its slots.</p>
          ) : (
            <>
              <DataTable
                viewKey="board.slots"
                label="Expansion slots"
                rows={slots.map((s, i) => ({ s, i }))}
                columns={SLOT_COLUMNS}
                rowKey={(r) => r.i}
                defaultView={{ groupBy: [], sort: [] }}
                compact
                listClassName="gap-1"
                renderRow={({ s, i }) => {
                  const usage = num(s.CurrentUsage);
                  return (
                    <p className="flex flex-wrap items-center gap-2 text-sm">
                      <span className="font-medium">{str(s.SlotDesignation) ?? `Slot ${i + 1}`}</span>
                      {usage === 4 ? <Badge tone="accent">in use</Badge> : usage === 3 ? <Badge>free</Badge> : null}
                    </p>
                  );
                }}
              />
              <p className="mt-2 text-xs text-muted">As the board's firmware (SMBIOS) lists them; some boards do not report correctly which slots are in use.</p>
            </>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function Facts({ rows }: { rows: [string, string | null][] }) {
  const shown = rows.filter(([, v]) => v);
  if (shown.length === 0) return <p className="text-sm text-muted">Not recorded.</p>;
  return (
    <dl className="mt-1 grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm">
      {shown.map(([k, v]) => (
        <div key={k} className="contents">
          <dt className="text-muted">{k}</dt>
          <dd className="break-words">{v}</dd>
        </div>
      ))}
    </dl>
  );
}

type ModuleRow = { m: Json; i: number };

const MODULE_COLUMNS: Column<ModuleRow>[] = [
  { id: 'slot', header: 'Slot', value: ({ m, i }) => str(m.DeviceLocator) ?? str(m.BankLabel) ?? `Module ${i + 1}`, groupable: false },
  { id: 'size', header: 'Size', value: ({ m }) => num(m.Capacity), firstDesc: true },
  { id: 'speed', header: 'Speed', value: ({ m }) => num(m.ConfiguredClockSpeed) ?? num(m.Speed), firstDesc: true },
  { id: 'maker', header: 'Maker', value: ({ m }) => str(m.Manufacturer) },
  { id: 'part', header: 'Part number', value: ({ m }) => str(m.PartNumber) },
];

type SlotRow = { s: Json; i: number };

const SLOT_COLUMNS: Column<SlotRow>[] = [
  { id: 'slot', header: 'Slot', value: ({ s, i }) => str(s.SlotDesignation) ?? `Slot ${i + 1}`, groupable: false },
  { id: 'usage', header: 'Use', value: ({ s }) => (num(s.CurrentUsage) === 4 ? 'In use' : num(s.CurrentUsage) === 3 ? 'Free' : 'Not reported') },
  { id: 'type', header: 'Type', value: ({ s }) => (/^M\.2/i.test(str(s.SlotDesignation) ?? '') ? 'M.2' : /PCIE?X?\d/i.test(str(s.SlotDesignation) ?? '') ? 'PCI Express' : 'Other') },
];

const STATUS_RANK: Record<BoardStatus, number> = { critical: 0, warning: 1, ok: 2, info: 3, doubtful: 4 };

function readingColumns(extraHeader?: string, extra?: (r: BoardReading) => string | null): Column<BoardReading>[] {
  return [
    { id: 'label', header: 'Sensor', value: (r) => r.label, groupable: false },
    { id: 'value', header: 'Value', value: (r) => r.value, groupable: false, firstDesc: true },
    { id: 'status', header: 'Status', value: (r) => STATUS_RANK[r.status], group: (r) => STATUS[r.status].label },
    { id: 'chip', header: 'Chip', value: (r) => r.chip },
    ...(extra && extraHeader ? [{ id: 'extra', header: extraHeader, value: extra, groupable: false } satisfies Column<BoardReading>] : []),
  ];
}

function Readings({ title, icon: Icon, rows, format, extra, extraHeader }: { title: string; icon: typeof Fan; rows: BoardReading[]; format: (r: BoardReading) => string; extra?: (r: BoardReading) => string | null; extraHeader?: string }) {
  return (
    <section aria-label={title}>
      <h3 className="mb-1.5 flex items-center gap-1.5 text-sm font-medium">
        <Icon className="size-3.5 text-muted" aria-hidden /> {title}
      </h3>
      {rows.length === 0 ? (
        <p className="text-xs text-muted">None.</p>
      ) : (
        <DataTable
          viewKey={`board.${title.toLowerCase()}`}
          label={title}
          rows={rows}
          columns={readingColumns(extraHeader, extra)}
          rowKey={(r) => r.key}
          defaultView={{ groupBy: [], sort: [] }}
          compact
          listClassName="divide-y divide-border rounded-md border border-border"
          rowTitle={(r) => r.note}
          renderRow={(r) => (
            <div className={cn('flex flex-col gap-0.5 px-2.5 py-1.5', r.status === 'doubtful' && 'opacity-70')}>
              <div className="flex items-center gap-2 text-sm">
                <span className="min-w-0 flex-1 truncate">{r.label}</span>
                {extra?.(r) && <span className="text-xs text-muted">{extra(r)}</span>}
                <span className="tabular-nums">{format(r)}</span>
                <Badge tone={STATUS[r.status].tone} className="px-1.5 py-0 text-[10px]">
                  {STATUS[r.status].label}
                </Badge>
              </div>
              {r.status !== 'ok' && <Note>{r.note}</Note>}
            </div>
          )}
        />
      )}
    </section>
  );
}

function Note({ children }: { children: ReactNode }) {
  return <p className="text-xs text-muted">{children}</p>;
}
