import { Link } from '@tanstack/react-router';
import { Activity, Cpu, Database, Gauge, LayoutDashboard, MonitorSmartphone, Moon, Settings, PawPrint, PlugZap, Siren, Stethoscope, Sun, Wand2, type LucideIcon } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import { useState, type ReactNode } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { useStatus } from '@/features/status/useStatus';
import { ApiError } from '@/lib/api/client';
import type { StorageNotice as StorageNoticeData } from '@/lib/api/types';
import { EASE } from '@/lib/motion';
import { loadTheme, switchTheme, type Theme } from '@/lib/theme';
import { ShellV2 } from './ShellV2';
import { cn } from '@/lib/utils';

interface NavItem {
  to: '/' | '/history' | '/incidents' | '/diagnostics' | '/system' | '/drivers' | '/tune-up' | '/on-screen' | '/characters' | '/generator' | '/storage' | '/settings';
  label: string;
  /** Label in the phone's bottom navigation, when shorter. */
  short?: string;
  icon: LucideIcon;
  /** Left out of the phone's bottom navigation (reached from Settings and the storage notice). */
  sidebarOnly?: boolean;
}

export const NAV_ITEMS: readonly NavItem[] = [
  { to: '/', label: 'Dashboard', short: 'Home', icon: LayoutDashboard },
  { to: '/history', label: 'History', icon: Activity },
  { to: '/incidents', label: 'Incidents', icon: Siren },
  { to: '/diagnostics', label: 'Diagnostics', short: 'Diagnose', icon: Stethoscope },
  { to: '/system', label: 'System', icon: Cpu },
  { to: '/drivers', label: 'Drivers', icon: PlugZap, sidebarOnly: true },
  { to: '/tune-up', label: 'Tune-up', icon: Gauge },
  { to: '/on-screen', label: 'On-screen', short: 'Screens', icon: MonitorSmartphone },
  { to: '/characters', label: 'Characters', icon: PawPrint, sidebarOnly: true },
  { to: '/generator', label: 'Generator', icon: Wand2, sidebarOnly: true },
  { to: '/storage', label: 'Storage', icon: Database, sidebarOnly: true },
  { to: '/settings', label: 'Settings', icon: Settings },
];

/** The app's shell (sidebar in four areas, header with search; a bottom bar on phones): `ShellV2`. */
export function AppShell() {
  return <ShellV2 />;
}

/**
 * A notice at the top of every page. One that appears while the app is open slides open (and closes the same way);
 * one there as the app opens is simply shown, so opening the app does not move the page.
 */
function Notice({ show, children }: { show: boolean; children: ReactNode }) {
  const settled = typeof performance !== 'undefined' && performance.now() > 2500;
  return (
    <AnimatePresence initial={false}>
      {show && (
        <motion.div
          className="overflow-hidden"
          initial={settled ? { opacity: 0, height: 0 } : false}
          animate={{ opacity: 1, height: 'auto' }}
          exit={{ opacity: 0, height: 0 }}
          transition={{ duration: 0.32, ease: EASE.out }}
        >
          {children}
        </motion.div>
      )}
    </AnimatePresence>
  );
}

/** Says so on every page when the drive is almost full (nothing is recorded), getting full, or the space limit is exceeded. */
export function StorageBanner() {
  const { data } = useStatus();
  const notice = data?.agent.status?.storage;
  const show = notice != null && notice.condition !== 'Normal';
  return <Notice show={show}>{show && <StorageNotice notice={notice} />}</Notice>;
}

function StorageNotice({ notice }: { notice: StorageNoticeData }) {
  const paused = notice.condition === 'WritesPaused';
  return (
    <div
      role={paused ? 'alert' : 'status'}
      className={`mb-4 flex flex-wrap items-center justify-between gap-2 rounded-md border px-3 py-2 text-sm ${paused ? 'border-danger/50 bg-danger/10' : 'border-warning/50 bg-warning/10'}`}
    >
      <span>
        <strong>{paused ? 'Not recording to disk: ' : notice.condition === 'LowSpace' ? 'The drive is getting full: ' : 'Over the space limit: '}</strong>
        {notice.message}
      </span>
      <Link to="/storage" className="shrink-0 text-accent hover:underline">
        Storage
      </Link>
    </div>
  );
}

