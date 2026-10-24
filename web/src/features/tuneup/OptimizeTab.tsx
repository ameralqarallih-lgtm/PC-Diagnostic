import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Cpu, Gauge, HardDrive, MemoryStick, MonitorCog, Network, ShieldCheck, ShieldOff, Sparkles } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { apiGet, apiPost } from '@/lib/api/client';
import type { ControlResult, OptimizeCandidate, OptimizePlan } from '@/lib/api/types';
import { formatBytes, formatBytesPerSecond, formatPercent } from '@/lib/format';
import { cn } from '@/lib/utils';
import type { PendingAction } from './ConfirmAction';
import { isSafeMode, useSafeMode } from './safeMode';
import { useControlAction } from './useControl';

/** What each optimization step would do now (refreshed while the tab is open). */
export function useOptimizePlan(enabled = true) {
  return useQuery({
    queryKey: ['tuneup', 'plan'],
    queryFn: ({ signal }) => apiGet<OptimizePlan>('/tuneup/plan', signal),
    refetchInterval: 5000,
    enabled,
  });
}

const item = (p: OptimizeCandidate) => `${p.pid}:${p.startTicks}`;

/** The steps "Optimize now" runs, in order (nothing that ends a program or restarts a driver). */
const STEPS = [
  {
    id: 'lower-priority',
    label: 'Lower busy background programs to below-normal priority',
    icon: Cpu,
  },
  { id: 'trim-memory', label: "Trim programs' idle memory", icon: MemoryStick },
  { id: 'free-standby', label: 'Empty the standby memory', icon: MemoryStick },
  {
    id: 'retrim-drives',
    label: 'Retrim the solid-state drives',
    icon: HardDrive,
  },
  { id: 'flush-dns', label: 'Clear the DNS cache', icon: Network },
] as const;

/**
 * Optimizing the PC's resources: GPU memory (background programs holding it, the graphics driver), memory, the CPU,
 * the drives and the network. Each step shows what it would act on and, in safe mode, what it leaves because other
 * programs are using it; "Optimize now" runs the steps that never end a program.
 */
