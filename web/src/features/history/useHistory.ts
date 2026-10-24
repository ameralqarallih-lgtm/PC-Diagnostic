import { keepPreviousData, useQueries, useQuery } from '@tanstack/react-query';
import { apiGet } from '@/lib/api/client';
import type { HistoryResult, SeriesInfo, TimelineEntry } from '@/lib/api/types';

/** Where chart data comes from: the live history, or one incident's own copy. */
export type DataScope = { kind: 'live' } | { kind: 'incident'; key: string };

const base = (scope: DataScope) => (scope.kind === 'live' ? '/history' : `/incidents/${encodeURIComponent(scope.key)}`);

/** The series that can be charted, including every hardware sensor. */
export function useCatalog(scope: DataScope) {
  return useQuery({
    queryKey: ['catalog', scope],
    queryFn: ({ signal }) => apiGet<SeriesInfo[]>(`${base(scope)}/catalog?sensors=true`, signal),
    staleTime: 60_000,
  });
}

/** Series per request (the server's limit); more are requested in parallel and put together. */
const PER_REQUEST = 12;

export interface SeriesRequest {
  ids: string[];
  from?: number;
  to?: number;
  points?: number;
}

/** Series in min/average/max buckets (in parallel requests of up to 12 series, put together). */
export function useSeries(scope: DataScope, request: SeriesRequest, refetchIntervalMs?: number): { data: HistoryResult | undefined } {
  const chunks: string[][] = [];
  for (let i = 0; i < request.ids.length; i += PER_REQUEST) chunks.push(request.ids.slice(i, i + PER_REQUEST));
  const results = useQueries({
    queries: chunks.map((ids) => {
      const params = new URLSearchParams({ ids: ids.join(','), points: String(request.points ?? 800) });
      if (request.from != null) params.set('from', String(Math.round(request.from)));
      if (request.to != null) params.set('to', String(Math.round(request.to)));
      return {
        queryKey: ['series', scope, { ...request, ids }],
        queryFn: ({ signal }: { signal: AbortSignal }) => apiGet<HistoryResult>(`${base(scope)}/series?${params.toString()}`, signal),
        placeholderData: keepPreviousData,
        refetchInterval: refetchIntervalMs,
      };
    }),
  });
  const parts = results.map((r) => r.data).filter((d): d is HistoryResult => d !== undefined);
  if (parts.length === 0) return { data: undefined };
  const first = parts[0]!;
  return { data: { ...first, series: parts.flatMap((p) => p.series) } };
}

/** Events in a range (live history). */
export function useHistoryEvents(from: number, to: number, minImportance: number, refetchIntervalMs?: number) {
  return useQuery({
    queryKey: ['history-events', from, to, minImportance],
    queryFn: ({ signal }) => apiGet<TimelineEntry[]>(`/history/events?from=${Math.round(from)}&to=${Math.round(to)}&min=${minImportance}&limit=500`, signal),
    placeholderData: keepPreviousData,
    refetchInterval: refetchIntervalMs,
  });
}
