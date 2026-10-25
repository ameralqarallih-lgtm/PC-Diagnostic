import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useToast } from '@/components/ui/toast';
import { apiGet, apiPost, apiRequest } from '@/lib/api/client';
import type { LocalStoreView, OverlayResponse, SettingsResponse, StorageCleanResult, StorageDeleteResult, StorageLocationRequest, StorageLocationsResponse, StorageResponse, StoredFile } from '@/lib/api/types';
import { formatBytes } from '@/lib/format';

/** What the data uses, the limits and the recorder's view (refreshed every 15 s while the page is open). */
export function useStorage() {
  return useQuery({ queryKey: ['storage'], queryFn: ({ signal }) => apiGet<StorageResponse>('/storage', signal), refetchInterval: 15_000 });
}

/**
 * Changes the space limit from the Storage page: the saved settings with only the limit changed, checked on the server
 * by the same rules as the Settings page. The recorder applies the new limit a few seconds later.
 */
export function useSetSpaceLimit() {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: async (dataLimitGb: number) => {
      const { settings } = await apiGet<SettingsResponse>('/settings');
      return apiRequest<SettingsResponse>('PUT', '/settings', { ...settings, storage: { ...settings.storage, dataLimitGb } });
    },
    onSuccess: (response, dataLimitGb) => {
      if (response) client.setQueryData(['settings'], response);
      toast.show({ title: dataLimitGb > 0 ? `Space limit set to ${dataLimitGb} GB` : 'Space limit removed', description: 'The recorder applies it within a few seconds.', tone: 'success' });
      void client.invalidateQueries({ queryKey: ['storage'] });
    },
    onError: (error) => toast.show({ title: 'The space limit was not changed', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
}

/** Applies the limits now and gives the recording database's free space back to the drive. */
export function useCleanStorage() {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: () => apiPost<StorageCleanResult>('/storage/clean', { compact: true }),
    onSuccess: (result) => {
      toast.show({ title: result.message, tone: 'success' });
      void client.invalidateQueries({ queryKey: ['storage'] });
    },
    onError: (error) => toast.show({ title: 'Not cleaned up', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
}

/** Deletes incidents (each by the recorder). */
export function useDeleteIncidents() {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: ({ keys }: { keys: string[]; bytes: number }) => apiPost<StorageDeleteResult>('/storage/incidents/delete', { keys }),
    onSuccess: (result, { bytes }) => {
      const count = result.deleted.length;
      toast.show({
        title: count === 1 ? '1 incident deleted' : `${count} incidents deleted`,
        description: result.problems.length > 0 ? result.problems.map((p) => `${p.key}: ${p.problem}`).join(' ') : `About ${formatBytes(bytes)} given back.`,
        tone: result.problems.length > 0 ? 'error' : 'success',
      });
      void client.invalidateQueries({ queryKey: ['storage'] });
      void client.invalidateQueries({ queryKey: ['incidents'] });
    },
    onError: (error) => toast.show({ title: 'Not deleted', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
}

/** Pins or unpins an incident (pinned incidents are never removed automatically). */
export function usePinIncident() {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: ({ key, pinned }: { key: string; pinned: boolean }) => apiRequest('PATCH', `/incidents/${key}`, { pinned }),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['storage'] });
      void client.invalidateQueries({ queryKey: ['incidents'] });
    },
    onError: (error) => toast.show({ title: 'Not changed', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
}

/** Where files are kept: the locations and the job bringing them up to date (every second while it runs). */
export function useFileLocations() {
  return useQuery({
    queryKey: ['storage-locations'],
    queryFn: ({ signal }) => apiGet<StorageLocationsResponse>('/storage/locations', signal),
    refetchInterval: (query) => (query.state.data?.job.running ? 1000 : 15_000),
  });
}

type LocationChange =
  | { action: 'add'; location: StorageLocationRequest }
  | { action: 'change'; id: string; keeps?: string[]; name?: string; accessKey?: string; secretKey?: string }
  | { action: 'remove'; id: string; force?: boolean }
  | { action: 'order'; ids: string[] }
  | { action: 'default'; id: string; move: boolean };

/** Adds, changes, removes or reorders a location (each answers with the locations as they are then). */
export function useChangeLocation() {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: (change: LocationChange) => {
      switch (change.action) {
        case 'add':
          return apiPost<StorageLocationsResponse>('/storage/locations', change.location);
        case 'change': {
          const { id, ...body } = change;
          return apiRequest<StorageLocationsResponse>('PATCH', `/storage/locations/${encodeURIComponent(id)}`, { ...body, action: undefined });
        }
        case 'remove':
          return apiRequest<StorageLocationsResponse>('DELETE', `/storage/locations/${encodeURIComponent(change.id)}${change.force ? '?force=true' : ''}`);
        case 'order':
          return apiPost<StorageLocationsResponse>('/storage/locations/order', { ids: change.ids });
        case 'default':
          return apiPost<StorageLocationsResponse>(`/storage/locations/${encodeURIComponent(change.id)}/default`, { move: change.move });
      }
    },
    onSuccess: (answer, change) => {
      if (answer) client.setQueryData(['storage-locations'], answer);
      void client.invalidateQueries({ queryKey: ['storage-locations'] });
      if (change.action === 'add') toast.show({ title: `${change.location.name} added`, description: 'The files it keeps are being copied there.', tone: 'success' });
      if (change.action === 'remove') toast.show({ title: 'Location removed', description: 'Its files were copied to the others first; the copies there stay.', tone: 'success' });
      if (change.action === 'default') toast.show({ title: 'Default changed', description: 'Files are kept there from now on; those kept so far are being copied there.', tone: 'success' });
      if (change.action === 'remove' || change.action === 'default') void client.invalidateQueries({ queryKey: ['storage-local'] });
    },
    onError: (error) => toast.show({ title: 'Not changed', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
}

/** PC Diagnostic Storage (SeaweedFS on this PC): how it is (every second while it is being set up). */
export function useLocalStore() {
  return useQuery({
    queryKey: ['storage-local'],
    queryFn: ({ signal }) => apiGet<LocalStoreView>('/storage/local', signal),
    refetchInterval: (query) => (query.state.data?.setup.running ? 1000 : 5000),
  });
}

type LocalAction = { action: 'setup'; folder?: string; makeDefault: boolean } | { action: 'start' } | { action: 'stop' };

/** Sets PC Diagnostic Storage up (its program downloaded once when it was not installed with PC Diagnostic), or starts or stops it. */
export function useLocalStoreAction() {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: (change: LocalAction) =>
      change.action === 'setup' ? apiPost<LocalStoreView>('/storage/local', { folder: change.folder || null, makeDefault: change.makeDefault }) : apiPost<LocalStoreView>(`/storage/local/${change.action}`),
    onSuccess: (answer) => {
      if (answer) client.setQueryData(['storage-local'], answer);
      void client.invalidateQueries({ queryKey: ['storage-local'] });
      void client.invalidateQueries({ queryKey: ['storage-locations'] });
    },
    onError: (error) => toast.show({ title: 'Not done', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
}

/** Tries a location before it is added (writes, reads and deletes a small file there): null when it works. */
export function useTestLocation() {
  return useMutation({ mutationFn: (location: StorageLocationRequest) => apiPost<{ problem: string | null }>('/storage/locations/test', location) });
}

/** Copies what is missing to every location now. */
export function useSyncLocations() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiPost('/storage/locations/sync'),
    onSuccess: () => void client.invalidateQueries({ queryKey: ['storage-locations'] }),
  });
}

/** The on-screen layout's backups (kept before saves, at most every 5 minutes, the newest 50). */
export function useLayoutBackups() {
  return useQuery({ queryKey: ['overlay-backups'], queryFn: ({ signal }) => apiGet<StoredFile[]>('/overlay/backups', signal), refetchInterval: 60_000 });
}

/** Brings a backup back: the recorder shows it at once, and the layout it replaces is kept too. */
export function useRestoreBackup() {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: (name: string) => apiPost<OverlayResponse>(`/overlay/backups/${encodeURIComponent(name)}/restore`),
    onSuccess: (answer) => {
      void client.invalidateQueries({ queryKey: ['overlay'] });
      void client.invalidateQueries({ queryKey: ['overlay-backups'] });
      toast.show({
        title: 'Layout brought back',
        description: answer.recorderProblem ? `Saved; the recorder shows it when it runs (${answer.recorderProblem}).` : 'The screens show it now. The layout it replaced was kept as a backup too.',
        tone: 'success',
      });
    },
    onError: (error) => toast.show({ title: 'Not brought back', description: error instanceof Error ? error.message : undefined, tone: 'error' }),
  });
}
