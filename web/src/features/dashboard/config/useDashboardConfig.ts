import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGetOptional, apiSend } from '@/lib/api/client';
import { normalize } from './layout';
import { defaultDashboard } from './presets';
import type { DashboardConfig } from './types';

const KEY = ['dashboard'];

/** The saved dashboard (or the default), and saving it. Stored by the web host in the data folder. */
export function useDashboardConfig() {
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: KEY,
    queryFn: async ({ signal }) => normalize(await apiGetOptional<unknown>('/dashboard', signal), defaultDashboard()),
    staleTime: Number.POSITIVE_INFINITY,
    retry: 1,
  });

  const save = useMutation({
    mutationFn: (config: DashboardConfig) => apiSend('PUT', '/dashboard', config),
    onSuccess: (_result, config) => queryClient.setQueryData(KEY, config),
  });

  // Without the saved dashboard (e.g. the web host is restarting) the default still works.
  const config: DashboardConfig | null = query.data ?? (query.isError ? defaultDashboard() : null);
  return { config, save };
}
