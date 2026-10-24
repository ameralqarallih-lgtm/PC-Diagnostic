import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { CheckCircle2, Cpu, HardDrive, MemoryStick, Monitor, Network, Plug, RefreshCw, ScanSearch, ShieldAlert, Unplug } from 'lucide-react';
import { useMemo, useState, type ReactNode } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Dialog } from '@/components/ui/dialog';
import { useToast } from '@/components/ui/toast';
import { apiGet, apiPost, apiRequest } from '@/lib/api/client';
import type { HardwareItem, HardwareKind, HardwareResponse, MissingHardware } from '@/lib/api/types';
import { formatAgo } from '@/lib/format';
import { cn } from '@/lib/utils';

export const HARDWARE_KINDS: Record<HardwareKind, { name: string; plural: string; icon: typeof Monitor }> = {
  device: { name: 'Device', plural: 'Devices', icon: Plug },
  monitor: { name: 'Monitor', plural: 'Monitors', icon: Monitor },
  volume: { name: 'Drive', plural: 'Drives', icon: HardDrive },
  network: { name: 'Network adapter', plural: 'Network adapters', icon: Network },
  gpu: { name: 'Graphics card', plural: 'Graphics cards', icon: Cpu },
  memory: { name: 'Memory', plural: 'Memory', icon: MemoryStick },
  cpu: { name: 'Processor', plural: 'Processor', icon: Cpu },
};

const KIND_ORDER: HardwareKind[] = ['device', 'monitor', 'volume', 'network', 'gpu', 'memory', 'cpu'];

/** The hardware check and the remembered setup (refreshed every 15 s). */
export function useHardware(enabled = true) {
  return useQuery({ queryKey: ['hardware'], queryFn: ({ signal }) => apiGet<HardwareResponse>('/hardware', signal), refetchInterval: 15_000, enabled });
}

function useHardwareActions() {
  const client = useQueryClient();
  const toast = useToast();
  const done = (response: HardwareResponse | undefined) => {
    if (response) client.setQueryData(['hardware'], response);
    void client.invalidateQueries({ queryKey: ['status'] });
  };
  const fail = (title: string) => (error: Error) => toast.show({ title, description: error.message, tone: 'error' });
  return {
    remember: useMutation({
      mutationFn: (body: { items: HardwareItem[]; incidentOnMissing: boolean }) => apiRequest<HardwareResponse>('PUT', '/hardware/expected', body),
      onSuccess: (r) => {
        done(r);
        toast.show({ title: 'Setup remembered', description: 'You will be told when any of it goes missing.', tone: 'success' });
      },
      onError: fail('Not remembered'),
    }),
    forget: useMutation({
      mutationFn: () => apiRequest('DELETE', '/hardware/expected'),
      onSuccess: () => void client.invalidateQueries({ queryKey: ['hardware'] }),
      onError: fail('Not forgotten'),
    }),
    check: useMutation({ mutationFn: () => apiPost<HardwareResponse>('/hardware/check'), onSuccess: done, onError: fail('Not checked') }),
  };
}

const MISSING_COLUMNS: Column<MissingHardware>[] = [
  { id: 'name', header: 'What', value: (m) => m.item.name, groupable: false },
  { id: 'kind', header: 'Kind', value: (m) => HARDWARE_KINDS[m.item.kind]?.name ?? m.item.kind },
  { id: 'since', header: 'Missing since', value: (m) => Date.parse(m.since), groupable: false, firstDesc: true },
];

/** Shortfalls read as amounts: "16 of 32 GB". */
const shortfall = (m: MissingHardware) =>
  m.item.amount != null ? `${m.now != null ? `${Math.round(m.now * 10) / 10} of ` : ''}${Math.round(m.item.amount * 10) / 10} ${m.item.kind === 'memory' ? 'GB' : 'threads'}` : null;

/**
 * Expected hardware: remember the setup once (what is connected now), and the recorder tells when any of it goes
 * missing — a drive, a monitor, a device, a network adapter, a graphics card, memory or processor threads.
 */
