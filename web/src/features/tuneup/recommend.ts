import type { ControlProcess, ControlService, LiveSnapshot, TuneUpReport } from '@/lib/api/types';
import { formatBytes } from '@/lib/format';

/** Where a recommendation leads: a tab to look at, or an action to run (always confirmed first). */
export type RecommendationAction =
  | { kind: 'tab'; tab: TuneUpTab; label: string }
  | { kind: 'apply'; action: string; items?: string[]; value?: string; label: string; confirm: string };

export type TuneUpTab = 'advice' | 'optimize' | 'processes' | 'services' | 'startup' | 'cleanup' | 'system';

export interface Recommendation {
  id: string;
  /** How much it matters now. */
  level: 'high' | 'medium' | 'low';
  title: string;
  detail: string;
  action?: RecommendationAction;
}

const GB = 1024 ** 3;

const isOwn = (p: ControlProcess) => p.kind === 'app' || p.kind === 'background';

/**
 * Advice from what the PC is doing now: memory pressure and who holds it, programs that stopped responding, busy
 * background programs, WSL's virtual machine, full drives, what can be cleaned, hibernation on a desktop, the power
 * plan, Game Mode, crowded startup and background services. Each says why, and leads to the tab or action that
 * deals with it; nothing runs without the user confirming.
 */
