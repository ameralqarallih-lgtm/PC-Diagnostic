import { useQuery } from '@tanstack/react-query';
import { apiGet } from '@/lib/api/client';
import type { StatusResponse } from '@/lib/api/types';

/** Web host and Agent status, polled every 5 s (the dashboard also gets status once a second from the live feed). */
export function useStatus() {
  return useQuery({
    queryKey: ['status'],
    queryFn: ({ signal }) => apiGet<StatusResponse>('/status', signal),
    refetchInterval: 5000,
  });
}
