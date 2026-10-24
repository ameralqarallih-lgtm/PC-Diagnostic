import { DEFAULT_SERIES, HISTORY_RANGES, MAX_SERIES, RANGE_MS, type HistorySearch } from '@/features/history/ranges';

/** Longest period (the server's limit). */
export const MAX_PERIOD_MS = 31 * 86_400_000;

export interface DiagnosticsSearch {
  /** A diagnostic job to show (running or finished). */
  job?: string;
  /** Period start (Unix ms): with `to`, starts a diagnostic of that period. */
  from?: number;
  /** Period end (Unix ms). */
  to?: number;
  /** Incidents to diagnose (comma-separated keys): starts a diagnostic of the period their recordings cover. */
  incidents?: string;
}

/** Most incidents one diagnostic covers (the server's limit). */
export const MAX_INCIDENTS = 20;

const time = (value: unknown): number | undefined => (typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.round(value) : undefined);

/** Validates ?job= (a generated job ID) and ?from=&to= (a period of at most 31 days); anything else shows the period chooser. */
export function parseDiagnosticsSearch(search: Record<string, unknown>): DiagnosticsSearch {
  if (typeof search.job === 'string' && /^\d{8}-\d{6}-[0-9a-f]{6}$/.test(search.job)) return { job: search.job };
  if (typeof search.incidents === 'string') {
    const keys = search.incidents.split(',').filter((k) => /^INC-\d{8}-\d{5}$/.test(k));
    if (keys.length > 0 && keys.length <= MAX_INCIDENTS) return { incidents: keys.join(',') };
  }
  const from = time(search.from);
  const to = time(search.to);
  return from !== undefined && to !== undefined && from < to && to - from <= MAX_PERIOD_MS ? { from, to } : {};
}

/** The History view of a span: the smallest window around it, centered, with these series and a picked moment. */
export function historyFor(from: number, to: number, seriesIds?: string[] | null, at?: number, now: number = Date.now()): HistorySearch {
  const span = Math.max(to - from, 60_000);
  const range = HISTORY_RANGES.find((r) => RANGE_MS[r] >= span * 1.2) ?? '30d';
  const end = Math.round(to + (RANGE_MS[range] - span) / 2);
  const search: HistorySearch = { range, series: (seriesIds?.length ? seriesIds.slice(0, MAX_SERIES) : DEFAULT_SERIES).join(',') };
  if (end < now - 5_000) search.end = end;
  if (at !== undefined) search.at = at;
  return search;
}