export function recommend(report: TuneUpReport | undefined, processes: ControlProcess[] | undefined, services: ControlService[] | undefined, live: LiveSnapshot | null | undefined): Recommendation[] {
  const items: Recommendation[] = [];
  const procs = processes ?? [];

  // Memory.
  const used = live?.memory?.usedPercent.value;
  if (used != null && used >= 85) {
    const heavy = procs
      .filter(isOwn)
      .sort((a, b) => (b.workingSetBytes ?? 0) - (a.workingSetBytes ?? 0))
      .slice(0, 3);
    items.push({
      id: 'memory',
      level: used >= 93 ? 'high' : 'medium',
      title: `Memory is ${Math.round(used)}% in use`,
      detail: heavy.length
        ? `Windows starts moving memory to the disk, which slows everything down. The programs holding the most: ${heavy.map((p) => `${p.name} (${formatBytes(p.workingSetBytes)})`).join(', ')}. Close what you do not need.`
        : 'Windows starts moving memory to the disk, which slows everything down. Close programs you do not need.',
      action: { kind: 'tab', tab: 'processes', label: 'See the processes' },
    });
  }

  // WSL / Hyper-V virtual machine.
  const vm = report?.system;
  if (vm?.wslRunning && (vm.wslBytes ?? 0) >= 2 * GB) {
    items.push({
      id: 'wsl',
      level: used != null && used >= 85 ? 'high' : 'medium',
      title: `WSL holds ${formatBytes(vm.wslBytes)} of memory`,
      detail:
        'The Linux subsystem (and Docker Desktop, which runs on it) keeps memory it once used. Shutting it down gives it back; it starts again when needed. To limit it for good, set memory= in %UserProfile%\\.wslconfig.',
      action: { kind: 'apply', action: 'shutdown-wsl', label: 'Shut down WSL', confirm: 'Linux programs and Docker containers running in WSL stop. Unsaved work in them is lost.' },
    });
  }

  // Programs that stopped responding.
  const hung = procs.filter((p) => p.notResponding && p.protection !== 'Protected');
  if (hung.length > 0) {
    items.push({
      id: 'hung',
      level: 'high',
      title: `${hung.length === 1 ? `${hung[0]!.name} is` : `${hung.length} programs are`} not responding`,
      detail: `${hung.map((p) => p.name).join(', ')}: ${hung.length === 1 ? 'it' : 'they'} stopped answering Windows. Wait a moment, or end ${hung.length === 1 ? 'it' : 'them'} (unsaved work is lost).`,
      action: {
        kind: 'apply',
        action: 'end-processes',
        items: hung.map((p) => `${p.pid}:${p.startTicks}`),
        label: hung.length === 1 ? 'End it' : 'End them',
        confirm: `End ${hung.map((p) => p.name).join(', ')}? Unsaved work in ${hung.length === 1 ? 'it' : 'them'} is lost.`,
      },
    });
  }

  // Busy background programs.
  const busy = procs.filter((p) => p.kind === 'background' && ((p.cpuPercent ?? 0) >= 10 || (p.workingSetBytes ?? 0) >= 1.5 * GB));
  if (busy.length > 0) {
    items.push({
      id: 'busy-background',
      level: 'medium',
      title: `${busy.length} background program${busy.length === 1 ? '' : 's'} use a lot`,
      detail: busy
        .slice(0, 4)
        .map((p) => `${p.name}: ${p.cpuPercent != null ? `${p.cpuPercent.toFixed(1)}% CPU, ` : ''}${formatBytes(p.workingSetBytes)}`)
        .join('; '),
      action: { kind: 'tab', tab: 'processes', label: 'See the processes' },
    });
  }

  // Drives.
  for (const v of vm?.volumes ?? []) {
    const share = v.totalBytes > 0 ? v.freeBytes / v.totalBytes : 1;
    if (share < 0.15) {
      items.push({
        id: `volume-${v.name}`,
        level: share < 0.07 ? 'high' : 'medium',
        title: `${v.name} has only ${formatBytes(v.freeBytes)} free (${Math.round(share * 100)}%)`,
        detail: v.system
          ? 'Windows needs free space for updates, its page file and temporary files; below about 10 % it slows down and updates fail.'
          : 'Programs and games on this drive may fail to save or update.',
        action: { kind: 'tab', tab: 'cleanup', label: 'Clean up' },
      });
    }
  }

  // What can be cleaned.
  const cleanable = (report?.cleanup ?? []).filter((t) => t.recommended).reduce((sum, t) => sum + t.bytes, 0);
  if (cleanable >= GB) {
    items.push({
      id: 'cleanup',
      level: 'medium',
      title: `${formatBytes(cleanable)} of temporary files and caches can be removed`,
      detail: 'Leftovers that Windows and programs make again when they need them.',
      action: { kind: 'tab', tab: 'cleanup', label: 'Clean up' },
    });
  }

  // Hibernation on a desktop.
  if (vm && !vm.hasBattery && vm.hibernationEnabled && (vm.hibernationFileBytes ?? 0) >= 2 * GB) {
    items.push({
      id: 'hibernation',
      level: 'low',
      title: `Hibernation keeps ${formatBytes(vm.hibernationFileBytes)} reserved on the system drive`,
      detail: 'A desktop PC rarely hibernates. Turning it off frees the space; sleep still works, but Windows then starts fully each time (no Fast Startup), which also avoids problems Fast Startup can cause with drivers.',
      action: { kind: 'apply', action: 'hibernation', value: 'off', label: 'Turn off hibernation', confirm: 'Hibernation and Fast Startup turn off; hiberfil.sys is removed. You can turn it on again here.' },
    });
  }

  // Power plan.
  const plan = vm?.powerPlans.find((p) => p.active);
  const balanced = vm?.powerPlans.find((p) => /balanced|ausbalanciert|équilibré|equilibrado/i.test(p.name));
  if (plan && /saver|sparen|économie|ahorro/i.test(plan.name)) {
    items.push({
      id: 'power-plan',
      level: 'medium',
      title: `The ${plan.name} power plan slows the PC down`,
      detail: 'It holds the processor back to save energy. Balanced gives full speed when needed.',
      action: balanced ? { kind: 'apply', action: 'power-plan', value: balanced.id, label: 'Use Balanced', confirm: `Switch to the ${balanced.name} power plan?` } : { kind: 'tab', tab: 'system', label: 'Power plans' },
    });
  }

  // Game Mode.
  if (vm?.gameMode === false) {
    items.push({
      id: 'game-mode',
      level: 'low',
      title: 'Game Mode is off',
      detail: 'With it on, Windows gives a running game priority and holds back updates and notifications during play.',
      action: { kind: 'apply', action: 'game-mode', value: 'on', label: 'Turn on Game Mode', confirm: 'Turn on Game Mode?' },
    });
  }

  // Startup.
  const starting = (report?.startup ?? []).filter((s) => s.enabled && !/microsoft/i.test(s.publisher ?? ''));
  if (starting.length >= 6) {
    items.push({
      id: 'startup',
      level: 'medium',
      title: `${starting.length} programs start when you sign in`,
      detail: `Each one makes sign-in slower and keeps running in the background: ${starting.slice(0, 6).map((s) => s.name).join(', ')}${starting.length > 6 ? '…' : ''}.`,
      action: { kind: 'tab', tab: 'startup', label: 'Review startup' },
    });
  }

  // Background services from other publishers.
  const otherServices = (services ?? []).filter((s) => !s.windows && s.status === 'Running');
  if (otherServices.length >= 12) {
    items.push({
      id: 'services',
      level: 'low',
      title: `${otherServices.length} services from other publishers are running`,
      detail: 'Updaters and helpers of installed programs. Those you do not need can be set to start only when needed (Manual).',
      action: { kind: 'tab', tab: 'services', label: 'Review services' },
    });
  }

  // Standby memory that will not go (rare).
  const total = live?.memory?.totalBytes.value;
  if (vm?.standbyBytes != null && vm.freeBytes != null && total && vm.freeBytes / total < 0.02 && vm.standbyBytes / total > 0.5) {
    items.push({
      id: 'standby',
      level: 'low',
      title: `${formatBytes(vm.standbyBytes)} of memory is standby cache and only ${formatBytes(vm.freeBytes)} is free`,
      detail: 'Windows hands out standby memory whenever a program needs it, so this is normally fine. Empty it only if a game stutters right after starting.',
      action: { kind: 'apply', action: 'free-standby', label: 'Empty standby memory', confirm: 'Empty the standby memory? Programs read their files from disk again until the cache refills.' },
    });
  }

  const rank = { high: 0, medium: 1, low: 2 } as const;
  return items.sort((a, b) => rank[a.level] - rank[b.level]);
}
