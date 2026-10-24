import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useToast } from '@/components/ui/toast';
import { ApiError, apiPost } from '@/lib/api/client';
import type { AgentStatus, CaptureResponse, UserIncidentType } from '@/lib/api/types';
import { formatTime } from '@/lib/format';

/** Start records everything, or only <c>selection</c> (a custom recording). */
export type RecorderCommand = { kind: 'start'; selection?: string[] | null } | { kind: 'stop' } | { kind: 'resume' } | { kind: 'pause'; minutes: number | null };

const DONE: Record<RecorderCommand['kind'], string> = {
  start: 'Recording started',
  stop: 'Recording stopped',
  resume: 'Recording resumed',
  pause: 'Recording paused',
};

export interface CaptureInput {
  type: UserIncidentType | null;
  note?: string;
}

/** Recorder commands and incident capture, with toasts and status refresh. */
export function useRecorderActions() {
  const queryClient = useQueryClient();
  const toast = useToast();

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['status'] });
    void queryClient.invalidateQueries({ queryKey: ['incidents'] });
  };

  const failed = (title: string) => (error: unknown) =>
    toast.show({
      title: error instanceof ApiError && error.status === 0 ? 'Connection lost' : title,
      description: error instanceof Error ? error.message : undefined,
      tone: 'error',
    });

  const command = useMutation({
    mutationFn: (cmd: RecorderCommand) =>
      cmd.kind === 'pause'
        ? apiPost<AgentStatus>('/recorder/pause', { minutes: cmd.minutes })
        : cmd.kind === 'start' && cmd.selection
          ? apiPost<AgentStatus>('/recorder/start', { selection: cmd.selection })
          : apiPost<AgentStatus>(`/recorder/${cmd.kind}`, {}),
    onSuccess: (status, cmd) => {
      refresh();
      if ((cmd.kind === 'stop' || cmd.kind === 'pause') && status.state === 'Recording' && status.activeIncident) {
        toast.show({
          tone: 'info',
          title: cmd.kind === 'stop' ? 'Stopping after the incident' : 'Pausing after the incident',
          description: `${status.activeIncident} is still recording what happens next; recording ${cmd.kind === 'stop' ? 'stops' : 'pauses'} as soon as it is complete.`,
        });
        return;
      }
      toast.show({ title: cmd.kind === 'start' && cmd.selection ? `Custom recording started (${cmd.selection.length} items)` : DONE[cmd.kind], tone: 'success' });
      if (cmd.kind === 'start' && cmd.selection) void queryClient.invalidateQueries({ queryKey: ['settings'] });
    },
    onError: failed('The recorder did not accept the command'),
  });

  const capture = useMutation({
    mutationFn: ({ type, note }: CaptureInput) =>
      apiPost<CaptureResponse>('/incidents/capture', { type, note: note?.trim() ? note.trim() : null }),
    onSuccess: ({ capture: result }) => {
      refresh();
      const until = formatTime(result.recordingUntil);
      toast.show({
        tone: 'success',
        title: result.merged ? `Added to ${result.incidentKey}` : `${result.incidentKey} captured`,
        description: result.merged
          ? 'That incident is still recording, so this report was added to it.'
          : result.snapshotOnly
            ? `The recorder was not running, so there is no data from before. Recording until ${until}.`
            : `The minutes before are saved. Recording continues until ${until}.`,
      });
    },
    onError: failed('Capture failed'),
  });

  return { command, capture };
}
