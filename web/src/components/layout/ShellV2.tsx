import { Link, Outlet, useNavigate } from '@tanstack/react-router';
import { Moon, Rows3, Rows4, Search } from 'lucide-react';
import { motion } from 'motion/react';
import { useEffect, useMemo, useState } from 'react';
import { Button } from '@/components/ui/button';
import { ReportingConsentBanner, ReportProblemButton } from '@/features/reports/ReportProblem';
import { applyDensity, loadDensity, useDensity } from '@/lib/look';
import { loadTheme, switchTheme } from '@/lib/theme';
import { cn } from '@/lib/utils';
import { Brand, HardwareBanner, NAV_ITEMS, RecorderBadge, StorageBanner, ThemeToggle, useLive } from './AppShell';
import { CommandPalette, useSettingsItems, type PaletteItem } from './CommandPalette';

type To = (typeof NAV_ITEMS)[number]['to'];

/** The four areas of the v2 sidebar and the pages in each (UI v2 plan, Navigation). */
export const AREAS: readonly { name: string; pages: readonly To[] }[] = [
  { name: 'Monitor', pages: ['/', '/incidents', '/history', '/diagnostics', '/tune-up'] },
  { name: 'On screen', pages: ['/on-screen', '/characters', '/generator'] },
  { name: 'Devices', pages: ['/system', '/drivers'] },
  { name: 'Settings', pages: ['/settings', '/storage'] },
];

/** v2 names where the plan renames a page. */
const V2_LABEL: Partial<Record<To, string>> = { '/on-screen': 'Screen editor', '/system': 'Hardware' };

const item = (to: To) => NAV_ITEMS.find((n) => n.to === to)!;

/**
 * The v2 shell: the sidebar in four areas (the page shown marked by a sliding fill), a header with search (Ctrl+K),
 * the recorder's state, problem reports, the theme and the density.
 */
export function ShellV2() {
  const live = useLive();
  const [palette, setPalette] = useState(false);
  const items = usePaletteItems();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setPalette((p) => !p);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  return (
    <div className="min-h-screen md:grid md:grid-cols-[15rem_1fr]">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-4 focus:z-50 focus:rounded-md focus:bg-surface focus:px-3 focus:py-2">
        Skip to content
      </a>

      <aside className="sticky top-0 hidden h-screen flex-col border-r border-border bg-surface md:flex">
        <Brand className="px-4 py-5" live={live} />
        <nav aria-label="Main" className="flex flex-1 flex-col gap-0.5 overflow-y-auto px-2 pb-4">
          {AREAS.map((area) => (
            <div key={area.name} role="group" aria-label={area.name} className="flex flex-col gap-0.5">
              <div className="px-3 pb-1 pt-4 text-xs font-medium uppercase tracking-[0.06em] text-muted">{area.name}</div>
              {area.pages.map((to) => (
                <SideLink key={to} to={to} />
              ))}
            </div>
          ))}
        </nav>
      </aside>

      <div className="flex min-w-0 flex-col">
        <header className="sticky top-0 z-20 flex h-14 items-center gap-3 border-b border-border bg-background/90 px-4 backdrop-blur">
          <Brand className="md:hidden" live={live} />
          <button
            type="button"
            onClick={() => setPalette(true)}
            className="hidden h-9 w-full max-w-sm items-center gap-2 rounded-md border border-border bg-surface-muted/60 px-3 text-sm text-muted transition-colors duration-[120ms] hover:border-accent/40 hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent md:flex"
          >
            <Search className="size-4" aria-hidden />
            <span className="flex-1 text-left">Search pages and settings</span>
            <kbd className="rounded border border-border px-1.5 font-mono text-[11px]">Ctrl K</kbd>
          </button>
          <div className="ml-auto flex items-center gap-2">
            <Button variant="ghost" size="icon" className="md:hidden" aria-label="Search" onClick={() => setPalette(true)}>
              <Search aria-hidden />
            </Button>
            <RecorderBadge />
            <ReportProblemButton />
            <ThemeToggle />
            <DensityToggle />
          </div>
        </header>

        <main id="main" className="mx-auto w-full max-w-[160rem] flex-1 px-4 pb-24 pt-4 md:pb-8 lg:px-6">
          <StorageBanner />
          <HardwareBanner />
          <ReportingConsentBanner />
          <Outlet />
        </main>
      </div>

      <nav aria-label="Main" className="fixed inset-x-0 bottom-0 z-30 grid grid-cols-8 border-t border-border bg-surface pb-[env(safe-area-inset-bottom)] md:hidden">
        {NAV_ITEMS.filter((n) => !n.sidebarOnly).map((n) => (
          <Link
            key={n.to}
            to={n.to}
            activeOptions={{ exact: n.to === '/' }}
            className="flex min-w-0 flex-col items-center gap-1 px-0.5 py-2 text-[10px] text-muted"
            activeProps={{ className: 'text-accent', 'aria-current': 'page' }}
          >
            <n.icon className="size-5" aria-hidden />
            <span className="max-w-full truncate">{n.short ?? n.label}</span>
          </Link>
        ))}
      </nav>

      <CommandPalette open={palette} onClose={() => setPalette(false)} items={items} />
    </div>
  );
}

