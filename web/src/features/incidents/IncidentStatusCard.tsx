import { Link } from '@tanstack/react-router';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import type { AgentStatus, IncidentState, IncidentSummary } from '@/lib/api/types';
import { formatDateTime } from '@/lib/format';
import { useIncidents } from './useIncidents';

export const STATE_TONE: Record<IncidentState, 'success' | 'warning' | 'danger' | 'neutral'> = {
  Recording: 'warning',
  Finalizing: 'warning',
  Analyzing: 'warning',
  Complete: 'success',
  Partial: 'warning',
  Failed: 'danger',
};

const COLUMNS: Column<IncidentSummary>[] = [
  { id: 'title', header: 'Title', value: (i) => i.title, groupable: false },
  { id: 'state', header: 'State', value: (i) => i.state },
  { id: 'source', header: 'Source', value: (i) => i.source },
  { id: 'time', header: 'Time', value: (i) => Date.parse(i.centerAt), groupable: false, firstDesc: true },
];

/** The incident being recorded (if any) and the most recent incidents. */
export function IncidentStatusCard({ status, count = 5, title, className }: { status: AgentStatus | null; count?: number; title?: string; className?: string }) {
  const { data, error } = useIncidents(count, 0, 15_000);

  return (
    <Card className={className}>
      <CardHeader>
        <CardTitle>{title ?? 'Incidents'}</CardTitle>
        {data && (
          <Link to="/incidents" className="text-xs text-accent hover:underline">
            All {data.total}
          </Link>
        )}
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {status?.activeIncident && (
          <p className="text-sm">
            Recording{' '}
            <Link to="/incidents/$key" params={{ key: status.activeIncident }} className="font-mono text-accent hover:underline">
              {status.activeIncident}
            </Link>
          </p>
        )}
        {error ? (
          <p className="text-sm text-danger">The incident list could not be loaded.</p>
        ) : !data ? (
          <p className="text-sm text-muted">Loading…</p>
        ) : data.items.length === 0 ? (
          <p className="text-sm text-muted">No incidents yet. Press CAPTURE INCIDENT when something goes wrong.</p>
        ) : (
          <DataTable
            viewKey="widget.incidents"
            label={title ?? 'Incidents'}
            rows={data.items}
            columns={COLUMNS}
            rowKey={(incident) => incident.key}
            defaultView={{ groupBy: [], sort: [{ id: 'time', desc: true }] }}
            compact
            listClassName="gap-2"
            renderRow={(incident) => (
              <div className="flex flex-col gap-0.5">
                <div className="flex items-center justify-between gap-2">
                  <Link
                    to="/incidents/$key"
                    params={{ key: incident.key }}
                    className="truncate text-sm font-medium hover:underline"
                    title={incident.title}
                  >
                    {incident.title}
                  </Link>
                  <Badge tone={STATE_TONE[incident.state]}>{incident.state}</Badge>
                </div>
                <p className="text-xs text-muted">
                  <span className="font-mono">{incident.key}</span> · {formatDateTime(incident.centerAt)}
                  {incident.snapshotOnly ? ' · no data from before' : ''}
                </p>
              </div>
            )}
          />
        )}
      </CardContent>
    </Card>
  );
}
