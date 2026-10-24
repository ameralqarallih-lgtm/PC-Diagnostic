import type { TuneUpTab } from './recommend';

export interface TuneUpSearch {
  /** The tab shown (the advice when absent). */
  tab?: TuneUpTab;
}

const TABS: readonly string[] = ['optimize', 'processes', 'services', 'startup', 'cleanup', 'system'];

/** Validates ?tab= so a shared address always opens a tab that exists. */
export function parseTuneUpSearch(search: Record<string, unknown>): TuneUpSearch {
  return typeof search.tab === 'string' && TABS.includes(search.tab) ? { tab: search.tab as TuneUpTab } : {};
}