/** Says so on every page when hardware the user expects is missing (a drive, a monitor, a device…). */
export function HardwareBanner() {
  const { data } = useStatus();
  const notice = data?.agent.status?.hardware;
  return (
    <Notice show={notice != null && notice.missing > 0}>
      {notice && (
        <div role="status" className="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-md border border-warning/50 bg-warning/10 px-3 py-2 text-sm">
          <span>
            <strong>Expected hardware missing: </strong>
            {notice.message}
          </span>
          <Link to="/system" search={{ tab: 'devices' }} className="shrink-0 text-accent hover:underline">
            Devices
          </Link>
        </div>
      )}
    </Notice>
  );
}

/** Whether the recorder is recording now (the brand's heartbeat runs). */
export function useLive(): boolean {
  const { data } = useStatus();
  const state = data?.agent.connected ? data.agent.status?.state : undefined;
  return state === 'Recording' || state === 'Starting';
}

export function Brand({ className, live }: { className?: string; live: boolean }) {
  return (
    <div className={cn('flex items-center gap-2', className)}>
      <svg viewBox="0 0 34 16" className="brand-beat h-4 w-[2.125rem] shrink-0 text-accent" aria-hidden>
        <path d="M1 9h8l2.2-5 3.4 10 3.4-12 2.6 9 1.8-2H33" pathLength={1} fill="none" stroke="currentColor" strokeWidth={1.8} strokeLinecap="round" strokeLinejoin="round" />
        {live && (
          <path
            className="brand-pulse"
            d="M1 9h8l2.2-5 3.4 10 3.4-12 2.6 9 1.8-2H33"
            pathLength={1}
            fill="none"
            stroke="currentColor"
            strokeWidth={2.6}
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        )}
      </svg>
      <span>
        <span className="text-base font-semibold">PC Diagnostic</span>
        <span className="ml-1 hidden text-xs text-muted lg:inline">Incident Recorder</span>
      </span>
    </div>
  );
}

/** A status light; a live one sends out a soft ring now and then. */
function LiveDot({ tone, live }: { tone: 'success' | 'danger' | 'warning' | 'neutral'; live?: boolean }) {
  const color = { success: 'bg-success', danger: 'bg-danger', warning: 'bg-warning', neutral: 'bg-muted' }[tone];
  return (
    <span aria-hidden className="relative flex size-2">
      {live && <span className={cn('absolute inline-flex size-full rounded-full opacity-60 motion-safe:animate-ping', color)} />}
      <span className={cn('relative inline-flex size-2 rounded-full', color)} />
    </span>
  );
}

export function RecorderBadge() {
  const { data, error } = useStatus();

  if (error instanceof ApiError && error.isUnauthorized) {
    return <Badge tone="warning">Session expired</Badge>;
  }
  if (error) {
    return <Badge tone="danger">Web host unreachable</Badge>;
  }
  if (!data) {
    return <Badge>Connecting…</Badge>;
  }
  const status = data.agent.status;
  if (!data.agent.connected || !status) {
    return <Badge title={data.agent.detail ?? undefined}>Recorder not connected</Badge>;
  }
  if (status.activeIncident) {
    return (
      <Badge tone="danger">
        <LiveDot tone="danger" live />
        Recording {status.activeIncident}
      </Badge>
    );
  }
  switch (status.state) {
    case 'Recording':
    case 'Starting':
      return (
        <Badge tone="success">
          <LiveDot tone="success" live />
          Recording
        </Badge>
      );
    case 'Paused':
      return (
        <Badge tone="warning">
          <LiveDot tone="warning" />
          Paused
        </Badge>
      );
    default:
      return <Badge>Not recording</Badge>;
  }
}

export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(loadTheme);
  const next: Theme = theme === 'dark' ? 'light' : 'dark';

  return (
    <Button
      variant="ghost"
      size="icon"
      aria-label={`Switch to ${next} theme`}
      className="overflow-hidden"
      onClick={(e) => {
        // The new theme grows from the toggle.
        const box = e.currentTarget.getBoundingClientRect();
        switchTheme(next, { x: box.left + box.width / 2, y: box.top + box.height / 2 });
        setTheme(next);
      }}
    >
      <AnimatePresence mode="popLayout" initial={false}>
        <motion.span
          key={theme}
          className="flex"
          initial={{ opacity: 0, rotate: -90, scale: 0.4 }}
          animate={{ opacity: 1, rotate: 0, scale: 1 }}
          exit={{ opacity: 0, rotate: 90, scale: 0.4 }}
          transition={{ duration: 0.35, ease: EASE.out }}
        >
          {theme === 'dark' ? <Sun aria-hidden /> : <Moon aria-hidden />}
        </motion.span>
      </AnimatePresence>
    </Button>
  );
}

