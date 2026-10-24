import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { HardDriveDownload, MonitorCheck, ShieldAlert, ShieldCheck, Trash2, TriangleAlert, Upload } from 'lucide-react';
import { useRef, useState } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useConfirm } from '@/components/ui/confirm';
import { useToast } from '@/components/ui/toast';
import { apiGet, apiPost, apiRequest, apiUpload } from '@/lib/api/client';
import type { WindowsDriversState } from '@/lib/api/types';

const KEY = ['windows-drivers'];

/**
 * Windows drivers for connected devices that have none (Device Manager's yellow mark), never the ones Windows starts
 * with. A driver (a zip with its INF, .sys and signed .cat) is imported, shown with who made and signed it and the
 * devices it fits, and installed for one of them: the person at the PC confirms it on the PC's own screen, a restore
 * point can be made first, and Windows installs it only if it suits the device (never forced). It can be removed.
 */
export function WindowsDriversCard() {
  const client = useQueryClient();
  const confirm = useConfirm();
  const toast = useToast();
  const picker = useRef<HTMLInputElement>(null);
  const [restorePoint, setRestorePoint] = useState(true);
  const state = useQuery({ queryKey: KEY, queryFn: ({ signal }) => apiGet<WindowsDriversState>('/drivers/windows', signal), refetchInterval: 30_000 });
  const set = (s: WindowsDriversState | undefined) => s && client.setQueryData(KEY, s);
  const importing = useMutation({
    mutationFn: (file: File) => apiUpload<WindowsDriversState>('/drivers/windows/import', new Blob([file], { type: 'application/zip' })),
    onSuccess: set,
  });
  const install = useMutation({
    mutationFn: (a: { id: string; device: string }) => apiPost<WindowsDriversState>(`/drivers/windows/${encodeURIComponent(a.id)}/install`, { device: a.device, restorePoint }),
    onMutate: () => toast.show({ title: 'Confirm on the PC', description: 'A window on the PC’s screen asks before anything is installed (it closes by itself after 2 minutes).', tone: 'info' }),
    onSuccess: (s) => {
      set(s);
      if (s.message) toast.show({ title: 'Windows driver', description: s.message, tone: s.message.startsWith('Installed') ? 'success' : 'info' });
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => apiRequest<WindowsDriversState>('DELETE', `/drivers/windows/${encodeURIComponent(id)}`),
    onSuccess: set,
  });
  const devices = state.data?.devices ?? [];
  const packages = state.data?.packages ?? [];
  const nameOf = (instanceId: string) => devices.find((d) => d.instanceId === instanceId)?.name ?? instanceId;
  const error = importing.error ?? install.error ?? remove.error ?? state.error;
  return (
    <Card>
      <CardHeader className="flex-wrap">
        <CardTitle className="flex items-center gap-2">
          <HardDriveDownload className="size-4 text-accent" aria-hidden /> Devices without a driver
        </CardTitle>
        <Button size="sm" className="ml-auto" disabled={importing.isPending} onClick={() => picker.current?.click()}>
          <Upload aria-hidden /> {importing.isPending ? 'Importing…' : 'Import a Windows driver'}
        </Button>
        <input
          ref={picker}
          type="file"
          accept=".zip,application/zip"
          aria-label="Windows driver (zip)"
          className="hidden"
          onChange={(e) => {
            const file = e.target.files?.[0];
            e.target.value = '';
            if (file) importing.mutate(file);
          }}
        />
        <p className="basis-full text-xs text-muted">
          Connected devices Windows has no working driver for. Import a driver from the device’s maker (a zip with its .inf, .sys and signed .cat), check who made and signed it, then
          install it for the device: you confirm it on this PC’s screen, and Windows installs it only if it suits the device. Devices Windows starts with (disks, storage controllers,
          processors) are never listed.
        </p>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <section aria-label="Devices without a driver">
          {state.isLoading ? (
            <p className="text-sm text-muted">Looking at the devices…</p>
          ) : devices.length === 0 ? (
            <p className="flex items-center gap-2 text-sm text-muted">
              <MonitorCheck className="size-4 text-success" aria-hidden /> Every connected device has a driver.
            </p>
          ) : (
            <ul className="flex flex-col divide-y divide-border" aria-label="Devices">
              {devices.map((d) => (
                <li key={d.instanceId} aria-label={d.name} className="flex flex-col gap-0.5 py-2 text-sm">
                  <span className="flex flex-wrap items-center gap-2">
                    <TriangleAlert className="size-4 text-warning" aria-hidden />
                    <span className="font-medium">{d.name}</span>
                    {d.class && <span className="text-xs text-muted">{d.class}</span>}
                    <Badge tone="warning">{d.problem}</Badge>
                  </span>
                  <span className="break-all pl-6 font-mono text-[11px] text-muted">{[...d.hardwareIds.slice(0, 2), ...d.compatibleIds.slice(0, 1)].join(' · ')}</span>
                </li>
              ))}
            </ul>
          )}
        </section>

        {packages.length > 0 && (
          <section aria-label="Windows drivers imported" className="flex flex-col gap-2 border-t border-border pt-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h3 className="text-sm font-medium">Imported drivers</h3>
              <label className="flex items-center gap-2 text-xs">
                <input type="checkbox" checked={restorePoint} onChange={(e) => setRestorePoint(e.target.checked)} className="accent-[var(--accent)]" />
                Make a restore point before installing
              </label>
            </div>
            <ul className="flex flex-col divide-y divide-border" aria-label="Windows drivers">
              {packages.map((p) => (
                <li key={p.id} aria-label={p.inf} className="flex flex-col gap-1 py-2 text-sm">
                  <span className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{p.inf}</span>
                    <span className="text-xs text-muted">
                      {p.version} · {p.date} · {p.provider ?? 'maker not said'}
                      {p.class ? ` · ${p.class}` : ''}
                    </span>
                    {p.signature === 'Signed' ? (
                      <Badge tone="success">
                        <ShieldCheck className="size-3" aria-hidden /> Signed{p.signer ? ` by ${p.signer}` : ''}
                      </Badge>
                    ) : (
                      <Badge tone="danger">
                        <ShieldAlert className="size-3" aria-hidden /> Not signed
                      </Badge>
                    )}
                    {p.installed && <Badge tone="neutral">Installed as {p.installed}</Badge>}
                    <Button
                      size="icon"
                      variant="ghost"
                      className="ml-auto size-8"
                      aria-label={`Remove ${p.inf}`}
                      title="Remove"
                      disabled={remove.isPending}
                      onClick={async () =>
                        (await confirm({
                          title: `Remove ${p.inf}?`,
                          description: p.installed
                            ? `It is uninstalled from Windows (${p.installed}) after you confirm it on the PC, then deleted here.`
                            : 'It was not installed; it is deleted here.',
                          action: 'Remove',
                        })) && remove.mutate(p.id)
                      }
                    >
                      <Trash2 aria-hidden />
                    </Button>
                  </span>
                  {p.problem && <span className="text-xs text-warning">{p.problem}</span>}
                  {p.matches.length > 0 && p.signature === 'Signed' && (
                    <span className="flex flex-wrap items-center gap-2 text-xs">
                      <span className="text-muted">Fits</span>
                      {p.matches.map((m) => (
                        <Button
                          key={m}
                          size="sm"
                          disabled={install.isPending}
                          onClick={async () =>
                            (await confirm({
                              title: `Install ${p.inf} for ${nameOf(m)}?`,
                              description: `From ${p.provider ?? 'an unnamed maker'}, signed by ${p.signer ?? 'an unknown signer'}. ${restorePoint ? 'A restore point is made first. ' : ''}You confirm it again on the PC’s screen.`,
                              action: 'Install',
                              danger: false,
                            })) && install.mutate({ id: p.id, device: m })
                          }
                        >
                          {install.isPending && install.variables?.id === p.id && install.variables.device === m ? 'Waiting for the PC…' : `Install for ${nameOf(m)}`}
                        </Button>
                      ))}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          </section>
        )}
        {error && (
          <p role="alert" className="text-sm text-danger">
            {error.message}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