export function ExpectedHardwareCard() {
  const { data, isLoading } = useHardware();
  const actions = useHardwareActions();
  const [choosing, setChoosing] = useState(false);
  const status = data?.status ?? null;
  const expected = data?.expected ?? null;
  const missing = status?.missing ?? [];
  const added = status?.added ?? [];

  return (
    <Card>
      <CardHeader className="flex-wrap">
        <CardTitle className="flex items-center gap-2">
          <ScanSearch className="size-4 text-accent" aria-hidden /> Expected hardware
        </CardTitle>
        <div className="flex flex-wrap items-center gap-2">
          <Button size="sm" variant="outline" disabled={!status || actions.check.isPending} onClick={() => actions.check.mutate()}>
            <RefreshCw className={cn(actions.check.isPending && 'animate-spin')} aria-hidden /> Check now
          </Button>
          <Button size="sm" disabled={!status} onClick={() => setChoosing(true)}>
            {expected ? 'Remember again' : 'Remember this setup'}
          </Button>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {data?.recorderProblem && <p className="text-sm text-muted">{data.recorderProblem} The recorder checks the hardware.</p>}
        {isLoading && <p className="text-sm text-muted">Checking…</p>}
        {!expected && status && (
          <p className="text-sm text-muted">
            Remember what this PC has now ({status.current.length} items: devices, monitors, drives, network adapters, graphics, memory and the processor), and the recorder tells you as soon as
            any of it is missing — a disk or monitor disconnected, a headset unplugged, a memory module or cores gone.
          </p>
        )}
        {expected && (
          <>
            {missing.length === 0 ? (
              <p className="flex items-center gap-2 text-sm">
                <CheckCircle2 className="size-4 text-success" aria-hidden />
                Everything expected is here: {expected.items.length} items, remembered {formatAgo(expected.saved)}
                {status && <span className="text-muted">· checked {formatAgo(status.checkedAt)}</span>}.
              </p>
            ) : (
              <div role="alert" className="rounded-md border border-warning/50 bg-warning/10 p-3">
                <p className="mb-2 flex items-center gap-2 text-sm font-medium">
                  <ShieldAlert className="size-4 text-warning" aria-hidden /> {missing.length} expected {missing.length === 1 ? 'item is' : 'items are'} missing
                </p>
                <DataTable
                  viewKey="system.missing"
                  label="Missing hardware"
                  rows={missing}
                  columns={MISSING_COLUMNS}
                  rowKey={(m) => m.item.key}
                  defaultView={{ groupBy: [], sort: [{ id: 'since', desc: true }] }}
                  compact
                  listClassName="divide-y divide-warning/30"
                  renderRow={(m) => {
                    const Icon = HARDWARE_KINDS[m.item.kind]?.icon ?? Unplug;
                    return (
                      <div className="flex items-center gap-2 py-1.5 text-sm">
                        <Icon className="size-4 shrink-0 text-warning" aria-hidden />
                        <span className="min-w-0 flex-1 truncate">
                          {m.item.name} <span className="text-xs text-muted">· {HARDWARE_KINDS[m.item.kind]?.name}{m.item.detail ? ` · ${m.item.detail}` : ''}</span>
                        </span>
                        <span className="shrink-0 text-xs text-muted">{shortfall(m) ?? `missing ${formatAgo(m.since)}`}</span>
                      </div>
                    );
                  }}
                />
              </div>
            )}
            {added.length > 0 && (
              <p className="text-xs text-muted">
                New since the setup was remembered: {added.slice(0, 6).map((a) => a.name).join(', ')}
                {added.length > 6 ? ` and ${added.length - 6} more` : ''}. Remember again to expect them too.
              </p>
            )}
            <div className="flex flex-wrap items-center gap-3 text-xs text-muted">
              <span>{expected.incidentOnMissing ? 'An incident starts when something goes missing.' : 'Missing hardware is shown here and on every page.'}</span>
              <button type="button" className="text-danger hover:underline" onClick={() => actions.forget.mutate()}>
                Forget the setup
              </button>
            </div>
          </>
        )}
      </CardContent>
      {choosing && status && (
        <RememberDialog
          current={status.current}
          expected={expected?.items ?? null}
          incident={expected?.incidentOnMissing ?? false}
          busy={actions.remember.isPending}
          onClose={() => setChoosing(false)}
          onSave={(items, incidentOnMissing) => actions.remember.mutate({ items, incidentOnMissing }, { onSuccess: () => setChoosing(false) })}
        />
      )}
    </Card>
  );
}