/** A sidebar entry; the page shown has the fill, which slides from the entry left (Spatial motion). */
function SideLink({ to }: { to: To }) {
  const n = item(to);
  return (
    <Link
      to={to}
      activeOptions={{ exact: to === '/' }}
      className="group relative isolate flex items-center gap-3 rounded-md px-3 py-1.5 text-sm text-muted transition-colors duration-[120ms] hover:text-foreground"
      activeProps={{ className: 'text-foreground font-medium', 'aria-current': 'page' }}
    >
      {({ isActive }) => (
        <>
          <span aria-hidden className="absolute inset-0 -z-20 rounded-md transition-colors duration-[120ms] group-hover:bg-surface-muted/60" />
          {isActive && (
            <motion.span layoutId="nav-v2" aria-hidden className="absolute inset-0 -z-10 rounded-md bg-surface-muted shadow-[inset_2px_0_0_var(--accent)]" transition={{ type: 'spring', stiffness: 380, damping: 32 }} />
          )}
          <n.icon className={cn('size-4 shrink-0', isActive && 'text-accent')} aria-hidden />
          <span>{V2_LABEL[to] ?? n.label}</span>
        </>
      )}
    </Link>
  );
}

/** Everything the palette finds: the pages (with their area), the settings topics and a few actions. */
function usePaletteItems(): PaletteItem[] {
  const navigate = useNavigate();
  const settings = useSettingsItems();
  return useMemo(() => {
    const pages: PaletteItem[] = AREAS.flatMap((area) =>
      area.pages.map((to) => {
        const n = item(to);
        return { id: `page:${to}`, kind: 'Pages' as const, title: V2_LABEL[to] ?? n.label, where: area.name, keywords: n.label, icon: n.icon, run: () => void navigate({ to }) };
      }),
    );
    const actions: PaletteItem[] = [
      {
        id: 'action:theme',
        kind: 'Actions',
        title: 'Switch between dark and light',
        keywords: 'theme dark light mode colours',
        icon: Moon,
        run: () => switchTheme(loadTheme() === 'dark' ? 'light' : 'dark'),
      },
      {
        id: 'action:density',
        kind: 'Actions',
        title: 'Switch between comfortable and compact',
        keywords: 'density compact comfortable spacing smaller denser',
        icon: Rows4,
        run: () => applyDensity(loadDensity() === 'compact' ? 'comfortable' : 'compact'),
      },
    ];
    return [...pages, ...settings, ...actions];
  }, [navigate, settings]);
}

/** Comfortable or Compact (this browser). */
function DensityToggle() {
  const density = useDensity();
  const next = density === 'compact' ? 'comfortable' : 'compact';
  return (
    <Button variant="ghost" size="icon" aria-label={`Switch to ${next}`} title={`Density: ${density} (switch to ${next})`} aria-pressed={density === 'compact'} onClick={() => applyDensity(next)}>
      {density === 'compact' ? <Rows4 aria-hidden /> : <Rows3 aria-hidden />}
    </Button>
  );
}
