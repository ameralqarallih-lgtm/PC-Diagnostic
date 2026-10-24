import { AlertTriangle, CheckCircle2, ChevronRight, Cpu, HardDrive, Info, MemoryStick, Monitor, Network, Search, Server, Thermometer, Usb } from 'lucide-react';
import { useMemo, useState, type ReactNode } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { TabFade, TabUnderline } from '@/components/ui/tab-indicator';
import type { DeviceInfo, InstalledDriver, SensorInfo, SystemOverview } from '@/lib/api/types';
import { formatBytes, formatDateTime, formatUnit } from '@/lib/format';
import { cn } from '@/lib/utils';
import { DevicesTab } from './DevicesTab';
import { DISABLED, countText, hasFault, summarize } from './devices';
import { MotherboardTab } from './MotherboardTab';
import { classifyReadings, type ReadingRow } from './readings';
import type { SystemTab } from './search';


const KIND_LABEL: Record<string, string> = { gpu: 'Graphics', disk: 'Drives', network: 'Network adapters', display: 'Displays', other: 'Other devices' };
const DAY = 24 * 3600_000;

function when(ms: number | null | undefined): string {
  return ms == null ? 'never' : formatDateTime(new Date(ms).toISOString());
}

function prettyKey(key: string): string {
  return key.replace(/[._]/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^\w/, (c) => c.toUpperCase());
}

function detailText(value: unknown): string {
  if (value == null) return '—';
  if (typeof value === 'number') return Math.abs(value) >= 1_000_000_000 ? formatBytes(value) : String(value);
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (typeof value === 'string') return value;
  if (typeof value === 'object' && value && 'Value' in value) return detailText((value as { Value: unknown }).Value);
  return JSON.stringify(value);
}

/** The newest time the inventory saw anything (an incident's copy is judged against its own time, not now). */
function reference(overview: SystemOverview): number {
  return Math.max(0, ...overview.devices.map((d) => d.lastSeen));
}

/** A device the recorder has not seen for a day before its newest sighting (removed, or re-enumerated). */
export function isRemoved(device: DeviceInfo, newest: number): boolean {
  return newest - device.lastSeen > DAY;
}

function info(overview: SystemOverview, pattern: RegExp): string | null {
  const key = Object.keys(overview.information).find((k) => pattern.test(k));
  return key ? (overview.information[key] ?? null) : null;
}

type Json = Record<string, unknown>;
const list = (value: unknown): Json[] => (Array.isArray(value) ? (value.filter((v) => v && typeof v === 'object') as Json[]) : []);
const str = (value: unknown): string | null => (typeof value === 'string' && value.trim() ? value.trim() : typeof value === 'number' ? String(value) : null);
const num = (value: unknown): number | null => (typeof value === 'number' && Number.isFinite(value) ? value : typeof value === 'string' && value.trim() && Number.isFinite(Number(value)) ? Number(value) : null);

const PLACEHOLDER = /^(system product name|to be filled by o\.?e\.?m\.?|default string|not applicable|none)$/i;

/** The PC's name as people know it: the maker's model, else the motherboard. */
function machineName(overview: SystemOverview): string {
  const maker = info(overview, /^system\.manufacturer$/);
  const model = info(overview, /^system\.model$/);
  if (model && !PLACEHOLDER.test(model)) return [maker, model].filter(Boolean).join(' ');
  const board = [info(overview, /^board\.manufacturer$/)?.replace(/ COMPUTER INC\.?$/i, ''), info(overview, /^board\.product$/)].filter(Boolean).join(' ');
  return board || maker || 'This PC';
}

function osText(overview: SystemOverview): string | null {
  const name = info(overview, /^os\.name$/);
  if (!name) return null;
  const version = info(overview, /^os\.display_version$/);
  const build = info(overview, /^os\.build$/);
  const ubr = info(overview, /^os\.ubr$/);
  return `${name}${version ? ` ${version}` : ''}${build ? ` (build ${build}${ubr ? `.${ubr}` : ''})` : ''}`;
}

/**
 * The display layout as recorded. The recorder stores it as { IsRemoteSession, Displays } (its own names, as each
 * display's fields are); read either spelling so the layout shows whichever wrote it.
 */
function displayOf(overview: SystemOverview): { isRemoteSession?: boolean; displays?: Record<string, unknown>[] } | null {
  const raw = overview.display as (Record<string, unknown> & { isRemoteSession?: boolean; displays?: Record<string, unknown>[] }) | null;
  if (!raw) return null;
  return {
    isRemoteSession: raw.isRemoteSession ?? (raw.IsRemoteSession as boolean | undefined),
    displays: raw.displays ?? (raw.Displays as Record<string, unknown>[] | undefined),
  };
}

/** What deserves a look: unavailable readings that matter, removed devices, unhealthy drives, remote displays. */
interface Attention {
  tone: 'warning' | 'info';
  text: string;
  detail?: string;
  tab?: SystemTab;
}

