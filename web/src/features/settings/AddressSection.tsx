import { useMutation, useQuery } from '@tanstack/react-query';
import { Globe } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { apiGet, apiRequest } from '@/lib/api/client';

interface AddressStatus {
  port: number;
  listening: number;
  problem: string | null;
}

/**
 * The dashboard's address on this PC: the same port every time PC Diagnostic starts (it used to be any free one, a new
 * address at each start), chosen here. Changing it restarts the web host and opens the dashboard at the new address.
 */
export function AddressSection() {
  const status = useQuery({ queryKey: ['address'], queryFn: ({ signal }) => apiGet<AddressStatus>('/lan/address', signal) });
  const [port, setPort] = useState<string>('');
  useEffect(() => {
    if (status.data && port === '') setPort(String(status.data.port));
  }, [status.data, port]);
  const change = useMutation({
    mutationFn: (p: number) => apiRequest<{ url: string }>('PUT', '/lan/address', { port: p }),
    onSuccess: (r) => {
      // The host starts again on the new port: the dashboard opens there (the session carries over).
      const url = r?.url ?? `http://127.0.0.1:${port}/`;
      window.setTimeout(() => window.location.assign(`${url.replace(/\/$/, '')}${window.location.pathname}${window.location.hash}`), 2500);
    },
  });
  const value = Number(port);
  const valid = Number.isInteger(value) && value >= 1024 && value <= 65535;
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Globe className="size-4 text-accent" aria-hidden /> Address on this PC
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        <p className="text-muted">
          The dashboard opens at the same address every time PC Diagnostic starts:{' '}
          <code className="font-mono text-foreground">http://127.0.0.1:{status.data?.listening || status.data?.port || '…'}/</code>
        </p>
        {status.data?.problem && (
          <p role="alert" className="text-warning">
            {status.data.problem}
          </p>
        )}
        <label className="flex flex-wrap items-center gap-3">
          <span className="w-32 shrink-0 text-muted">Port</span>
          <input
            className="h-8 w-28 rounded-md border border-border bg-background px-2 font-mono"
            inputMode="numeric"
            aria-label="Port on this PC"
            value={port}
            onChange={(e) => setPort(e.target.value.replace(/\D/g, '').slice(0, 5))}
          />
          <Button size="sm" disabled={!valid || change.isPending || value === status.data?.port} onClick={() => change.mutate(value)}>
            {change.isSuccess ? 'Opening the new address…' : 'Use this port'}
          </Button>
        </label>
        {!valid && port !== '' && <p className="text-xs text-danger">A port is 1024–65535.</p>}
        {change.error && <p className="text-xs text-danger">{(change.error as Error).message}</p>}
        <p className="text-xs text-muted">Another program using it: a free one is used until it is free, and this says so.</p>
      </CardContent>
    </Card>
  );
}
