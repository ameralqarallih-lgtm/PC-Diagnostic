import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useToast } from '@/components/ui/toast';
import { apiGet, apiPost } from '@/lib/api/client';
import type { ControlProcess, ControlResult, ControlService, LiveMessage, TuneUpReport } from '@/lib/api/types';
import { formatBytes } from '@/lib/format';
import { isSafeMode } from './safeMode';

/** Running processes (every few seconds while shown). */
export function useControlProcesses(enabled = true) {
  return useQuery({
    queryKey: ['control', 'processes'],
    queryFn: ({ signal }) => apiGet<ControlProcess[]>('/control/processes', signal),
    refetchInterval: 3000,
    enabled,
  });
}

/** Windows services. */
export function useControlServices(enabled = true) {
  return useQuery({
    queryKey: ['control', 'services'],
    queryFn: ({ signal }) => apiGet<ControlService[]>('/control/services', signal),
    refetchInterval: 10_000,
    enabled,
  });
}

/** What can be cleaned and the performance settings. */
export function useTuneUpReport(enabled = true) {
  return useQuery({
    queryKey: ['tuneup'],
    queryFn: ({ signal }) => apiGet<TuneUpReport>('/tuneup', signal),
    staleTime: 60_000,
    enabled,
  });
}

/** Memory and the rest of the live picture, for the advice (every few seconds). */
export function useLiveOnce() {
  return useQuery({
    queryKey: ['live-once'],
    queryFn: ({ signal }) => apiGet<LiveMessage>('/live', signal),
    refetchInterval: 5000,
  });
}

/** A short line for what an action freed. */
function freed(result: ControlResult): string | undefined {
  const parts = [
    result.freedBytes ? `${formatBytes(result.freedBytes)} freed.` : null,
    result.restartNeeded ? 'Restart Windows for it to take effect.' : null,
    result.skipped?.length ? `Safe mode left ${result.skipped.slice(0, 3).join('; ')}${result.skipped.length > 3 ? ` and ${result.skipped.length - 3} more` : ''}.` : null,
    result.problems?.slice(0, 3).join(' '),
  ].filter(Boolean);
  return parts.length ? parts.join(' ') : undefined;
}

/**
 * Runs a process, service or tune-up action, shows what happened and refreshes the lists. The recorder checks
 * every action again; the confirmation in the page is for the user.
 */
export function useControlAction() {
  const queryClient = useQueryClient();
  const toast = useToast();
  return useMutation({
    // Every action carries safe mode as it is when it is sent.
    mutationFn: ({ path, body }: { path: string; body?: Record<string, unknown>; label?: string }) => apiPost<ControlResult>(path, { ...(body ?? {}), safe: isSafeMode() }),
    onSuccess: (result) => {
      if (result) {
        toast.show({ title: result.message, description: freed(result), tone: result.ok ? 'success' : 'error' });
      }

      void queryClient.invalidateQueries({ queryKey: ['control'] });
      void queryClient.invalidateQueries({ queryKey: ['tuneup'] });
    },
    onError: (error, variables) =>
      toast.show({ title: `${variables.label ?? 'The action'} did not work`, description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
}