function attention(overview: SystemOverview, readings: ReadingRow[]): Attention[] {
  const items: Attention[] = [];
  const newest = reference(overview);
  const missing = readings.filter((r) => r.category === 'missing');
  if (missing.length > 0) {
    const examples = missing.slice(0, 3).map((r) => `${r.label}: ${r.why}`);
    items.push({ tone: 'warning', text: `${missing.length} reading${missing.length === 1 ? ' is' : 's are'} unavailable on this PC`, detail: examples.join(' · '), tab: 'readings' });
  }
  for (const d of overview.devices.filter((d) => isRemoved(d, newest))) {
    items.push({
      tone: 'info',
      text: `${d.name ?? 'A device'} was last seen ${when(d.lastSeen)}`,
      detail: 'Usually the same device listed again after a driver update or a move to another slot. Its old readings stay in the history.',
      tab: 'hardware',
    });
  }
  for (const d of overview.devices.filter((d) => d.kind === 'disk')) {
    const health = str(d.details?.health);
    if (health && !/^healthy$/i.test(health)) items.push({ tone: 'warning', text: `${d.name ?? 'A drive'} reports its health as ${health}`, detail: 'Back up its data and check the drive maker’s tool.', tab: 'hardware' });
  }
  for (const { device: d } of (overview.ioDevices ?? []).filter((r) => hasFault(r.device))) {
    items.push({ tone: 'warning', text: `${d.name} is not working (code ${d.problemCode})`, detail: d.problem ?? undefined, tab: 'devices' });
  }
  for (const { device: d } of (overview.ioDevices ?? []).filter((r) => r.device.present && r.device.problemCode === DISABLED)) {
    items.push({ tone: 'info', text: `${d.name} is disabled`, detail: 'Turned off in Device Manager by a person or a program; it stays off until it is enabled again.', tab: 'devices' });
  }
  if (displayOf(overview)?.isRemoteSession) {
    items.push({ tone: 'info', text: 'The display layout was recorded during a Remote Desktop session', detail: 'It shows the remote displays, not the monitors connected to this PC.', tab: 'hardware' });
  }
  return items;
}

export interface SystemOverviewViewProps {
  overview: SystemOverview;
  /** The tab shown (the System page keeps it in the address); without it the view keeps its own. */
  tab?: SystemTab;
  onTab?: (tab: SystemTab) => void;
  /** More tabs after the built-in ones (the System page adds the recorder's overhead). */
  extraTabs?: { id: string; label: string; content: ReactNode }[];
  /** The PC now (the System page): board readings and device changes refresh. An incident shows what was recorded. */
  live?: boolean;
}

/** The PC explored by topic: an overview, its hardware, drivers, sensors and why some readings are unavailable. */
export function SystemOverviewView({ overview, tab, onTab, extraTabs = [], live }: SystemOverviewViewProps) {
  const [ownTab, setOwnTab] = useState<SystemTab>('overview');
  const active = tab ?? ownTab;
  const select = (next: SystemTab) => (onTab ? onTab(next) : setOwnTab(next));
  const readings = useMemo(() => classifyReadings(overview), [overview]);
  const missing = readings.filter((r) => r.category === 'missing').length;
  const devices = useMemo(() => summarize(overview.ioDevices ?? []), [overview.ioDevices]);

  const tabs: { id: SystemTab; label: string; count?: number; warn?: boolean }[] = [
    { id: 'overview', label: 'Overview' },
    { id: 'hardware', label: 'Hardware', count: overview.devices.length },
    { id: 'motherboard', label: 'Motherboard' },
    { id: 'devices', label: 'Devices', count: devices.problems || devices.connected, warn: devices.problems > 0 },
    { id: 'drivers', label: 'Drivers', count: overview.drivers.length },
    { id: 'sensors', label: 'Sensors', count: overview.sensors.length },
    { id: 'readings', label: 'Readings', count: missing, warn: missing > 0 },
    ...extraTabs.map((t) => ({ id: t.id, label: t.label })),
  ];
  const current = tabs.some((t) => t.id === active) ? active : 'overview';

  return (
    <div className="flex flex-col gap-4">
      <div role="tablist" aria-label="System" className="-mx-1 flex gap-1 overflow-x-auto border-b border-border px-1">
        {tabs.map((t) => (
          <button
            key={t.id}
            type="button"
            role="tab"
            id={`system-tab-${t.id}`}
            aria-selected={current === t.id}
            aria-controls={`system-panel-${t.id}`}
            onClick={() => select(t.id)}
            className={cn(
              'relative -mb-px flex shrink-0 items-center gap-1.5 border-b-2 border-transparent px-3 py-2 text-sm transition-colors',
              current === t.id ? 'font-medium text-foreground' : 'text-muted hover:text-foreground',
            )}
          >
            {current === t.id && <TabUnderline group="system-tabs" />}
            {t.label}
            {t.count != null && t.count > 0 && (
              <span className={cn('rounded-full px-1.5 text-[11px] tabular-nums', t.warn ? 'bg-warning/20 text-warning' : 'bg-surface-muted text-muted')}>{t.count}</span>
            )}
          </button>
        ))}
      </div>

      <div role="tabpanel" id={`system-panel-${current}`} aria-labelledby={`system-tab-${current}`}>
        <TabFade tab={current}>
          {current === 'overview' && <OverviewTab overview={overview} readings={readings} onTab={select} />}
          {current === 'hardware' && <HardwareTab overview={overview} />}
          {current === 'motherboard' && <MotherboardTab overview={overview} live={live} />}
          {current === 'devices' && <DevicesTab overview={overview} live={live} />}
          {current === 'drivers' && <DriversTab overview={overview} />}
          {current === 'sensors' && <SensorsTab overview={overview} />}
          {current === 'readings' && <ReadingsTab readings={readings} />}
          {extraTabs.find((t) => t.id === current)?.content}
        </TabFade>
      </div>
    </div>
  );
}

