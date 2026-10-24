import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

export interface MetricRowProps {
  label: string;
  value: { text: string; title?: string };
  detail?: ReactNode;
  /** A bar under the value, in the style of its reading. */
  meter?: ReactNode;
}

/** One labelled value, with an optional bar and detail line. Missing values say why on hover. */
export function MetricRow({ label, value, detail, meter }: MetricRowProps) {
  const missing = value.title !== undefined;
  return (
    <div className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-3 gap-y-1">
      <dt className="text-sm">{label}</dt>
      <dd className={cn('text-right text-sm tabular-nums', missing ? 'text-unavailable' : 'font-medium')} title={value.title}>
        {value.text}
      </dd>
      {meter && <dd className="col-span-2 -mt-0.5">{meter}</dd>}
      {detail && <dd className="col-span-2 -mt-1 truncate text-xs text-muted">{detail}</dd>}
    </div>
  );
}
