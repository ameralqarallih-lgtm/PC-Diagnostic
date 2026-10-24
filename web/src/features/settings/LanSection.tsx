import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Copy, KeyRound, Laptop, Smartphone, Trash2, Wifi } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import { DataTable } from '@/components/data/DataTable';
import { QrCode } from '@/components/ui/qr-code';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useToast } from '@/components/ui/toast';
import { ApiError, apiGet, apiPost, apiRequest } from '@/lib/api/client';
import type { LanStatus } from '@/lib/api/types';
import { formatDateTime } from '@/lib/format';

function secondsLeft(expiresAt: string, now: number): number {
  return Math.max(0, Math.round((Date.parse(expiresAt) - now) / 1000));
}

/**
 * Access from other devices on the local network: off by default; when on, the dashboard also answers over HTTPS
 * on a fixed port, and each device pairs once with a code shown here. Managed on this PC only (the server refuses
 * these requests from other devices, and this card is then replaced by a note).
 */
export function LanSection() {
  const queryClient = useQueryClient();
  const toast = useToast();
  const portId = useId();
  const { data, error } = useQuery({
    queryKey: ['lan'],
    queryFn: ({ signal }) => apiGet<LanStatus>('/lan', signal),
    retry: false,
    // While a pairing code is shown, watch for the device that uses it.
    refetchInterval: (query) => (query.state.data?.pairing ? 3000 : false),
  });
  const deviceCount = useRef<number | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [port, setPort] = useState(47443);
  const [restarting, setRestarting] = useState(false);
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (data) {
      setEnabled(data.enabled);
      setPort(data.port);
    }
  }, [data]);

  useEffect(() => {
    if (!data) return;
    if (deviceCount.current !== null && data.devices.length > deviceCount.current) {
      toast.show({ title: `${data.devices[0]?.name ?? 'A device'} is paired`, tone: 'success' });
    }
    deviceCount.current = data.devices.length;
  }, [data, toast]);

  useEffect(() => {
    if (!data?.pairing) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [data?.pairing]);

  const apply = useMutation({
    mutationFn: () => apiRequest('PUT', '/lan', { enabled, port }),
    onSuccess: () => {
      setRestarting(true);
      // The host restarts itself in a few seconds, on the same address for this PC.
      setTimeout(() => {
        setRestarting(false);
        void queryClient.invalidateQueries({ queryKey: ['lan'] });
      }, 4000);
    },
    onError: (e) => toast.show({ title: 'Network access could not be changed', description: e instanceof Error ? e.message : undefined, tone: 'error' }),
  });
  const code = useMutation({
    mutationFn: () => apiPost('/lan/pairing-code'),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['lan'] }),
  });
  const confirm = useConfirm();
  const revoke = useMutation({
    mutationFn: (id: string) => apiRequest('DELETE', `/lan/devices/${id}`),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['lan'] }),
  });

  if (error) {
    // Another device: network access is managed on the PC itself.
    return error instanceof ApiError && error.status === 403 ? (
      <Card className="lg:col-span-2">
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
          Access from other devices
        </CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-muted">You are on a paired device. Network access, paired devices and settings are changed on the PC that runs PC Diagnostic.</p>
        </CardContent>
      </Card>
    ) : null;
  }

  if (!data) return null;
  const changed = enabled !== data.enabled || port !== data.port;
  const pairing = data.pairing;
  const left = pairing ? secondsLeft(pairing.expiresAt, now) : 0;

  return (
    <Card id="network" className="scroll-mt-28 lg:col-span-2">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          Access from other devices
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <p className="text-sm text-muted">
          Open the dashboard from a phone, tablet or another computer on the same network. Off by default. When on, the dashboard also answers over HTTPS on the
          port below; each device pairs once with a code shown here, only private network addresses are accepted, and paired devices can view everything,
          control recording and capture, but cannot change settings or network access.
        </p>
        {!data.supported ? (
          <p className="text-sm text-unavailable">{data.unsupported}</p>
        ) : (
          <>
            <div className="flex flex-wrap items-end gap-4">
              <label className="flex items-center gap-2.5 text-sm">
                <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} className="size-4 accent-[var(--accent)]" />
                <span className="font-medium">Allow access from the local network</span>
              </label>
              <div className="flex flex-col gap-1">
                <label htmlFor={portId} className="text-xs text-muted">
                  HTTPS port
                </label>
                <input
                  id={portId}
                  type="number"
                  min={1024}
                  max={65535}
                  value={port}
                  onChange={(e) => setPort(Number(e.target.value))}
                  aria-invalid={port < 1024 || port > 65535 || undefined}
                  className="h-9 w-28 rounded-md border border-border bg-background px-2 text-sm aria-[invalid=true]:border-danger"
                />
              </div>
              <Button type="button" disabled={!changed || apply.isPending || restarting || port < 1024 || port > 65535} onClick={() => apply.mutate()}>
                <Wifi aria-hidden /> {restarting ? 'Restarting the dashboard…' : 'Apply'}
              </Button>
            </div>
            {changed && <p className="text-xs text-muted">Applying restarts the dashboard host for a few seconds; this page reconnects by itself.</p>}

            {data.problem && (
              <p role="alert" className="rounded-md border border-danger/50 bg-danger/10 px-3 py-2 text-sm">
                {data.problem}
              </p>
            )}

            {data.listening && (
              <div className="flex flex-col gap-3 rounded-md border border-border p-3">
                <div>
                  <p className="text-sm font-medium">On another device, open</p>
                  <ul className="mt-1 flex flex-col gap-1">
                    {data.urls.map((url) => (
                      <li key={url} className="flex items-center gap-2">
                        <code className="font-mono text-sm">{url}</code>
                        <button
                          type="button"
                          className="text-muted hover:text-foreground"
                          aria-label={`Copy ${url}`}
                          onClick={() => void navigator.clipboard?.writeText(url).then(() => toast.show({ title: 'Address copied', tone: 'success' }))}
                        >
                          <Copy className="size-3.5" aria-hidden />
                        </button>
                      </li>
                    ))}
                  </ul>
                  <p className="mt-2 text-xs text-muted">
                    The browser warns about the certificate the first time, because it was made by this PC rather than bought from an authority. Check that the
                    certificate's SHA-256 fingerprint matches, then continue:
                  </p>
                  <p className="mt-1 break-all font-mono text-xs">{data.fingerprint}</p>
                  <p className="mt-2 text-xs text-muted">
                    The first time, Windows may ask whether PcDiag.Web may use the network: allow it for private networks only.
                  </p>
                </div>

                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  {data.apkUrl && data.urls[0] && (
                    <div className="flex flex-col items-start gap-2">
                      <p className="flex items-center gap-1.5 text-sm font-medium">
                        <Smartphone className="size-4" aria-hidden /> Android app
                      </p>
                      <QrCode text={new URL(data.apkUrl, data.urls[0]).toString()} size={150} label="QR code to download the Android app" />
                      <p className="text-xs text-muted">
                        Scan with the phone's camera to download it (accept the certificate warning, then allow installing from this source). The app
                        finds this PC by itself, pins its certificate and keeps its key in the phone's secure storage; it can also capture an incident in one tap.
                      </p>
                    </div>
                  )}
                  <div aria-live="polite" className="flex flex-col items-start gap-2">
                    {pairing && left > 0 ? (
                      <>
                        <p className="text-sm font-medium">Pair a device</p>
                        {pairing.link && <QrCode text={pairing.link} size={150} label="QR code to pair the Android app" />}
                        <p className="text-xs text-muted">Scan with the app's phone camera, or enter the code on the device (works once):</p>
                        <p className="font-mono text-3xl font-semibold tracking-[0.2em]">{pairing.code}</p>
                        <p className="text-xs text-muted">Expires in {Math.floor(left / 60)}:{String(left % 60).padStart(2, '0')}</p>
                      </>
                    ) : (
                      <Button type="button" variant="ghost" onClick={() => code.mutate()} disabled={code.isPending}>
                        <KeyRound aria-hidden /> Show pairing code
                      </Button>
                    )}
                  </div>
                </div>
              </div>
            )}

            <div>
              <h3 className="text-xs font-semibold uppercase tracking-wide text-muted">Paired devices</h3>
              {data.devices.length === 0 ? (
                <p className="text-sm text-muted">None yet.</p>
              ) : (
                <DataTable
                  viewKey="settings.lan-devices"
                  label="Paired devices"
                  rows={data.devices}
                  columns={[
                    { id: 'name', header: 'Name', value: (d) => d.name, groupable: false },
                    { id: 'paired', header: 'Paired', value: (d) => Date.parse(d.pairedAt), groupable: false, firstDesc: true },
                    { id: 'used', header: 'Last used', value: (d) => (d.lastSeenAt ? Date.parse(d.lastSeenAt) : null), groupable: false, firstDesc: true },
                    { id: 'address', header: 'Address', value: (d) => d.lastAddress ?? null },
                  ]}
                  rowKey={(d) => d.id}
                  defaultView={{ groupBy: [], sort: [{ id: 'used', desc: true }] }}
                  className="mt-1"
                  listClassName="divide-y divide-border rounded-md border border-border"
                  compact
                  renderRow={(device) => (
                    <div className="flex items-center justify-between gap-2 px-3 py-2">
                      <div className="flex min-w-0 items-center gap-2.5">
                        <Laptop className="size-4 shrink-0 text-muted" aria-hidden />
                        <div className="min-w-0">
                          <p className="truncate text-sm font-medium">{device.name}</p>
                          <p className="text-xs text-muted">
                            Paired {formatDateTime(device.pairedAt)} · last used {formatDateTime(device.lastSeenAt)}
                            {device.lastAddress ? ` from ${device.lastAddress}` : ''}
                          </p>
                        </div>
                      </div>
                      <Button type="button" variant="ghost" size="sm" onClick={async () => (await confirm({ title: `Remove ${device.name}?`, description: 'It can no longer open the dashboard until it is paired again.', action: 'Remove' })) && revoke.mutate(device.id)} aria-label={`Remove ${device.name}`}>
                        <Trash2 aria-hidden /> Remove
                      </Button>
                    </div>
                  )}
                />
              )}
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}
