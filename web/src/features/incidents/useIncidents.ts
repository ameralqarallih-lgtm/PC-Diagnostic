import { useQuery } from '@tanstack/react-query';
import { apiGet } from '@/lib/api/client';
import type { IncidentPage } from '@/lib/api/types';

/** A page of incidents, newest first. */
export function useIncidents(limit = 50, offset = 0, refetchIntervalMs?: number) {
  return useQuery({
    queryKey: ['incidents', { limit, offset }],
    queryFn: ({ signal }) => apiGet<IncidentPage>(`/incidents?limit=${limit}&offset=${offset}`, signal),
    refetchInterval: refetchIntervalMs,
  });
}