// Overview.

function OverviewTab({ overview, readings, onTab }: { overview: SystemOverview; readings: ReadingRow[]; onTab: (tab: SystemTab) => void }) {
  const hardware = (overview.hardware ?? {}) as Json;
  const newest = reference(overview);
  const cpus = list(hardware.cpus);
  const modules = list(hardware.memory_modules);
  const gpus = overview.devices.filter((d) => d.kind === 'gpu' && !isRemoved(d, newest));
  const disks = overview.devices.filter((d) => d.kind === 'disk' && !isRemoved(d, newest));
  const networks = overview.devices.filter((d) => d.kind === 'network' && !isRemoved(d, newest));
  const displays = displayOf(overview)?.displays ?? [];
  const totalMemory = num(info(overview, /^memory\.total_bytes$/));
  const items = attention(overview, readings);
  const devices = summarize(overview.ioDevices ?? []);
  const cpuName = info(overview, /^cpu(\.\d+)?\.name$/) ?? str(cpus[0]?.Name);
  const cores = info(overview, /^cpu(\.\d+)?\.cores$/) ?? str(cpus[0]?.NumberOfCores);
  const threads = info(overview, /^cpu(\.\d+)?\.logical_processors$/) ?? str(cpus[0]?.NumberOfLogicalProcessors);
  const os = osText(overview);
  const lastBoot = info(overview, /^os\.last_boot$/);
  const installed = info(overview, /^os\.install_date$/);

  if (Object.keys(overview.information).length === 0 && overview.devices.length === 0) {
    return <p className="text-sm text-muted">No inventory recorded yet. It is taken when recording starts.</p>;
  }

  return (
    <div className="flex flex-col gap-4">
      <Card>
        <CardContent className="flex flex-col gap-1 pt-4">
          <p className="text-xs font-medium uppercase tracking-wide text-muted">This PC</p>
          <p className="text-2xl font-semibold">{machineName(overview)}</p>
          <p className="text-sm text-muted">
            {[os, lastBoot && `last started ${lastBoot}`, installed && `Windows installed ${installed}`].filter(Boolean).join(' · ') || 'Windows version not recorded'}
          </p>
        </CardContent>
      </Card>

      <section aria-label="Needs attention">
        {items.length === 0 ? (
          <p className="flex items-center gap-2 rounded-lg border border-success/40 bg-success/10 px-3 py-2 text-sm">
            <CheckCircle2 className="size-4 text-success" aria-hidden /> Nothing needs attention: every tracked reading is available and every device is present.
          </p>
        ) : (
          <DataTable
            viewKey="system.attention"
            label="What needs attention"
            rows={items.map((item, index) => ({ ...item, index }))}
            columns={ATTENTION_COLUMNS}
            rowKey={(item) => item.index}
            defaultView={{ groupBy: [], sort: [] }}
            compact
            listClassName="gap-2"
            renderRow={(item) => (
              <div className={cn('flex items-start gap-2.5 rounded-lg border px-3 py-2', item.tone === 'warning' ? 'border-warning/40 bg-warning/10' : 'border-border bg-surface')}>
                {item.tone === 'warning' ? <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden /> : <Info className="mt-0.5 size-4 shrink-0 text-muted" aria-hidden />}
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium">{item.text}</p>
                  {item.detail && <p className="text-xs text-muted">{item.detail}</p>}
                </div>
                {item.tab && (
                  <button type="button" className="flex shrink-0 items-center text-xs text-accent hover:underline" onClick={() => onTab(item.tab!)}>
                    Details <ChevronRight className="size-3.5" aria-hidden />
                  </button>
                )}
              </div>
            )}
          />
        )}
      </section>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 2xl:grid-cols-3">
        <Spec icon={Cpu} title="Processor" onMore={() => onTab('hardware')}>
          <Fact strong>{cpuName ?? 'Not recorded'}</Fact>
          {(cores || threads) && <Fact>{[cores && `${cores} cores`, threads && `${threads} threads`].filter(Boolean).join(', ')}</Fact>}
          {cpus[0] && (
            <Fact muted>
              {[num(cpus[0].MaxClockSpeed) && `base ${(num(cpus[0].MaxClockSpeed)! / 1000).toFixed(1)} GHz`, num(cpus[0].L3CacheSize) && `L3 ${Math.round(num(cpus[0].L3CacheSize)! / 1024)} MB`, str(cpus[0].SocketDesignation)]
                .filter(Boolean)
                .join(' · ')}
            </Fact>
          )}
        </Spec>

        <Spec icon={MemoryStick} title="Memory" onMore={() => onTab('hardware')}>
          <Fact strong>{totalMemory ? formatBytes(totalMemory) : 'Not recorded'}</Fact>
          {modules.length > 0 && (
            <Fact>
              {modules.length} module{modules.length === 1 ? '' : 's'}
              {num(modules[0]!.ConfiguredClockSpeed) ? ` at ${num(modules[0]!.ConfiguredClockSpeed)} MT/s` : ''}
            </Fact>
          )}
          {modules.length > 0 && <Fact muted>{[...new Set(modules.map((m) => [str(m.Manufacturer), formatBytes(num(m.Capacity))].filter(Boolean).join(' ')))].join(' + ')}</Fact>}
        </Spec>

        <Spec icon={Monitor} title="Graphics" onMore={() => onTab('hardware')}>
          {gpus.length === 0 && <Fact muted>None recorded</Fact>}
          {gpus.map((g) => (
            <Fact key={g.id} strong={g === gpus[0]}>
              {g.name ?? 'Graphics adapter'}
              {num(g.details?.dedicatedMemoryBytes) && num(g.details?.dedicatedMemoryBytes)! > 512 * 1024 * 1024 ? <span className="text-muted"> · {formatBytes(num(g.details?.dedicatedMemoryBytes))}</span> : <span className="text-muted"> · integrated</span>}
            </Fact>
          ))}
        </Spec>

        <Spec icon={HardDrive} title="Storage" onMore={() => onTab('hardware')}>
          {disks.length === 0 && <Fact muted>None recorded</Fact>}
          {disks.map((d) => (
            <Fact key={d.id} strong={d === disks[0]}>
              {d.name ?? 'Drive'}
              <span className="text-muted">
                {' '}
                · {formatBytes(num(d.details?.sizeBytes), 0)}
                {Array.isArray(d.details?.volumes) && (d.details!.volumes as string[]).length > 0 ? ` · ${(d.details!.volumes as string[]).join(' ')}` : ''}
              </span>
              {str(d.details?.health) && !/^healthy$/i.test(str(d.details?.health)!) && <Badge tone="warning">{str(d.details?.health)}</Badge>}
            </Fact>
          ))}
        </Spec>

        <Spec icon={Network} title="Network" onMore={() => onTab('hardware')}>
          {networks.length === 0 && <Fact muted>None recorded</Fact>}
          {[...networks]
            .sort((a, b) => Number(a.vendor === 'Virtual') - Number(b.vendor === 'Virtual'))
            .map((n) => (
              <Fact key={n.id} strong={n.vendor !== 'Virtual'} muted={n.vendor === 'Virtual'}>
                {n.name}
                <span className="text-muted"> · {str(n.details?.description) ?? n.vendor}</span>
              </Fact>
            ))}
        </Spec>

        <Spec icon={Server} title="Motherboard and BIOS" onMore={() => onTab('motherboard')}>
          <Fact strong>{[info(overview, /^board\.manufacturer$/), info(overview, /^board\.product$/)].filter(Boolean).join(' ') || 'Not recorded'}</Fact>
          {info(overview, /^board\.chipset$/) && <Fact>{info(overview, /^board\.chipset$/)} chipset</Fact>}
          {info(overview, /^bios\.version$/) && (
            <Fact>
              BIOS {info(overview, /^bios\.version$/)}
              {info(overview, /^bios\.date$/) ? ` (${info(overview, /^bios\.date$/)})` : ''}
            </Fact>
          )}
          {info(overview, /^bios\.vendor$/) && <Fact muted>{info(overview, /^bios\.vendor$/)}</Fact>}
        </Spec>

        <Spec icon={Monitor} title="Displays" onMore={() => onTab('hardware')}>
          {displays.length === 0 && <Fact muted>No display layout recorded</Fact>}
          {displays.map((d, i) => (
            <Fact key={i} strong={d.IsPrimary === true}>
              {displayText(d)}
            </Fact>
          ))}
        </Spec>

        <Spec icon={Usb} title="Devices" onMore={() => onTab('devices')}>
          {(overview.ioDevices ?? []).length === 0 ? (
            <Fact muted>No device register recorded</Fact>
          ) : (
            <>
              <Fact strong>{devices.connected} connected</Fact>
              <Fact>{connectedKinds(overview)}</Fact>
              <Fact muted>
                {devices.remembered} remembered from earlier connections
                {devices.problems > 0 ? ` · ${devices.problems} with a problem` : ''}
              </Fact>
            </>
          )}
        </Spec>

        <Spec icon={Thermometer} title="Monitoring" onMore={() => onTab('sensors')}>
          <Fact strong>{overview.sensors.length} hardware sensors</Fact>
          <Fact>{readings.filter((r) => r.category === 'missing').length} readings unavailable</Fact>
          <Fact muted>{overview.drivers.length} drivers in the inventory</Fact>
        </Spec>
      </div>
    </div>
  );
}

