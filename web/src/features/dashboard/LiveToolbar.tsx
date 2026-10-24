import { Pause, Play } from 'lucide-react';
import { Button } from '@/components/ui/button';
import type { LiveConnection } from '@/features/live/useLiveFeed';
import { formatTime } from '@/lib/format';
import { cn } from '@/lib/utils';

export type FeedMode = 'recording' | 'liveView' | 'stale' | 'offline';

export interface LiveToolbarProps {
  connection: LiveConnection;
  mode: FeedMode;
  /** Seconds since the last update arrived. */
  ageSeconds: number | null;
  frozenAt: number | null;
  onToggleFreeze: () => void;
}

const MODE_TEXT: Record<FeedMode, string> = {
  recording: 'Live · recording',
  liveView: 'Live view · not recording',
  stale: 'Waiting for data',
  offline: 'Recorder offline',
};

/** Live status (with a pulsing dot while data flows), freshness, and freeze/resume of the whole dashboard. */
export function LiveToolbar({ connection, mode, ageSeconds, frozenAt, onToggleFreeze }: LiveToolbarProps) {
  const frozen = frozenAt != null;
  const flowing = connection === 'open' && (mode === 'recording' || mode === 'liveView') && !frozen;
  const tone = frozen ? 'bg-warning' : flowing ? (mode === 'recording' ? 'bg-success' : 'bg-accent') : 'bg-unavailable';
  const text = frozen
    ? `Frozen at ${formatTime(new Date(frozenAt).toISOString())}`
    : connection === 'reconnecting'
      ? 'Reconnecting…'
      : connection === 'connecting'
        ? 'Connecting…'
        : MODE_TEXT[mode];

  return (
    <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-surface px-3 py-2">
      <div className="flex items-center gap-2.5" role="status" aria-live="polite">
        <span className="relative flex size-2.5">
          {flowing && <span className={cn('absolute inline-flex size-full animate-ping rounded-full opacity-60', tone)} />}
          <span className={cn('relative inline-flex size-2.5 rounded-full', tone)} />
        </span>
        <span className="text-sm font-medium">{text}</span>
        {!frozen && ageSeconds != null && connection === 'open' && (
          <span className="text-xs text-muted tabular-nums">updated {ageSeconds <= 1 ? 'just now' : `${ageSeconds} s ago`}</span>
        )}
      </div>
      <Button size="sm" variant={frozen ? 'default' : 'outline'} onClick={onToggleFreeze} aria-pressed={frozen}>
        {frozen ? <Play aria-hidden /> : <Pause aria-hidden />}
        {frozen ? 'Resume live' : 'Freeze view'}
      </Button>
    </div>
  );
}
