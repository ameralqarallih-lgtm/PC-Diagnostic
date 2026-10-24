import { useNavigate, useSearch } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { ShieldCheck, ShieldOff } from 'lucide-react';
import { cn } from '@/lib/utils';
import { OptimizeTab } from './OptimizeTab';
import { useSafeMode } from './safeMode';
import { ConfirmAction, type PendingAction } from './ConfirmAction';
import { ProcessesTab } from './ProcessesTab';
import { recommend, type TuneUpTab } from './recommend';
import { AdviceTab, CleanupTab, ServicesTab, StartupTab, SystemTab } from './TuneUpTabs';
import { useControlProcesses, useControlServices, useLiveOnce, useTuneUpReport } from './useControl';
import { TabFade, TabUnderline } from '@/components/ui/tab-indicator';

const TABS: { id: TuneUpTab; label: string }[] = [
  { id: 'advice', label: 'Advice' },
  { id: 'optimize', label: 'Optimize' },
  { id: 'processes', label: 'Processes' },
  { id: 'services', label: 'Services' },
  { id: 'startup', label: 'Startup' },
  { id: 'cleanup', label: 'Clean up' },
  { id: 'system', label: 'System' },
];

/**
 * Making the PC faster: advice from what it is doing now, control of processes, services and startup programs, a
 * clean-up of caches and leftovers, and the settings that matter for speed. The elevated recorder carries every
 * change out and checks it again; nothing happens without the user confirming.
 */
export function TuneUpPage() {
  const search = useSearch({ from: '/tune-up' });
  const navigate = useNavigate({ from: '/tune-up' });
  const tab: TuneUpTab = search.tab ?? 'advice';
  const [pending, setPending] = useState<PendingAction | null>(null);
  const processes = useControlProcesses(tab === 'advice' || tab === 'processes');
  const services = useControlServices(tab === 'advice' || tab === 'services');
  const report = useTuneUpReport();
  const live = useLiveOnce();
  const advice = useMemo(() => recommend(report.data, processes.data, services.data, live.data?.live), [report.data, processes.data, services.data, live.data]);
  const select = (next: TuneUpTab) => void navigate({ search: next === 'advice' ? {} : { tab: next }, replace: true });
  const high = advice.filter((a) => a.level !== 'low').length;
  const [safe, setSafe] = useSafeMode();

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Tune-up</h1>
          <p className="text-sm text-muted">Make this PC faster: advice from what it is doing now, the programs and services that run, what starts with Windows, and caches to clean.</p>
        </div>
        <label
          className={cn('flex max-w-sm cursor-pointer items-start gap-2.5 rounded-md border px-3 py-2', safe ? 'border-success/40 bg-success/10' : 'border-warning/40 bg-warning/10')}
          title="On: every action leaves what other programs are using now (the program in front, one with a window open, one busy with the CPU, GPU, network or a disk, a running browser's cache) and says what it left"
        >
          <input type="checkbox" role="switch" checked={safe} onChange={(e) => setSafe(e.target.checked)} className="mt-0.5 size-4 accent-[var(--accent)]" aria-label="Safe mode" />
          <span className="text-sm">
            <span className="flex items-center gap-1.5 font-medium">
              {safe ? <ShieldCheck className="size-4 text-success" aria-hidden /> : <ShieldOff className="size-4 text-warning" aria-hidden />}
              Safe mode {safe ? 'on' : 'off'}
            </span>
            <span className="block text-xs text-muted">{safe ? 'Leaves anything other programs are using now.' : 'Actions also reach what other programs are using.'}</span>
          </span>
        </label>
      </div>

      <div role="tablist" aria-label="Tune-up" className="-mx-1 flex gap-1 overflow-x-auto border-b border-border px-1">
        {TABS.map((t) => (
          <button
            key={t.id}
            type="button"
            role="tab"
            aria-selected={tab === t.id}
            onClick={() => select(t.id)}
            className={cn('relative -mb-px flex shrink-0 items-center gap-1.5 border-b-2 border-transparent px-3 py-2 text-sm transition-colors', tab === t.id ? 'font-medium' : 'text-muted hover:text-foreground')}
          >
            {tab === t.id && <TabUnderline group="tuneup-tabs" />}
            {t.label}
            {t.id === 'advice' && high > 0 && <span className="rounded-full bg-warning/20 px-1.5 text-[11px] tabular-nums text-warning">{high}</span>}
          </button>
        ))}
      </div>

      <div role="tabpanel" aria-label={TABS.find((t) => t.id === tab)?.label}>
        <TabFade tab={tab}>
          {tab === 'advice' && <AdviceTab advice={advice} loading={!report.data && !report.error} onTab={select} ask={setPending} />}
          {tab === 'optimize' && <OptimizeTab ask={setPending} />}
          {tab === 'processes' && <ProcessesTab processes={processes.data} ask={setPending} />}
          {tab === 'services' && <ServicesTab services={services.data} ask={setPending} />}
          {tab === 'startup' && <StartupTab entries={report.data?.startup} />}
          {tab === 'cleanup' && <CleanupTab targets={report.data?.cleanup} ask={setPending} onRescan={() => void report.refetch()} scanning={report.isFetching} />}
          {tab === 'system' && <SystemTab report={report.data} ask={setPending} />}
        </TabFade>
      </div>

      {(processes.error || services.error || report.error) && (
        <p className="text-sm text-danger">The recorder could not be asked: {(processes.error ?? services.error ?? report.error)?.message}</p>
      )}
      <ConfirmAction pending={pending} onClose={() => setPending(null)} />
    </div>
  );
}