/** "2 keyboards, 1 mouse, 3 audio" for the connected devices. */
function connectedKinds(overview: SystemOverview): string {
  const counts = new Map<string, number>();
  for (const { device } of overview.ioDevices ?? []) {
    if (device.present && !device.technical && device.kind !== 'system') counts.set(device.kind, (counts.get(device.kind) ?? 0) + 1);
  }
  return [...counts.entries()]
    .sort((a, b) => b[1] - a[1])
    .slice(0, 5)
    .map(([kind, n]) => countText(kind, n))
    .join(', ');
}

const ATTENTION_COLUMNS: Column<Attention & { index: number }>[] = [
  { id: 'tone', header: 'Kind', value: (a) => (a.tone === 'warning' ? 0 : 1), group: (a) => (a.tone === 'warning' ? 'Warnings' : 'For information') },
  { id: 'topic', header: 'Topic', value: (a) => (a.tab ? prettyKey(a.tab) : 'General') },
  { id: 'text', header: 'What', value: (a) => a.text, groupable: false },
];

type DisplayRow = { d: Record<string, unknown>; index: number };

const CONNECTIONS = ['Other', 'HDMI', 'DisplayPort', 'Built-in', 'DVI', 'VGA', 'USB-C', 'Wireless', 'Indirect'];

