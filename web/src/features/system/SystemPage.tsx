import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { apiGet } from '@/lib/api/client';
import type { SystemOverview } from '@/lib/api/types';
import { RecorderOverheadCard } from './RecorderOverheadCard';
import { SystemOverviewView } from './SystemOverviewView';

/** The PC as the recorder sees it now, by topic: overview, hardware, motherboard, devices, drivers, sensors, readings, recorder cost. */
export function SystemPage() {
  const search = useSearch({ from: '/system' });
  const navigate = useNavigate({ from: '/system' });
  const { data, error } = useQuery({
    queryKey: ['system'],
    queryFn: ({ signal }) => apiGet<SystemOverview>('/system', signal),
    refetchInterval: 60_000,
  });

  return (
    <div className="flex flex-col gap-4">
      <div>
        <h1 className="text-xl font-semibold">System</h1>
        <p className="text-sm text-muted">This PC’s hardware, drivers and sensors, what can be measured on it, and what recording costs.</p>
      </div>
      {error ? (
        <p className="text-sm text-danger">The system information could not be loaded.</p>
      ) : !data ? (
        <p className="text-sm text-muted">Loading…</p>
      ) : (
        <SystemOverviewView
          overview={data}
          tab={search.tab ?? 'overview'}
          onTab={(tab) => void navigate({ search: tab === 'overview' ? {} : { tab }, replace: true })}
          extraTabs={[{ id: 'recorder', label: 'Recorder cost', content: <RecorderOverheadCard /> }]}
          live
        />
      )}
    </div>
  );
}