/** What to expect: everything there now, grouped by kind, each one can be left out. */
function RememberDialog({
  current,
  expected,
  incident,
  busy,
  onClose,
  onSave,
}: {
  current: HardwareItem[];
  expected: HardwareItem[] | null;
  incident: boolean;
  busy: boolean;
  onClose: () => void;
  onSave: (items: HardwareItem[], incidentOnMissing: boolean) => void;
}) {
  const [chosen, setChosen] = useState<Set<string>>(() => new Set(current.map((i) => i.key)));
  const [incidentOnMissing, setIncidentOnMissing] = useState(incident);
  const groups = useMemo(() => KIND_ORDER.map((kind) => ({ kind, items: current.filter((i) => i.kind === kind) })).filter((g) => g.items.length > 0), [current]);
  const toggle = (key: string, on: boolean) =>
    setChosen((s) => {
      const next = new Set(s);
      if (on) next.add(key);
      else next.delete(key);
      return next;
    });
  const footer: ReactNode = (
    <>
      <Button variant="outline" onClick={onClose}>
        Cancel
      </Button>
      <Button disabled={busy || chosen.size === 0} onClick={() => onSave(current.filter((i) => chosen.has(i.key)), incidentOnMissing)}>
        {busy ? 'Remembering…' : `Expect ${chosen.size} items`}
      </Button>
    </>
  );
  return (
    <Dialog open title="Remember this setup" onClose={onClose} footer={footer}>
      <div className="flex max-h-[60vh] flex-col gap-3 overflow-y-auto text-sm">
        <p className="text-muted">Everything connected now is chosen. Leave out what comes and goes (a phone, a USB stick).</p>
        {groups.map(({ kind, items }) => {
          const Icon = HARDWARE_KINDS[kind].icon;
          return (
            <fieldset key={kind} className="rounded-md border border-border p-2">
              <legend className="flex items-center gap-1.5 px-1 text-xs font-medium text-muted">
                <Icon className="size-3.5" aria-hidden /> {HARDWARE_KINDS[kind].plural} ({items.length})
              </legend>
              <ul className="flex flex-col">
                {items.map((item) => (
                  <li key={item.key}>
                    <label className="flex cursor-pointer items-center gap-2 rounded px-1 py-1 hover:bg-surface-muted">
                      <input type="checkbox" checked={chosen.has(item.key)} onChange={(e) => toggle(item.key, e.target.checked)} className="size-4 accent-[var(--accent)]" />
                      <span className="min-w-0 flex-1 truncate">{item.name}</span>
                      {item.detail && <span className="shrink-0 text-xs text-muted">{item.detail}</span>}
                      {expected && !expected.some((e) => e.key === item.key) && <Badge tone="accent">New</Badge>}
                    </label>
                  </li>
                ))}
              </ul>
            </fieldset>
          );
        })}
        <label className="flex items-start gap-2 rounded-md border border-border p-2">
          <input type="checkbox" checked={incidentOnMissing} onChange={(e) => setIncidentOnMissing(e.target.checked)} className="mt-0.5 size-4 accent-[var(--accent)]" />
          <span>
            Start an incident when expected hardware goes missing
            <span className="block text-xs text-muted">It keeps what the PC was doing around the moment, to see why a device dropped out.</span>
          </span>
        </label>
      </div>
    </Dialog>
  );
}