/** The display's connection (stored as DisplayConnection's number, or its name). */
function connectionName(value: unknown): string | null {
  return typeof value === 'number' ? (CONNECTIONS[value] ?? null) : str(value);
}

const DISPLAY_COLUMNS: Column<DisplayRow>[] = [
  { id: 'name', header: 'Display', value: ({ d }) => str(d.FriendlyName), groupable: false },
  { id: 'connection', header: 'Connection', value: ({ d }) => connectionName(d.Connection) },
  { id: 'adapter', header: 'Adapter', value: ({ d }) => str(d.AdapterName) },
  { id: 'pixels', header: 'Resolution', value: ({ d }) => (num(d.WidthPixels) ?? 0) * (num(d.HeightPixels) ?? 0) || null, groupable: false, firstDesc: true },
  { id: 'refresh', header: 'Refresh rate', value: ({ d }) => num(d.RefreshRateHz), firstDesc: true },
  { id: 'primary', header: 'Main display', value: ({ d }) => (d.IsPrimary === true ? 'Main display' : 'Other displays') },
];

function displayText(d: Record<string, unknown>): string {
  const name = str(d.FriendlyName) ?? 'Display';
  const w = num(d.WidthPixels);
  const h = num(d.HeightPixels);
  const hz = num(d.RefreshRateHz);
  const parts = [w && h ? `${w} × ${h}` : null, hz ? `${Math.round(hz)} Hz` : null, str(d.ColorMode), d.IsPrimary === true ? 'main' : null].filter(Boolean);
  return `${name}${parts.length ? `: ${parts.join(', ')}` : ''}`;
}

function Spec({ icon: Icon, title, onMore, children }: { icon: typeof Cpu; title: string; onMore?: () => void; children: ReactNode }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Icon className="size-4 text-accent" aria-hidden /> {title}
        </CardTitle>
        {onMore && (
          <button type="button" onClick={onMore} className="text-xs text-accent hover:underline" aria-label={`More about ${title.toLowerCase()}`}>
            More
          </button>
        )}
      </CardHeader>
      <CardContent className="flex flex-col gap-1">{children}</CardContent>
    </Card>
  );
}

function Fact({ children, strong, muted }: { children: ReactNode; strong?: boolean; muted?: boolean }) {
  return <p className={cn('flex flex-wrap items-center gap-x-1.5 text-sm', strong && 'font-medium', muted && 'text-xs text-muted')}>{children}</p>;
}

// Hardware.

interface Property {
  group: string;
  key: string;
  value: string | null;
}

const PROPERTY_COLUMNS: Column<Property>[] = [
  { id: 'group', header: 'Area', value: (p) => p.group, group: (p) => prettyKey(p.group), hidden: true },
  { id: 'key', header: 'Property', value: (p) => prettyKey(p.key), groupable: false, className: 'text-muted' },
  { id: 'value', header: 'Value', value: (p) => p.value, cell: (p) => p.value ?? '—', groupable: false, className: 'break-words' },
];

function deviceColumns(newest: number): Column<DeviceInfo>[] {
  return [
    { id: 'kind', header: 'Kind', value: (d) => KIND_LABEL[d.kind] ?? d.kind },
    { id: 'name', header: 'Name', value: (d) => d.name ?? 'Unnamed device', groupable: false },
    { id: 'vendor', header: 'Vendor', value: (d) => d.vendor },
    { id: 'present', header: 'Present', value: (d) => (isRemoved(d, newest) ? 'Not seen for a day' : 'Present') },
    { id: 'lastSeen', header: 'Last seen', value: (d) => d.lastSeen, groupable: false, firstDesc: true },
  ];
}

