import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Activity, Cpu, MonitorSmartphone, PlugZap, ShieldCheck, Trash2, Upload } from 'lucide-react';
import { useRef } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useConfirm } from '@/components/ui/confirm';
import { useToast } from '@/components/ui/toast';
import { apiGet, apiPost, apiRequest, apiUpload } from '@/lib/api/client';
import type { ScreenDriverInfo } from '@/lib/api/types';
import { WindowsDriversCard } from './WindowsDriversCard';

const KEY = ['drivers'];

/** The driver packages (screens and sensors), as the recorder's screen host found them. */
export function useDrivers() {
  return useQuery({ queryKey: KEY, queryFn: ({ signal }) => apiGet<ScreenDriverInfo[]>('/drivers', signal), refetchInterval: 15_000 });
}

/**
 * Driver packages: those that come with PC Diagnostic and those imported (a zip with driver.json), for screens that
 * are not Windows monitors and for sensors it has no built-in support for. An imported package is checked by the
 * recorder, then waits for approval by its content (a changed package waits again); one can be withdrawn or removed.
 */
export function DriversCard() {
  const client = useQueryClient();
  const confirm = useConfirm();
  const toast = useToast();
  const drivers = useDrivers();
  const picker = useRef<HTMLInputElement>(null);
  const set = (list: ScreenDriverInfo[]) => client.setQueryData(KEY, list);
  const importing = useMutation({
    mutationFn: (file: File) => apiUpload<ScreenDriverInfo[]>('/drivers/import', new Blob([file], { type: 'application/zip' })),
    onSuccess: (list) => {
      set(list);
      toast.show({ title: 'Driver package imported', description: 'Check what it is, then approve it to use it.', tone: 'success' });
    },
  });
  const approve = useMutation({
    mutationFn: (d: { id: string; sha256: string; approve: boolean }) => apiPost<ScreenDriverInfo[]>(`/drivers/${encodeURIComponent(d.id)}/approve`, d),
    onSuccess: set,
  });
  const remove = useMutation({
    mutationFn: (id: string) => apiRequest<ScreenDriverInfo[]>('DELETE', `/drivers/${encodeURIComponent(id)}`),
    onSuccess: (list) => list && set(list),
  });
  const list = drivers.data ?? [];
  const error = importing.error ?? approve.error ?? remove.error;
  return (
    <Card>
      <CardHeader className="flex-wrap">
        <CardTitle className="flex items-center gap-2">
          <PlugZap className="size-4 text-accent" aria-hidden /> Driver packages
        </CardTitle>
        <Button size="sm" className="ml-auto" disabled={importing.isPending} onClick={() => picker.current?.click()}>
          <Upload aria-hidden /> {importing.isPending ? 'Importing…' : 'Import a package'}
        </Button>
        <input
          ref={picker}
          type="file"
          accept=".zip,application/zip"
          aria-label="Driver package (zip)"
          className="hidden"
          onChange={(e) => {
            const file = e.target.files?.[0];
            e.target.value = '';
            if (file) importing.mutate(file);
          }}
        />
        <p className="basis-full text-xs text-muted">
          For screens that are not Windows monitors, and sensors PC Diagnostic does not read by itself (a probe on a serial port, a HID device, a gadget that answers on the local
          network). A package is a zip with its driver.json; once imported it is used only after you approve it. Packages with code run without administrator rights: approve only
          those you trust.
        </p>
      </CardHeader>
      <CardContent>
        {drivers.isLoading ? (
          <p className="text-sm text-muted">Loading…</p>
        ) : list.length === 0 ? (
          <p className="text-sm text-muted">The recorder has not reported its drivers yet.</p>
        ) : (
          <ul className="flex flex-col divide-y divide-border" aria-label="Driver packages">
            {list.map((d) => {
              const kinds = d.capabilities ?? ['screen'];
              return (
                <li key={d.id} aria-label={d.name} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2 text-sm">
                  <Cpu className="size-4 shrink-0 text-muted" aria-hidden />
                  <span className="font-medium">{d.name}</span>
                  <span className="text-xs text-muted">
                    {d.version} · {d.publisher}
                  </span>
                  {kinds.map((k) => (
                    <span key={k} className="inline-flex items-center gap-1 rounded bg-surface-muted px-1.5 py-0.5 text-[11px] text-muted">
                      {k === 'sensors' ? <Activity className="size-3" aria-hidden /> : <MonitorSmartphone className="size-3" aria-hidden />}
                      {k === 'sensors' ? 'Sensors' : 'Screens'}
                    </span>
                  ))}
                  {d.builtIn ? (
                    <Badge tone="neutral">Built in</Badge>
                  ) : d.approved && d.loaded && !d.problem ? (
                    <Badge tone="success">
                      <ShieldCheck className="size-3" aria-hidden /> Approved
                    </Badge>
                  ) : (
                    <Badge tone={d.approved ? 'danger' : 'warning'}>{d.problem ?? 'Not in use'}</Badge>
                  )}
                  <span className="ml-auto flex gap-1">
                    {!d.builtIn && d.sha256 && (
                      <Button
                        size="sm"
                        variant={d.approved ? 'outline' : 'default'}
                        disabled={approve.isPending}
                        onClick={async () =>
                          (d.approved ||
                            (await confirm({
                              title: `Approve ${d.name}?`,
                              description: `From ${d.publisher}. ${kinds.includes('sensors') ? 'Its sensors are read every few seconds' : 'It drives its screens'}${d.sha256 ? ` (content SHA-256 ${d.sha256.slice(0, 16)}…)` : ''}. Approve only packages you trust.`,
                              action: 'Approve',
                              danger: false,
                            }))) &&
                          approve.mutate({ id: d.id, sha256: d.sha256!, approve: !d.approved })
                        }
                      >
                        {d.approved ? 'Withdraw approval' : 'Approve'}
                      </Button>
                    )}
                    {!d.builtIn && (
                      <Button
                        size="icon"
                        variant="ghost"
                        className="size-8"
                        aria-label={`Remove ${d.name}`}
                        title="Remove"
                        disabled={remove.isPending}
                        onClick={async () =>
                          (await confirm({ title: `Remove ${d.name}?`, description: 'The package is deleted from this PC and its approval forgotten. Its readings stop.', action: 'Remove' })) &&
                          remove.mutate(d.id)
                        }
                      >
                        <Trash2 aria-hidden />
                      </Button>
                    )}
                  </span>
                  <span className="basis-full pl-7 text-xs text-muted">
                    {[...d.models, ...(d.sensors ?? [])].join(', ')}
                    {!d.builtIn && d.sha256 && <span className="ml-2 font-mono">SHA-256 {d.sha256.slice(0, 16)}…</span>}
                  </span>
                </li>
              );
            })}
          </ul>
        )}
        {error && (
          <p role="alert" className="mt-2 text-sm text-danger">
            {error.message}
          </p>
        )}
      </CardContent>
    </Card>
  );
}

/** Devices › Drivers: the driver packages, and Windows drivers for devices that have none. */
export function DriversPage() {
  return (
    <div className="flex flex-col gap-3">
      <header className="flex flex-wrap items-baseline gap-x-4">
        <h1 className="text-xl font-semibold">Drivers</h1>
        <p className="text-sm text-muted">Driver packages for screens and sensors PC Diagnostic does not support by itself, and Windows drivers for devices that have none.</p>
      </header>
      <DriversCard />
      <WindowsDriversCard />
    </div>
  );
}
