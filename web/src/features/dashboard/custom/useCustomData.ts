import { useQueries, useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import type { LiveSeries } from '@/features/live/series';
import { apiGet } from '@/lib/api/client';
import type { HistoryResult, LiveSnapshot, SeriesInfo } from '@/lib/api/types';
import { buildSources, livePoints, liveReason, missingSource, trendPoints, type Source } from './sources';

/** Most series per history request (the web host's limit). */
const PER_REQUEST = 12;

/** The recorded catalog with every hardware sensor, shared by all widgets and the builder. */
export function useSourceCatalog() {
  return useQuery({
    queryKey: ['catalog', 'sources'],
    queryFn: ({ signal }) => apiGet<SeriesInfo[]>('/history/catalog?sensors=true', signal),
    staleTime: 60_000,
    retry: 1,
  });
}

/** Every value source on this PC (recorded and live). */
export function useSources(live: LiveSnapshot | null): { sources: Source[]; byId: Map<string, Source>; loading: boolean } {
  const catalog = useSourceCatalog();
  // Devices come and go rarely; the live snapshot only adds those the recording has not seen.
  const deviceKey = live ? `${live.gpus.map((g) => g.key).join()}|${live.disks.map((d) => d.name).join()}|${live.networks.map((n) => n.name).join()}` : '';
  const sources = useMemo(
    () => buildSources(catalog.data ?? [], live, Date.now()),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- rebuilt when the devices change, not every second
    [catalog.data, deviceKey],
  );
  const byId = useMemo(() => new Map(sources.map((s) => [s.id, s])), [sources]);
  return { sources, byId, loading: catalog.isLoading };
}

export interface SourceData {
  source: Source;
  /** [time, value] over the window, oldest first. */
  points: [number, number | null][];
  /** The value now: live, or the newest recorded one. */
  latest: number | null;
  min: number | null;
  max: number | null;
  avg: number | null;
  /** Why there is no value, in words. */
  missing: string | null;
}

/** Live values older than this are not current. */
const LIVE_STALE_MS = 5000;

/**
 * The values of a widget's sources over its window: the live value once a second where the live feed has it, the
 * recording for everything else and for the time before the page was opened. Recorded-only values (most hardware
 * sensors) refresh every few seconds while recording.
 */
export function useCustomData(ids: string[], byId: Map<string, Source>, windowMinutes: number, now: number, series: LiveSeries, recording: boolean): SourceData[] {
  const windowMs = windowMinutes * 60_000;
  const resolved = useMemo(() => ids.map((id) => byId.get(id) ?? missingSource(id)), [ids, byId]);
  const recorded = resolved.filter((s) => s.recordedId).map((s) => s.recordedId!);
  const chunks: string[][] = [];
  for (let i = 0; i < recorded.length; i += PER_REQUEST) chunks.push(recorded.slice(i, i + PER_REQUEST));
  // Without a live value the recording is the only source: ask again every few seconds; otherwise only the time
  // before the page was opened (and beyond the ten minutes the page keeps) comes from it.
  const recordedOnly = resolved.some((s) => s.recordedId && !s.liveId);
  const refetch = recordedOnly ? Math.max(3000, windowMs / 200) : windowMs > 10 * 60_000 ? 30_000 : false;
  const points = Math.min(600, Math.max(60, windowMs / 1000));

  const results = useQueries({
    queries: chunks.map((chunk) => ({
      queryKey: ['custom-series', chunk.join(','), windowMinutes],
      queryFn: ({ signal }: { signal: AbortSignal }) => {
        const to = Date.now();
        const params = new URLSearchParams({ ids: chunk.join(','), from: String(to - windowMs), to: String(to), points: String(points) });
        return apiGet<HistoryResult>(`/history/series?${params.toString()}`, signal);
      },
      refetchInterval: refetch,
      staleTime: refetch === false ? 5 * 60_000 : 0,
      retry: 1,
    })),
  });
  const recordedPoints = new Map<string, [number, number][]>();
  for (const result of results) {
    for (const s of result.data?.series ?? []) recordedPoints.set(s.id, s.points.map((p) => [p[0], p[2]] as [number, number]));
  }

  const from = now - windowMs;
  return resolved.map((source) => {
    const live = source.liveId ? livePoints(source.liveId, from, now) : [];
    // The dashboard's chart history covers the headline values from before this page opened.
    const trend = trendPoints(series, source.id, from, now).filter((p) => live.length === 0 || p[0] < live[0]![0]);
    const ownLive = [...trend, ...live];
    const firstLive = ownLive[0]?.[0] ?? Number.POSITIVE_INFINITY;
    const past = (source.recordedId ? (recordedPoints.get(source.recordedId) ?? []) : []).filter((p) => p[0] >= from && p[0] < firstLive - 1000);
    const merged: [number, number | null][] = [...past, ...ownLive];

    const present = merged.map((p) => p[1]).filter((v): v is number => v != null && Number.isFinite(v));
    const lastLive = live.at(-1);
    const lastRecorded = past.at(-1);
    let latest: number | null = null;
    if (lastLive && now - lastLive[0] <= LIVE_STALE_MS) latest = lastLive[1];
    else if (!source.liveId && lastRecorded && now - lastRecorded[0] <= Math.max(60_000, windowMs / 100)) latest = lastRecorded[1];

    let missing: string | null = null;
    if (latest == null) {
      if (source.group === 'Not found') missing = 'Not on this PC (any more)';
      else if (source.liveId && liveReason(source.liveId)) missing = liveReason(source.liveId);
      else if (!source.liveId && !recording) missing = 'Recorded only: shown while the recorder is recording';
      else if (!source.liveId) missing = 'Waiting for the recording';
      else missing = 'Waiting for a value';
    }

    return {
      source,
      points: merged,
      latest,
      min: present.length ? Math.min(...present) : null,
      max: present.length ? Math.max(...present) : null,
      avg: present.length ? present.reduce((a, b) => a + b, 0) / present.length : null,
      missing,
    };
  });
}