interface MemoryModule {
  slot: string;
  size: number | null;
  speed: number | null;
  maker: string | null;
  part: string | null;
}

const MODULE_COLUMNS: Column<MemoryModule>[] = [
  { id: 'slot', header: 'Slot', value: (m) => m.slot, groupable: false, className: 'font-mono text-xs' },
  { id: 'size', header: 'Size', value: (m) => m.size, cell: (m) => formatBytes(m.size, 0), align: 'right', firstDesc: true },
  { id: 'speed', header: 'Speed', value: (m) => m.speed, cell: (m) => (m.speed ? `${m.speed} MT/s` : '—'), align: 'right' },
  { id: 'maker', header: 'Maker', value: (m) => m.maker },
  { id: 'part', header: 'Part number', value: (m) => m.part, groupable: false, className: 'font-mono text-xs' },
];

function groupInformation(info: Record<string, string | null>): Property[] {
  return Object.entries(info)
    .filter(([key]) => !key.startsWith('inventory.') && !key.startsWith('rollup.'))
    .map(([key, value]) => {
      const dot = key.indexOf('.');
      return { group: dot > 0 ? key.slice(0, dot) : 'general', key: dot > 0 ? key.slice(dot + 1) : key, value };
    });
}

function HardwareTab({ overview }: { overview: SystemOverview }) {
  const newest = reference(overview);
  const properties = useMemo(() => groupInformation(overview.information), [overview.information]);
  const modules = useMemo<MemoryModule[]>(
    () =>
      list((overview.hardware as Json | null)?.memory_modules).map((m, i) => ({
        slot: str(m.DeviceLocator) ?? str(m.BankLabel) ?? `Module ${i + 1}`,
        size: num(m.Capacity),
        speed: num(m.ConfiguredClockSpeed) ?? num(m.Speed),
        maker: str(m.Manufacturer),
        part: str(m.PartNumber),
      })),
    [overview.hardware],
  );
  const columns = useMemo(() => deviceColumns(newest), [newest]);

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
      <Card className="xl:row-span-2">
        <CardHeader>
          <CardTitle>Devices</CardTitle>
        </CardHeader>
        <CardContent>
          <DataTable
            viewKey="system.devices"
            label="Devices"
            rows={overview.devices}
            columns={columns}
            rowKey={(d) => d.id}
            defaultView={{ groupBy: ['kind'], sort: [{ id: 'name', desc: false }] }}
            emptyText="No devices recorded yet."
            compact
            renderRow={(d) => (
              <div className={cn('mb-2 rounded-md border border-border p-2.5', isRemoved(d, newest) && 'border-dashed opacity-75')}>
                <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
                  {d.name ?? 'Unnamed device'}
                  {isRemoved(d, newest) && <Badge tone="neutral">not seen since {when(d.lastSeen)}</Badge>}
                </p>
                <p className="text-xs text-muted">
                  {d.vendor ?? ''} · first seen {when(d.firstSeen)}
                </p>
                {d.details && (
                  <dl className="mt-1.5 grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 text-xs">
                    {Object.entries(d.details)
                      .filter(([, v]) => v != null && v !== '' && (typeof v !== 'object' || Array.isArray(v)))
                      .slice(0, 10)
                      .map(([k, v]) => (
                        <div key={k} className="contents">
                          <dt className="text-muted">{prettyKey(k)}</dt>
                          <dd className="truncate">{Array.isArray(v) ? v.join(', ') : detailText(v)}</dd>
                        </div>
                      ))}
                  </dl>
                )}
              </div>
            )}
          />
        </CardContent>
      </Card>

      {modules.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Memory modules</CardTitle>
          </CardHeader>
          <CardContent>
            <DataTable viewKey="system.memory" label="Memory modules" rows={modules} columns={MODULE_COLUMNS} rowKey={(m) => m.slot} defaultView={{ groupBy: [], sort: [{ id: 'slot', desc: false }] }} compact />
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader>
          <CardTitle>Displays</CardTitle>
          <span className="text-xs text-muted">{when(overview.displayTs)}</span>
        </CardHeader>
        <CardContent>
          {!displayOf(overview)?.displays?.length ? (
            <p className="text-sm text-muted">No display configuration recorded.</p>
          ) : (
            <>
              {displayOf(overview)!.isRemoteSession && <p className="mb-2 text-xs text-warning">Recorded during a Remote Desktop session (remote displays, not the physical monitors).</p>}
              <DataTable
                viewKey="system.displays"
                label="Displays"
                rows={displayOf(overview)!.displays!.map((d, index) => ({ d, index }))}
                columns={DISPLAY_COLUMNS}
                rowKey={(r) => r.index}
                defaultView={{ groupBy: [], sort: [] }}
                compact
                listClassName="gap-2"
                renderRow={({ d }) => (
                <div className="rounded-md border border-border p-2.5">
                  <p className="text-sm font-medium">{displayText(d)}</p>
                  <dl className="mt-1 grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 text-xs">
                    {Object.entries(d)
                      .filter(([k, v]) => v != null && v !== '' && !['FriendlyName', 'WidthPixels', 'HeightPixels', 'RefreshRateHz'].includes(k) && (typeof v !== 'object' || 'Value' in (v as object)))
                      .slice(0, 12)
                      .map(([k, v]) => (
                        <div key={k} className="contents">
                          <dt className="text-muted">{prettyKey(k)}</dt>
                          <dd className="truncate" title={detailText(v)}>
                            {detailText(v)}
                          </dd>
                        </div>
                      ))}
                  </dl>
                </div>
                )}
              />
            </>
          )}
        </CardContent>
      </Card>

      <Card className="xl:col-span-2">
        <CardHeader>
          <CardTitle>All inventory details</CardTitle>
        </CardHeader>
        <CardContent>
          <DataTable
            viewKey="system.information"
            label="All inventory details"
            rows={properties}
            columns={PROPERTY_COLUMNS}
            rowKey={(p) => `${p.group}.${p.key}`}
            defaultView={{ groupBy: ['group'], sort: [] }}
            emptyText="No inventory recorded yet."
            compact
          />
        </CardContent>
      </Card>
    </div>
  );
}

