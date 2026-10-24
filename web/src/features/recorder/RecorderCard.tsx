import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Pause, Play, ShieldCheck, SlidersHorizontal, Square } from 'lucide-react';
import { useState } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { apiGet } from '@/lib/api/client';
import { isIndefinitePause, type AgentStatus, type LiveAgent, type SettingsResponse } from '@/lib/api/types';
import { formatBytes, formatPercent, formatRemaining, formatTime } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { RecordingPicker } from './RecordingPicker';
import type { RecorderCommand } from './useRecorderActions';

export interface RecorderCardProps {
  status: AgentStatus | null;
  /** Why the Agent is unreachable, when it is. */
  unavailable?: string | null;
  agent?: LiveAgent | null;
  busy?: boolean;
  onCommand: (command: RecorderCommand) => void;
  /** Show the recorder's own CPU and memory use. */
  showOverhead?: boolean;
  title?: string;
  className?: string;
}

const MODE_TEXT = { Manual: 'Manual: records when you start it', Always: 'Always on', Scheduled: 'On a schedule' } as const;

/** Recorder state and controls: start/stop, pause for a while, resume; plus the active incident countdown. */
export function RecorderCard({ status, unavailable, agent, busy = false, onCommand, showOverhead = true, title, className }: RecorderCardProps) {
  const now = useNow(status?.activeIncidentUntil != null);
  const [picking, setPicking] = useState(false);
  // The last custom recording is offered again.
  const settings = useQuery({ queryKey: ['settings'], queryFn: ({ signal }) => apiGet<SettingsResponse>('/settings', signal), enabled: picking });
  const recording = status?.state === 'Recording' || status?.state === 'Starting';

  return (
    <Card className={className}>
      <CardHeader>
        <CardTitle>{title ?? 'Recorder'}</CardTitle>
        <StateBadge status={status} />
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {!status ? (
          <p className="text-sm text-muted">{unavailable ?? 'Connecting to the recorder…'}</p>
        ) : (
          <>
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
              <dt className="text-muted">Mode</dt>
              <dd>{MODE_TEXT[status.mode]}</dd>
              {status.state === 'Recording' && (
                <>
                  <dt className="text-muted">Records</dt>
                  <dd>{status.recordingItems != null ? `A custom selection (${status.recordingItems} items)` : 'Everything'}</dd>
                </>
              )}
              {status.state === 'Paused' && (
                <>
                  <dt className="text-muted">Paused</dt>
                  <dd>{isIndefinitePause(status.pausedUntil) ? 'Until you resume' : `Until ${formatTime(status.pausedUntil)}`}</dd>
                </>
              )}
              {status.activeIncident && (
                <>
                  <dt className="text-muted">Incident</dt>
                  <dd>
                    <Link to="/incidents/$key" params={{ key: status.activeIncident }} className="font-mono text-accent hover:underline">
                      {status.activeIncident}
                    </Link>{' '}
                    <span className="text-muted">recording, {formatRemaining(status.activeIncidentUntil, now)} left</span>
                  </dd>
                </>
              )}
              {status.liveView && status.state !== 'Recording' && (
                <>
                  <dt className="text-muted">Live view</dt>
                  <dd>Sampling for this dashboard only; nothing is saved</dd>
                </>
              )}
              {status.enhanced && (
                <>
                  <dt className="text-muted">Enhanced mode</dt>
                  <dd>{status.enhanced}</dd>
                </>
              )}
              <dt className="text-muted">Rights</dt>
              <dd className="flex items-center gap-1.5">
                {status.elevated ? (
                  <>
                    <ShieldCheck className="size-4 text-success" aria-hidden /> Administrator (all sensors)
                  </>
                ) : (
                  'Standard user (some sensors need administrator)'
                )}
              </dd>
            </dl>

            <div className="flex flex-wrap gap-2">
              {status.state === 'Paused' ? (
                <Button size="sm" disabled={busy} onClick={() => onCommand({ kind: 'resume' })}>
                  <Play aria-hidden /> Resume
                </Button>
              ) : recording ? (
                <Button size="sm" variant="outline" disabled={busy} onClick={() => onCommand({ kind: 'stop' })}>
                  <Square aria-hidden /> Stop
                </Button>
              ) : (
                <div className="flex w-full flex-wrap gap-2">
                  <Button disabled={busy} onClick={() => onCommand({ kind: 'start' })} className="flex-1">
                    <Play aria-hidden /> Record everything
                  </Button>
                  <Button variant="outline" disabled={busy} onClick={() => setPicking(true)}>
                    <SlidersHorizontal aria-hidden /> Custom recording…
                  </Button>
                </div>
              )}
              {recording && (
                <Button size="sm" variant="outline" disabled={busy} onClick={() => setPicking(true)}>
                  <SlidersHorizontal aria-hidden /> Change what is recorded…
                </Button>
              )}
              {status.state !== 'Paused' && (
                <div className="flex flex-wrap gap-2" role="group" aria-label="Pause recording">
                  <Button size="sm" variant="outline" disabled={busy} onClick={() => onCommand({ kind: 'pause', minutes: 15 })}>
                    <Pause aria-hidden /> 15 min
                  </Button>
                  <Button size="sm" variant="outline" disabled={busy} onClick={() => onCommand({ kind: 'pause', minutes: 60 })}>
                    1 hour
                  </Button>
                  <Button size="sm" variant="outline" disabled={busy} onClick={() => onCommand({ kind: 'pause', minutes: null })}>
                    Until resumed
                  </Button>
                </div>
              )}
            </div>
          </>
        )}

        <RecordingPicker
          open={picking && (!settings.isLoading || settings.isError)}
          title="Custom recording"
          initial={settings.data?.settings.recorder.customSelection?.items ?? null}
          onClose={() => setPicking(false)}
          onConfirm={(selection) => {
            setPicking(false);
            onCommand({ kind: 'start', selection });
          }}
          confirmLabel="Start custom recording"
          busy={busy}
        />

        {agent && showOverhead && (
          <p className="text-xs text-muted">
            Recorder overhead: {formatPercent(agent.cpuPercent.value, 2)} CPU · {formatBytes(agent.workingSetBytes.value, 0)} memory
            {agent.managedHeapBytes?.value != null ? ` (managed heap ${formatBytes(agent.managedHeapBytes.value, 0)})` : ''} · in-memory buffer {agent.ringFrames} samples (
            {agent.ringMegabytes} MB)
          </p>
        )}
      </CardContent>
    </Card>
  );
}

function StateBadge({ status }: { status: AgentStatus | null }) {
  if (!status) return <Badge>Not connected</Badge>;
  switch (status.state) {
    case 'Recording':
      return <Badge tone="success">Recording</Badge>;
    case 'Starting':
      return <Badge tone="success">Starting</Badge>;
    case 'Paused':
      return <Badge tone="warning">Paused</Badge>;
    case 'Stopping':
      return <Badge>Stopping</Badge>;
    default:
      return <Badge>Not recording</Badge>;
  }
}
