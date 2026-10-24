import { Link } from '@tanstack/react-router';
import { motion, useReducedMotion } from 'motion/react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import type { AnomalyInfo, Severity } from '@/lib/api/types';
import { formatTime } from '@/lib/format';
import { useArrivals } from '@/lib/motion';
import { cn } from '@/lib/utils';

const SEVERITY_DOT: Record<Severity, string> = {
  Info: 'bg-muted',
  Low: 'bg-accent',
  Medium: 'bg-warning',
  High: 'bg-danger',
  Critical: 'bg-danger',
};

const SEVERITY_RANK: Record<Severity, number> = { Info: 0, Low: 1, Medium: 2, High: 3, Critical: 4 };

export interface AnomaliesCardProps {
  anomalies: AnomalyInfo[] | undefined;
  /** Whether the recorder is recording (the rules only look at recorded data). */
  recording: boolean;
  /** Only anomalies that are still going on. */
  runningOnly?: boolean;
  /** Lowest severity shown. */
  minimum?: Severity;
  title?: string;
  className?: string;
}

/** What anomalies can be grouped and sorted by. */
const ANOMALY_COLUMNS: Column<AnomalyInfo>[] = [
  { id: 'started', header: 'Started', value: (a) => a.startedAt, groupable: false, firstDesc: true },
  { id: 'state', header: 'State', value: (a) => (a.endedAt == null ? 'Going on' : 'Ended') },
  { id: 'severity', header: 'Severity', value: (a) => SEVERITY_RANK[a.severity], group: (a) => a.severity, firstDesc: true },
  { id: 'classification', header: 'What', value: (a) => a.classification },
  { id: 'device', header: 'Device', value: (a) => a.device },
  { id: 'duration', header: 'Duration', value: (a) => a.durationSeconds, groupable: false, firstDesc: true },
];

function duration(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const minutes = Math.floor(seconds / 60);
  return seconds % 60 >= 1 && minutes < 10 ? `${minutes} min ${Math.round(seconds % 60)} s` : `${minutes} min`;
}

/**
 * What the anomaly rules detected, with their reasoning in plain words (the normal range, the current value, how
 * long, and the rule). Running anomalies pulse; one that created an incident links to it.
 */
export function AnomaliesCard({ anomalies, recording, runningOnly = false, minimum = 'Info', title, className }: AnomaliesCardProps) {
  const reduced = useReducedMotion() ?? false;
  const shown = (anomalies ?? []).filter((a) => (!runningOnly || a.endedAt == null) && SEVERITY_RANK[a.severity] >= SEVERITY_RANK[minimum]);
  const isArrival = useArrivals(shown, (a) => String(a.id || `${a.ruleId}-${a.startedAt}`));
  const running = (anomalies ?? []).filter((a) => a.endedAt == null).length;

  return (
    // The card fills its widget and only the list scrolls (no second scrollbar around it).
    <Card className={cn('flex flex-col', className)}>
      <CardHeader>
        <CardTitle>{title ?? 'Anomalies'}</CardTitle>
        <span className={cn('text-xs', running > 0 ? 'font-medium text-warning' : 'text-muted')}>{running > 0 ? `${running} going on` : 'last 10 min'}</span>
      </CardHeader>
      <CardContent className="flex min-h-0 flex-1 flex-col">
        {shown.length === 0 ? (
          <p className="text-sm text-muted">
            {!recording && (anomalies ?? []).length === 0
              ? 'The anomaly rules watch recorded data, so they run while the recorder is recording.'
              : 'Nothing unusual in the last 10 minutes.'}{' '}
            <Link to="/settings" hash="triggers" className="text-accent hover:underline">
              Rules
            </Link>
          </p>
        ) : (
          <DataTable
            viewKey="dashboard.anomalies"
            label="Anomalies"
            rows={shown}
            columns={ANOMALY_COLUMNS}
            rowKey={(a) => a.id || `${a.ruleId}-${a.startedAt}`}
            defaultView={{ groupBy: [], sort: [{ id: 'started', desc: true }] }}
            className="min-h-0 flex-1"
            scrollClassName="flex-1 pr-1"
            compact
            renderRow={(a) => {
              const live = a.endedAt == null;
                return (
                  <motion.div
                    initial={reduced || !isArrival(a) ? false : { opacity: 0, y: -8, backgroundColor: 'rgba(245, 158, 11, 0.18)' }}
                    animate={{ opacity: 1, y: 0, backgroundColor: 'rgba(245, 158, 11, 0)' }}
                    transition={{ duration: 0.4, backgroundColor: { duration: 2.5 } }}
                    className={cn('mb-2 rounded-md border px-2.5 py-2', live ? 'border-warning/50' : 'border-border')}
                  >
                    <div className="flex items-start gap-2">
                      <span className="relative mt-1.5 flex size-2 shrink-0" aria-hidden>
                        {live && !reduced && <span className={cn('absolute inline-flex size-full animate-ping rounded-full opacity-70', SEVERITY_DOT[a.severity])} />}
                        <span className={cn('relative inline-flex size-2 rounded-full', SEVERITY_DOT[a.severity])} />
                      </span>
                      <div className="min-w-0 flex-1">
                        <p className="text-sm font-medium">
                          {a.classification}
                          {a.device && !a.series.includes(':') && <span className="font-normal text-muted"> · {a.device}</span>}
                        </p>
                        <p className="text-sm tabular-nums">{a.current}</p>
                        {a.baseline && <p className="text-xs text-muted">Normally {a.baseline}</p>}
                        <p className="mt-0.5 text-xs text-muted">
                          <time dateTime={new Date(a.startedAt).toISOString()}>{formatTime(new Date(a.startedAt).toISOString())}</time>
                          {live ? ` · going on for ${duration(a.durationSeconds)}` : a.durationSeconds >= 1 ? ` · lasted ${duration(a.durationSeconds)}` : ''} · {a.severity}
                        </p>
                        <p className="mt-0.5 text-xs text-muted" title="The rule that detected it">
                          Rule: {a.rule}
                        </p>
                        {a.incidentKey && (
                          <Link to="/incidents/$key" params={{ key: a.incidentKey }} className="mt-1 inline-block text-xs font-medium text-accent hover:underline">
                            Incident {a.incidentKey}
                          </Link>
                        )}
                      </div>
                    </div>
                  </motion.div>
                );
            }}
          />
        )}
      </CardContent>
    </Card>
  );
}
