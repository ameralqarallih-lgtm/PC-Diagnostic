export const HISTORY_RANGES = ['1m', '5m', '15m', '1h', '6h', '24h', '7d', '30d'] as const;
export type HistoryRange = (typeof HISTORY_RANGES)[number];

export const RANGE_MS: Record<HistoryRange, number> = {
  '1m': 60_000,
  '5m': 300_000,
  '15m': 900_000,
  '1h': 3_600_000,
  '6h': 21_600_000,
  '24h': 86_400_000,
  '7d': 604_800_000,
  '30d': 2_592_000_000,
};

export const DEFAULT_SERIES = ['cpu', 'memory', 'gpu', 'vram', 'disk'];

export interface HistorySearch {
  range: HistoryRange;
  /** Comma-separated series ids. */
  series: string;
  /** End of the window (Unix ms); absent means "now" (live). */
  end?: number;
  /** The zoomed part of the chart (Unix ms), when zoomed in. */
  zoomFrom?: number;
  zoomTo?: number;
  /** Shade each bucket's minimum–maximum range. */
  band?: boolean;
  /** Least important events listed (1 all, 2 warnings, 3 serious); 2 when absent. */
  events?: 1 | 3;
  /** The moment picked on the chart (Unix ms): its values and processes are shown below it. */
  at?: number;
}

const time = (value: unknown): number | undefined => (typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.round(value) : undefined);

/**
 * Validates ?range=&series=&end=&zoomFrom=&zoomTo=&band=&events=&at= so the view in the URL is always well-formed:
 * a reload or a shared link shows the same window, zoom, band, event filter and picked moment.
 */
/** Most series overlaid at once (requested from the server 12 at a time). */
export const MAX_SERIES = 30;

export function parseHistorySearch(search: Record<string, unknown>): HistorySearch {
  const range = HISTORY_RANGES.find((r) => r === search.range) ?? '1h';
  const series =
    typeof search.series === 'string'
      ? search.series
          .split(',')
          .filter((id) => /^[a-z0-9.]{1,40}$/.test(id))
          .slice(0, MAX_SERIES)
          .join(',')
      : DEFAULT_SERIES.join(',');
  const result: HistorySearch = { range, series };
  const end = time(search.end);
  if (end !== undefined) result.end = end;
  const zoomFrom = time(search.zoomFrom);
  const zoomTo = time(search.zoomTo);
  if (zoomFrom !== undefined && zoomTo !== undefined && zoomFrom < zoomTo) {
    result.zoomFrom = zoomFrom;
    result.zoomTo = zoomTo;
  }
  if (search.band === true) result.band = true;
  if (search.events === 1 || search.events === 3) result.events = search.events;
  const at = time(search.at);
  if (at !== undefined) result.at = at;
  return result;
}

/** Refresh interval for a live window of this length. */
export function refreshFor(range: HistoryRange): number | undefined {
  const ms = RANGE_MS[range];
  return ms <= 3_600_000 ? 5_000 : ms <= 86_400_000 ? 60_000 : undefined;
}
