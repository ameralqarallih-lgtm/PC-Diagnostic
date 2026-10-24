import { motion, useReducedMotion } from 'motion/react';
import { useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import type { LiveEvent, Severity } from '@/lib/api/types';
import { formatTime } from '@/lib/format';
import { useArrivals } from '@/lib/motion';
import { cn } from '@/lib/utils';
import { Segmented } from './LiveChartCard';

const SEVERITY_DOT: Record<Severity, string> = {
  Info: 'bg-muted',
  Low: 'bg-accent',
  Medium: 'bg-warning',
  High: 'bg-danger',
  Critical: 'bg-danger ring-2 ring-danger/40',
};

const SEVERITY_RANK: Record<Severity, number> = { Info: 0, Low: 1, Medium: 2, High: 3, Critical: 4 };

const SOURCE_TEXT: Record<string, string> = {
  windows: 'Windows event',
  recorder: 'Recorder',
  display: 'Display',
  process: 'Process',
  service: 'Service',
};

const FILTERS = [
  { value: '0', label: 'All' },
  { value: '2', label: 'Warnings' },
  { value: '3', label: 'Serious' },
];

/** What the events can be grouped and sorted by. */
const EVENT_COLUMNS: Column<LiveEvent>[] = [
  { id: 'time', header: 'Time', value: (e) => Date.parse(e.timestamp), groupable: false, firstDesc: true },
  { id: 'source', header: 'Source', value: (e) => SOURCE_TEXT[e.source] ?? e.source },
  { id: 'severity', header: 'Severity', value: (e) => SEVERITY_RANK[e.severity], group: (e) => e.severity, firstDesc: true },
  { id: 'category', header: 'Category', value: (e) => e.category || null },
  { id: 'title', header: 'Title', value: (e) => e.title, groupable: false },
];

export interface RecentEventsCardProps {
  events: LiveEvent[] | undefined;
  initialMinimum?: string;
  /** Sources included (all when omitted). */
  sources?: string[];
  title?: string;
  className?: string;
}

/** Notable events since the recorder started; new ones slide in and flash. Filter by severity. */
export function RecentEventsCard({ events, initialMinimum = '0', sources, title, className }: RecentEventsCardProps) {
  const [minimum, setMinimum] = useState(initialMinimum);
  const reduced = useReducedMotion() ?? false;
  const shown = (events ?? []).filter((e) => SEVERITY_RANK[e.severity] >= Number(minimum) && (!sources || sources.includes(e.source)));
  const isArrival = useArrivals(shown, (e) => `${e.timestamp}-${e.title}`);

  return (
    // The card fills its widget and only the list scrolls (no second scrollbar around it).
    <Card className={cn('flex flex-col', className)}>
      <CardHeader className="flex-wrap">
        <CardTitle>{title ?? 'Recent events'}</CardTitle>
        <Segmented label="Show events" options={FILTERS} value={minimum} onChange={setMinimum} />
      </CardHeader>
      <CardContent className="flex min-h-0 flex-1 flex-col">
        {shown.length === 0 ? (
          <p className="text-sm text-muted">
            {events && events.length > 0 ? 'No events at this severity.' : 'No notable events since the recorder started.'}
          </p>
        ) : (
          <DataTable
            viewKey="dashboard.events"
            label="Events"
            rows={shown}
            columns={EVENT_COLUMNS}
            rowKey={(e) => `${e.timestamp}-${e.title}`}
            defaultView={{ groupBy: [], sort: [{ id: 'time', desc: true }] }}
            className="min-h-0 flex-1"
            scrollClassName="flex-1 pr-1"
            listClassName="gap-1"
            compact
            renderRow={(e) => (
              <motion.div
                initial={reduced || !isArrival(e) ? false : { opacity: 0, x: -16, backgroundColor: 'rgba(56, 189, 248, 0.18)' }}
                animate={{ opacity: 1, x: 0, backgroundColor: 'rgba(56, 189, 248, 0)' }}
                transition={{ duration: 0.45, backgroundColor: { duration: 2.2 } }}
                className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-x-2.5 rounded-md px-1.5 py-1"
              >
                <span className={cn('mt-1.5 size-2 rounded-full', SEVERITY_DOT[e.severity], e.severity === 'Critical' && !reduced && 'animate-pulse')} aria-hidden />
                <div className="min-w-0">
                  <p className="truncate text-sm" title={e.title}>
                    {e.title}
                  </p>
                  <p className="text-xs text-muted">
                    <time dateTime={e.timestamp}>{formatTime(e.timestamp)}</time> · {SOURCE_TEXT[e.source] ?? e.source} · {e.severity}
                  </p>
                </div>
              </motion.div>
            )}
          />
        )}
      </CardContent>
    </Card>
  );
}