// Drivers.

const DRIVER_COLUMNS: Column<InstalledDriver>[] = [
  { id: 'device', header: 'Device', value: (d) => d.deviceName, cell: (d) => d.deviceName ?? '—', groupable: false },
  { id: 'class', header: 'Class', value: (d) => d.deviceClass, cell: (d) => d.deviceClass ?? '—', className: 'text-muted' },
  { id: 'provider', header: 'Provider', value: (d) => d.provider, cell: (d) => d.provider ?? '—' },
  { id: 'version', header: 'Version', value: (d) => d.version, cell: (d) => d.version ?? '—', groupable: false, className: 'font-mono text-xs' },
  { id: 'date', header: 'Date', value: (d) => d.date, cell: (d) => d.date ?? '—', className: 'text-muted', firstDesc: true },
  { id: 'signer', header: 'Signer', value: (d) => d.signer, hidden: true },
];

function SearchBox({ value, onChange, placeholder, label }: { value: string; onChange: (v: string) => void; placeholder: string; label: string }) {
  return (
    <label className="relative block w-full max-w-sm">
      <span className="sr-only">{label}</span>
      <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
      <input type="search" value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} className="h-8 w-full rounded-md border border-border bg-background pl-7 pr-2 text-sm placeholder:text-muted" />
    </label>
  );
}

function DriversTab({ overview }: { overview: SystemOverview }) {
  const [filter, setFilter] = useState('');
  const drivers = useMemo(() => {
    const text = filter.trim().toLowerCase();
    return text ? overview.drivers.filter((d) => [d.deviceName, d.deviceClass, d.provider, d.version].some((v) => v?.toLowerCase().includes(text))) : overview.drivers;
  }, [overview.drivers, filter]);
  return (
    <Card>
      <CardHeader className="flex-wrap">
        <CardTitle>Drivers</CardTitle>
        <span className="text-xs text-muted">Inventory of {when(overview.driversTs)}</span>
      </CardHeader>
      <CardContent>
        {overview.drivers.length === 0 ? (
          <p className="text-sm text-muted">No driver inventory recorded yet.</p>
        ) : (
          <DataTable
            viewKey="system.drivers"
            label="Drivers"
            rows={drivers}
            columns={DRIVER_COLUMNS}
            rowKey={(d) => `${d.deviceName ?? ''}|${d.hardwareId ?? ''}|${d.inf ?? ''}|${d.version ?? ''}`}
            defaultView={{ groupBy: ['class'], sort: [{ id: 'device', desc: false }] }}
            emptyText="Nothing matches."
            toolbarStart={<SearchBox value={filter} onChange={setFilter} placeholder="Filter, e.g. Display, NVIDIA" label="Filter drivers" />}
            fill
          />
        )}
      </CardContent>
    </Card>
  );
}

// Sensors.

/** The hardware a sensor belongs to ("NVIDIA GeForce RTX 4090 / GPU Hot Spot" → the part before the slash). */
function hardwareOf(s: SensorInfo): string {
  const slash = s.name.indexOf(' / ');
  return slash > 0 ? s.name.slice(0, slash) : s.provider;
}

const SENSOR_COLUMNS: Column<SensorInfo>[] = [
  { id: 'hardware', header: 'Hardware', value: hardwareOf, hidden: true },
  {
    id: 'name',
    header: 'Sensor',
    value: (s) => s.name,
    groupable: false,
    cell: (s) => (
      <span className="truncate" title={`${s.provider} · ${s.key}`}>
        {s.name.includes(' / ') ? s.name.slice(s.name.indexOf(' / ') + 3) : s.name}
      </span>
    ),
  },
  { id: 'kind', header: 'Kind', value: (s) => s.kind, className: 'text-xs text-muted' },
  { id: 'value', header: 'Value', value: (s) => s.value, cell: (s) => (s.value == null ? '—' : formatUnit(s.value, s.unit === 'C' ? '°C' : s.unit)), groupable: false, align: 'right', firstDesc: true },
  { id: 'provider', header: 'Provider', value: (s) => s.provider, hidden: true },
];