export function OptimizeTab({ ask }: { ask: (action: PendingAction) => void }) {
  const plan = useOptimizePlan();
  const [safe] = useSafeMode();
  const control = useControlAction();
  const queryClient = useQueryClient();
  const [steps, setSteps] = useState<Set<string>>(() => new Set(STEPS.map((s) => s.id)));
  const [vram, setVram] = useState<Set<string>>(new Set());
  const [running, setRunning] = useState(false);
  const [results, setResults] = useState<{ step: string; result: ControlResult | null; error?: string }[] | null>(null);
  const data = plan.data;
  const blocked = (p: OptimizeCandidate) => !!p.protected || (safe && !!p.inUse);
  const chosenVram = (data?.gpuMemory ?? []).filter((p) => vram.has(item(p)) && !blocked(p));
  const vramTotal = chosenVram.reduce((sum, p) => sum + (p.gpuMemoryBytes ?? 0), 0);

  const runAll = async () => {
    setRunning(true);
    const done: {
      step: string;
      result: ControlResult | null;
      error?: string;
    }[] = [];
    for (const step of STEPS.filter((s) => steps.has(s.id))) {
      try {
        done.push({
          step: step.label,
          result: await apiPost<ControlResult>(`/tuneup/${step.id}`, {
            items: [],
            safe: isSafeMode(),
            confirmed: true,
          }),
        });
      } catch (error) {
        done.push({
          step: step.label,
          result: null,
          error: error instanceof Error ? error.message : 'It did not run.',
        });
      }
      setResults([...done]);
    }
    setRunning(false);
    void queryClient.invalidateQueries({ queryKey: ['tuneup'] });
    void queryClient.invalidateQueries({ queryKey: ['control'] });
  };

  if (plan.error) return <p className="text-sm text-danger">The recorder could not be asked: {plan.error.message}</p>;
  if (!data) return <p className="text-sm text-muted">Looking at what the PC is doing…</p>;

  return (
    <div className="flex flex-col gap-4">
      <SafeNote safe={safe} foreground={data.foregroundName} />

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Sparkles className="size-4" aria-hidden /> Optimize now
          </CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <p className="text-sm text-muted">Runs the chosen steps one after another. None of them ends a program; each leaves what safe mode protects and says so.</p>
          <ul className="flex flex-col gap-1.5">
            {STEPS.map((s) => (
              <li key={s.id}>
                <label className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={steps.has(s.id)} onChange={(e) => setSteps((x) => toggled(x, s.id, e.target.checked))} className="size-4 accent-[var(--accent)]" />
                  <s.icon className="size-4 text-muted" aria-hidden />
                  {s.label}
                  {s.id === 'free-standby' && data.standbyBytes ? <span className="text-xs text-muted">({formatBytes(data.standbyBytes)} now)</span> : null}
                  {s.id === 'free-standby' && safe && data.standbyInUse ? <InUse text={data.standbyInUse} /> : null}
                </label>
              </li>
            ))}
          </ul>
          <div>
            <Button disabled={running || steps.size === 0} onClick={() => void runAll()}>
              <Gauge aria-hidden /> {running ? 'Optimizing…' : 'Optimize now'}
            </Button>
          </div>
          {results && (
            <ul aria-label="What was done" className="flex flex-col gap-1 rounded-md border border-border p-2 text-sm">
              {results.map((r) => (
                <li key={r.step}>
                  <span className={cn('font-medium', r.result?.ok ? 'text-success' : 'text-muted')}>{r.step}:</span> {r.result?.message ?? r.error}
                  {r.result?.freedBytes ? <span className="text-muted"> ({formatBytes(r.result.freedBytes)} freed)</span> : null}
                  {r.result?.skipped?.length ? <Skipped items={r.result.skipped} /> : null}
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <MonitorCog className="size-4" aria-hidden /> GPU memory
          </CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          <p className="text-sm text-muted">
            Windows lets a program give its GPU memory back only by ending it. These background programs hold the most; choose those you do not need (a program with a window open is left in safe mode,
            so no unsaved work is lost).
          </p>
          <Candidates
            label="Programs holding GPU memory"
            rows={data.gpuMemory}
            value={(p) => formatBytes(p.gpuMemoryBytes)}
            chosen={vram}
            blocked={blocked}
            onToggle={(p, on) => setVram((x) => toggled(x, item(p), on))}
            empty="No background program holds much GPU memory."
          />
          <div className="flex flex-wrap gap-2">
            <Button
              variant="danger"
              disabled={chosenVram.length === 0 || control.isPending}
              onClick={() =>
                ask({
                  title: `End ${chosenVram.length} program(s) to free ${formatBytes(vramTotal)} of GPU memory?`,
                  confirmLabel: 'End them',
                  danger: true,
                  body: (
                    <ul className="list-disc pl-5 text-xs">
                      {chosenVram.map((p) => (
                        <li key={item(p)}>
                          {p.name} ({p.pid}): {formatBytes(p.gpuMemoryBytes)}
                        </li>
                      ))}
                    </ul>
                  ),
                  run: (confirmed) =>
                    control.mutate({
                      path: '/tuneup/free-vram',
                      body: { items: chosenVram.map(item), confirmed },
                      label: 'Freeing GPU memory',
                    }),
                })
              }
            >
              Free {formatBytes(vramTotal)} of GPU memory
            </Button>
            {data.gpus.map((g) => (
              <Button
                key={g.instanceId}
                variant="outline"
                disabled={control.isPending || (safe && !!data.gpuRestartInUse)}
                title={safe && data.gpuRestartInUse ? `Safe mode: ${data.gpuRestartInUse}` : 'Gives back memory a driver kept; the screens go dark for a moment'}
                onClick={() =>
                  ask({
                    title: `Restart the ${g.name} driver?`,
                    confirmLabel: 'Restart the driver',
                    danger: true,
                    acknowledge: 'I understand the screens go dark for a moment and programs drawing with the GPU may stop.',
                    body: <p className="text-sm">Windows restarts the graphics driver, which gives back GPU memory it kept. Close games and video first.</p>,
                    run: (confirmed) =>
                      control.mutate({
                        path: '/tuneup/restart-gpu',
                        body: { value: g.instanceId, confirmed },
                        label: 'Restarting the graphics driver',
                      }),
                  })
                }
              >
                Restart {g.name} driver
              </Button>
            ))}
          </div>
          {safe && data.gpuRestartInUse && <InUse text={`The driver is not restarted now: ${data.gpuRestartInUse}.`} />}
        </CardContent>
      </Card>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <Section
          icon={MemoryStick}
          title="Memory"
          note="Trimming moves a program's idle memory to the standby list: still in memory, given straight back if the program needs it, otherwise free for others."
        >
          <Candidates label="Programs holding the most memory" rows={data.memory} value={(p) => formatBytes(p.workingSetBytes)} blocked={blocked} empty="No program holds much memory." />
          <StepButton action="trim-memory" label="Trim their memory" ask={ask} />
          {data.wslBytes ? (
            <StepButton
              action="shutdown-wsl"
              label={`Shut down WSL (${formatBytes(data.wslBytes)})`}
              ask={ask}
              inUse={safe ? data.wslInUse : null}
              confirm="WSL's Linux programs and Docker stop; they start again when needed."
            />
          ) : null}
        </Section>
        <Section icon={Cpu} title="CPU" note="Busy background programs (no window) run at below-normal priority, so what you use gets the processor first. It lasts until they end.">
          <Candidates label="Busy background programs" rows={data.cpu} value={(p) => formatPercent(p.cpuPercent, 1)} blocked={blocked} empty="No background program is busy." />
          <StepButton action="lower-priority" label="Lower their priority" ask={ask} />
        </Section>
        <Section icon={HardDrive} title="Drives" note="Retrimming tells a solid-state drive which blocks are free, which keeps writes fast. Hard disks are defragmented by Windows on its schedule.">
          <ul aria-label="Drives" className="divide-y divide-border rounded-md border border-border text-sm">
            {data.drives.map((d) => (
              <li key={d.mount} className="flex flex-wrap items-center justify-between gap-2 px-3 py-1.5">
                <span>
                  <span className="font-medium">{d.mount}</span> <span className="text-xs text-muted">{d.model}</span>
                </span>
                <span className="flex items-center gap-2 text-xs">
                  <Badge tone={d.ssd ? 'accent' : 'neutral'}>{d.ssd ? 'SSD' : 'Hard disk'}</Badge>
                  {d.activePercent != null && <span className="text-muted">{formatPercent(d.activePercent, 0)} active</span>}
                  {safe && d.inUse && <InUse text={d.inUse} />}
                </span>
              </li>
            ))}
          </ul>
          <StepButton action="retrim-drives" label="Retrim the solid-state drives" ask={ask} />
        </Section>
        <Section icon={Network} title="Network" note="Clearing the DNS cache makes Windows look names up afresh (after a site moved, or a network change).">
          <StepButton action="flush-dns" label="Clear the DNS cache" ask={ask} />
        </Section>
      </div>
    </div>
  );
}

function toggled(set: Set<string>, id: string, on: boolean) {
  const next = new Set(set);
  if (on) next.add(id);
  else next.delete(id);
  return next;
}

function SafeNote({ safe, foreground }: { safe: boolean; foreground: string | null }) {
  return (
    <p role="status" className={cn('flex items-start gap-2 rounded-md border px-3 py-2 text-sm', safe ? 'border-success/40 bg-success/10' : 'border-warning/40 bg-warning/10')}>
      {safe ? <ShieldCheck className="mt-0.5 size-4 shrink-0 text-success" aria-hidden /> : <ShieldOff className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden />}
      <span>
        {safe ? 'Safe mode is on: what other programs are using now is left alone' : 'Safe mode is off: actions also reach what other programs are using'}
        {foreground ? ` (in front now: ${foreground})` : ''}. Switch it at the top of the page.
      </span>
    </p>
  );
}

function InUse({ text }: { text: string }) {
  return <span className="text-xs text-warning">In use: {text}</span>;
}

function Skipped({ items }: { items: string[] }) {
  return (
    <ul className="mt-0.5 list-disc pl-5 text-xs text-warning">
      {items.slice(0, 6).map((s) => (
        <li key={s}>Safe mode left {s}</li>
      ))}
      {items.length > 6 && <li>and {items.length - 6} more</li>}
    </ul>
  );
}

function Section({ icon: Icon, title, note, children }: { icon: typeof Cpu; title: string; note: string; children: ReactNode }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Icon className="size-4" aria-hidden /> {title}
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <p className="text-sm text-muted">{note}</p>
        {children}
      </CardContent>
    </Card>
  );
}

function Candidates({
  label,
  rows,
  value,
  blocked,
  chosen,
  onToggle,
  empty,
}: {
  label: string;
  rows: OptimizeCandidate[];
  value: (p: OptimizeCandidate) => string;
  blocked: (p: OptimizeCandidate) => boolean;
  chosen?: Set<string>;
  onToggle?: (p: OptimizeCandidate, on: boolean) => void;
  empty: string;
}) {
  if (rows.length === 0) return <p className="text-sm text-muted">{empty}</p>;
  return (
    <ul aria-label={label} className="max-h-64 divide-y divide-border overflow-auto rounded-md border border-border text-sm">
      {rows.map((p) => (
        <li key={item(p)} className={cn('flex flex-wrap items-center gap-2 px-3 py-1.5', blocked(p) && 'opacity-60')}>
          {onToggle && (
            <input
              type="checkbox"
              aria-label={`Choose ${p.name} (${p.pid})`}
              checked={chosen?.has(item(p)) ?? false}
              disabled={blocked(p)}
              onChange={(e) => onToggle(p, e.target.checked)}
              className="size-4 accent-[var(--accent)]"
            />
          )}
          <span className="min-w-0 flex-1 truncate" title={p.name}>
            {p.name} <span className="font-mono text-xs text-muted">{p.pid}</span>
          </span>
          <span className="tabular-nums">{value(p)}</span>
          <span className="w-full text-xs text-muted sm:w-auto">
            {[
              p.gpuPercent ? `GPU ${formatPercent(p.gpuPercent, 1)}` : null,
              p.cpuPercent ? `CPU ${formatPercent(p.cpuPercent, 1)}` : null,
              p.netBytesPerSec ? `net ${formatBytesPerSecond(p.netBytesPerSec)}` : null,
            ]
              .filter(Boolean)
              .join(' · ')}
          </span>
          {p.protected ? (
            <span className="w-full text-xs text-muted">Protected: {p.protected}</span>
          ) : p.inUse ? (
            <span className="w-full">
              <InUse text={p.inUse} />
            </span>
          ) : null}
        </li>
      ))}
    </ul>
  );
}

function StepButton({ action, label, ask, inUse, confirm }: { action: string; label: string; ask: (action: PendingAction) => void; inUse?: string | null; confirm?: string }) {
  const control = useControlAction();
  const run = () =>
    control.mutate({
      path: `/tuneup/${action}`,
      body: { items: [], confirmed: true },
      label,
    });
  return (
    <div className="flex flex-wrap items-center gap-2">
      <Button
        variant="outline"
        disabled={control.isPending || !!inUse}
        title={inUse ? `Safe mode: ${inUse}` : undefined}
        onClick={() =>
          confirm
            ? ask({
                title: `${label}?`,
                confirmLabel: label,
                body: <p className="text-sm">{confirm}</p>,
                run: () => run(),
              })
            : run()
        }
      >
        {control.isPending ? 'Working…' : label}
      </Button>
      {inUse && <InUse text={inUse} />}
    </div>
  );
}
