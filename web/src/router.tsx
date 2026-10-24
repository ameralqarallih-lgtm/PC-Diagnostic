import { createRootRoute, createRoute, createRouter, lazyRouteComponent, type RouterHistory } from '@tanstack/react-router';
import { AppShell } from '@/components/layout/AppShell';
import { parseDiagnosticsSearch } from '@/features/diagnostics/search';
import { parseHistorySearch } from '@/features/history/ranges';
import { parseIncidentSearch } from '@/features/incidents/IncidentPage';
import { parseIncidentsSearch } from '@/features/incidents/IncidentsPage';
import { PhasePlaceholder } from '@/features/placeholder/PhasePlaceholder';
import { parseSystemSearch } from '@/features/system/search';
import { parseTuneUpSearch } from '@/features/tuneup/search';

export { HISTORY_RANGES, parseHistorySearch, type HistoryRange } from '@/features/history/ranges';

const rootRoute = createRootRoute({
  component: AppShell,
  notFoundComponent: () => (
    <PhasePlaceholder title="Page not found" phase="no phase" description="This address does not exist in PC Diagnostic." />
  ),
});

// Pages load on demand: the charting and layout libraries are only fetched when a page that uses them opens.
const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: lazyRouteComponent(() => import('@/features/dashboard/DashboardPage'), 'DashboardPage'),
});

const historyRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'history',
  validateSearch: parseHistorySearch,
  component: lazyRouteComponent(() => import('@/features/history/HistoryPage'), 'HistoryPage'),
});

const incidentsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'incidents',
  validateSearch: parseIncidentsSearch,
  component: lazyRouteComponent(() => import('@/features/incidents/IncidentsPage'), 'IncidentsPage'),
});

const incidentRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'incidents/$key',
  validateSearch: parseIncidentSearch,
  component: lazyRouteComponent(() => import('@/features/incidents/IncidentPage'), 'IncidentPage'),
});

const diagnosticsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'diagnostics',
  validateSearch: parseDiagnosticsSearch,
  component: lazyRouteComponent(() => import('@/features/diagnostics/DiagnosticsPage'), 'DiagnosticsPage'),
});

const systemRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'system',
  validateSearch: parseSystemSearch,
  component: lazyRouteComponent(() => import('@/features/system/SystemPage'), 'SystemPage'),
});

const tuneUpRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'tune-up',
  validateSearch: parseTuneUpSearch,
  component: lazyRouteComponent(() => import('@/features/tuneup/TuneUpPage'), 'TuneUpPage'),
});

const overlayRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'on-screen',
  component: lazyRouteComponent(() => import('@/features/overlay/OverlayPage'), 'OverlayPage'),
});

const driversRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'drivers',
  component: lazyRouteComponent(() => import('@/features/devices/DriversPage'), 'DriversPage'),
});

const charactersRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'characters',
  component: lazyRouteComponent(() => import('@/features/overlay/characters/CharactersPage'), 'CharactersPage'),
});

const generatorRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'generator',
  component: lazyRouteComponent(() => import('@/features/overlay/characters/GeneratorPage'), 'GeneratorPage'),
});

const storageRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'storage',
  component: lazyRouteComponent(() => import('@/features/storage/StoragePage'), 'StoragePage'),
});

const settingsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'settings',
  component: lazyRouteComponent(() => import('@/features/settings/SettingsPage'), 'SettingsPage'),
});

const routeTree = rootRoute.addChildren([dashboardRoute, historyRoute, incidentsRoute, incidentRoute, diagnosticsRoute, systemRoute, driversRoute, tuneUpRoute, overlayRoute, charactersRoute, generatorRoute, storageRoute, settingsRoute]);

/** Creates the router; tests pass a memory history. */
export function createAppRouter(history?: RouterHistory) {
  return createRouter({
    routeTree,
    history,
    defaultPreload: 'intent',
    scrollRestoration: true,
    // Going to another page plays the page transition (index.css); opening the app and a change on the same page (a
    // tab, a range) do not.
    defaultViewTransition: { types: ({ fromLocation, pathChanged }) => (fromLocation && pathChanged ? ['page'] : false) },
  });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
