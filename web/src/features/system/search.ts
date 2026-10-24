/** The System page's tabs (the incident's System tab has the same, without the recorder's cost). */
export const SYSTEM_TABS = ['overview', 'hardware', 'motherboard', 'devices', 'drivers', 'sensors', 'readings'] as const;
export type SystemTab = (typeof SYSTEM_TABS)[number] | (string & {});

export interface SystemSearch {
  /** The tab shown (the overview when absent). */
  tab?: SystemTab;
}

const TABS: readonly string[] = [...SYSTEM_TABS, 'recorder'];

/** Validates ?tab= so a shared address always opens a tab that exists. */
export function parseSystemSearch(search: Record<string, unknown>): SystemSearch {
  return typeof search.tab === 'string' && TABS.includes(search.tab) && search.tab !== 'overview' ? { tab: search.tab } : {};
}
