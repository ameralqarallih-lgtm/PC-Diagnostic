import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGet, apiPost } from '@/lib/api/client';
import type { UpdateStatus } from '@/lib/api/types';

/** What is known about a newer version (the Web host asks once a day; this only reads its answer). */
export function useUpdates() {
  return useQuery({
    queryKey: ['updates'],
    queryFn: ({ signal }) => apiGet<UpdateStatus>('/updates', signal),
    refetchInterval: 30 * 60_000,
    staleTime: 60_000,
  });
}

/** Asks now (at most once a minute; a quicker second ask returns the last answer). */
export function useCheckUpdates() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => apiPost<UpdateStatus>('/updates/check', {}),
    onSuccess: (status) => queryClient.setQueryData(['updates'], status),
  });
}

const DISMISSED = 'pcdiag.update.dismissed';

/** The version whose notice the user put away (it comes back for the next one). */
export function dismissedVersion(): string | null {
  try {
    return localStorage.getItem(DISMISSED);
  } catch {
    return null;
  }
}

export function dismissVersion(version: string) {
  try {
    localStorage.setItem(DISMISSED, version);
  } catch {
    // Not remembered in this browser: it shows again next time.
  }
}