function SensorsTab({ overview }: { overview: SystemOverview }) {
  const [filter, setFilter] = useState('');
  const [kind, setKind] = useState<string | null>(null);
  const kinds = useMemo(() => [...new Set(overview.sensors.map((s) => s.kind))].sort(), [overview.sensors]);
  const sensors = useMemo(() => {
    const text = filter.trim().toLowerCase();
    return overview.sensors.filter((s) => (!kind || s.kind === kind) && (!text || `${s.name} ${s.kind} ${s.provider}`.toLowerCase().includes(text)));
  }, [overview.sensors, filter, kind]);
  return (
    <Card>
      <CardHeader className="flex-wrap">
        <CardTitle>Hardware sensors</CardTitle>
        <span className="text-xs text-muted">The newest reading of each; chart any of them in History or on a dashboard widget.</span>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {overview.sensors.length === 0 ? (
          <p className="text-sm text-muted">No hardware sensors recorded yet.</p>
        ) : (
          <>
            <div role="group" aria-label="Sensor kind" className="flex flex-wrap gap-1.5">
              {[null, ...kinds].map((k) => (
                <button
                  key={k ?? 'all'}
                  type="button"
                  aria-pressed={kind === k}
                  onClick={() => setKind(k)}
                  className={cn('rounded-full border px-2.5 py-0.5 text-xs', kind === k ? 'border-accent bg-accent/10 text-foreground' : 'border-border text-muted hover:text-foreground')}
                >
                  {k ?? 'All'} <span className="tabular-nums text-muted">{k ? overview.sensors.filter((s) => s.kind === k).length : overview.sensors.length}</span>
                </button>
              ))}
            </div>
            <DataTable
              viewKey="system.sensors"
              label="Hardware sensors"
              rows={sensors}
              columns={SENSOR_COLUMNS}
              rowKey={(s) => s.key}
              defaultView={{ groupBy: ['hardware'], sort: [{ id: 'name', desc: false }] }}
              emptyText="Nothing matches."
              toolbarStart={<SearchBox value={filter} onChange={setFilter} placeholder="Filter, e.g. fan, hot spot, voltage" label="Filter sensors" />}
              fill
            />
          </>
        )}
      </CardContent>
    </Card>
  );
}

// Readings that are unavailable.

const CATEGORY_TEXT: Record<ReadingRow['category'], string> = {
  missing: 'Unavailable',
  notApplicable: 'Does not apply',
  notOnThisGpu: 'Not available on this GPU',
  removed: 'Removed device',
};

const READING_COLUMNS: Column<ReadingRow>[] = [
  { id: 'label', header: 'Reading', value: (r) => r.label, groupable: false, cell: (r) => <span title={r.series}>{r.label}</span> },
  { id: 'device', header: 'Device', value: (r) => r.device, hidden: true },
  { id: 'area', header: 'Area', value: (r) => r.area, hidden: true },
  { id: 'category', header: 'Status', value: (r) => CATEGORY_TEXT[r.category], cell: (r) => <Badge tone={r.category === 'missing' ? 'warning' : 'neutral'}>{CATEGORY_TEXT[r.category]}</Badge> },
  { id: 'why', header: 'Why', value: (r) => r.why, className: 'text-xs text-muted' },
  { id: 'fix', header: 'What you can do', value: (r) => r.fix, groupable: false, className: 'text-xs' },
];

function ReadingsTab({ readings }: { readings: ReadingRow[] }) {
  const [all, setAll] = useState(false);
  const shown = all ? readings : readings.filter((r) => r.category === 'missing');
  const hidden = readings.length - readings.filter((r) => r.category === 'missing').length;
  return (
    <Card>
      <CardHeader className="flex-wrap">
        <CardTitle>Readings that are unavailable</CardTitle>
        {hidden > 0 && (
          <label className="flex items-center gap-2 text-xs text-muted">
            <input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} className="size-3.5 accent-[var(--accent)]" />
            Also show the {hidden} that do not apply (wired adapters, integrated graphics, removed devices)
          </label>
        )}
      </CardHeader>
      <CardContent>
        {readings.length === 0 ? (
          <p className="text-sm text-muted">Every tracked reading is available.</p>
        ) : shown.length === 0 ? (
          <p className="text-sm text-muted">Every reading that applies to this PC is available.</p>
        ) : (
          <DataTable
            viewKey="system.unavailable"
            label="Readings that are unavailable"
            rows={shown}
            columns={READING_COLUMNS}
            rowKey={(r) => r.series}
            defaultView={{ groupBy: ['device'], sort: [{ id: 'label', desc: false }] }}
            fill
          />
        )}
      </CardContent>
    </Card>
  );
}
