import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGet, apiPost, apiRequest } from '@/lib/api/client';

/** Where characters are generated: nowhere, the generator on this PC (internal), or another service (external). */
export interface GeneratorSettings {
  kind: 'off' | 'internal' | 'external';
  url: string | null;
  hasKey: boolean;
  /** The internal generator's Python and folder (as set, or found on this PC). */
  python?: string | null;
  folder?: string | null;
  /** The kit folder the models are read from (KITGEN; null: the one settings.py names). */
  kit?: string | null;
}

/** A prerequisite of the generator on this PC. */
export interface PrerequisiteStep {
  id: string;
  title: string;
  ok: boolean;
  warn: boolean;
  detail: string;
  actions: string[];
}

/** A setup step run in the background. */
export interface SetupRun {
  action: string;
  state: 'running' | 'done' | 'failed';
  log: string[];
  started: string;
}

export function usePrerequisites() {
  return useQuery({
    queryKey: ['character-generator-prerequisites'],
    queryFn: ({ signal }) => apiGet<{ kit: string | null; steps: PrerequisiteStep[]; running: SetupRun | null }>('/overlay/generator/prerequisites', signal),
    staleTime: 60_000,
  });
}

/** The setup step under way, followed every second and a half while it runs. */
export function useSetupRun(running: boolean) {
  return useQuery({
    queryKey: ['character-generator-setup'],
    queryFn: ({ signal }) => apiGet<SetupRun | null>('/overlay/generator/prerequisites/run', signal),
    refetchInterval: (q) => (q.state.data?.state === 'running' || (running && !q.state.data) ? 1500 : false),
  });
}

export function useRunSetup() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (action: string) => apiPost<SetupRun>(`/overlay/generator/prerequisites/${encodeURIComponent(action)}`),
    onSuccess: (run) => client.setQueryData(['character-generator-setup'], run),
  });
}

/** A generation followed in the background by PC Diagnostic: stills to choose from, then the character animated. */
export interface GenerationJob {
  id: string;
  type: 'stills' | 'character';
  prompt: string;
  kind: 'person' | 'animal';
  name: string | null;
  state: 'queued' | 'running' | 'done' | 'failed';
  step: string;
  progress: number;
  error: string | null;
  created: string;
  stills: number;
  character: string | null;
  from: string | null;
  /** What the generator said last about it, newest last. */
  log?: string[] | null;
}

export const isActive = (j: GenerationJob) => j.state === 'queued' || j.state === 'running';

export function useGenerator() {
  return useQuery({ queryKey: ['character-generator'], queryFn: ({ signal }) => apiGet<GeneratorSettings>('/overlay/generator', signal) });
}

export function useSaveGenerator() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (s: { kind: string; url: string | null; key?: string | null; python?: string | null; folder?: string | null; kit?: string | null }) => apiRequest<GeneratorSettings>('PUT', '/overlay/generator', s),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['character-generator'] });
      void client.invalidateQueries({ queryKey: ['character-generator-status'] });
      void client.invalidateQueries({ queryKey: ['character-generator-prerequisites'] });
    },
  });
}

/** The generator's state: running (and busy) or not, asked every few seconds while it is shown. */
export interface GeneratorStatus {
  running: boolean;
  busy: boolean;
  message: string;
  /** The generator on this PC: started and stopped here. */
  canStart: boolean;
  /** Its process runs (started here, also by an earlier run) but does not answer yet. */
  starting?: boolean;
}

export function useGeneratorStatus() {
  return useQuery({ queryKey: ['character-generator-status'], queryFn: ({ signal }) => apiGet<GeneratorStatus>('/overlay/generator/status', signal), refetchInterval: 4000 });
}

export function useStartGenerator() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiPost<{ ok: boolean; message: string }>('/overlay/generator/start'),
    onSettled: () => client.invalidateQueries({ queryKey: ['character-generator-status'] }),
  });
}

export function useStopGenerator() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiPost<{ ok: boolean; message: string }>('/overlay/generator/stop'),
    onSettled: () => client.invalidateQueries({ queryKey: ['character-generator-status'] }),
  });
}

/** Where the generator is set up: its section on the On-screen page. */
export const GENERATOR_SECTION = 'character-generator';

/** Brings the generator's section into sight. */
export function showGeneratorSection() {
  // The generator has a page of its own.
  if (!document.getElementById(GENERATOR_SECTION)) {
    window.location.assign(`/generator#${GENERATOR_SECTION}`);
    return;
  }
  window.setTimeout(() => document.getElementById(GENERATOR_SECTION)?.scrollIntoView?.({ behavior: 'smooth', block: 'start' }), 50);
}

export function useTestGenerator() {
  // The choice on the page when given (tested before it is saved), else the one saved.
  return useMutation({ mutationFn: (choice?: { kind: string; url?: string | null; key?: string | null; python?: string | null; folder?: string | null }) => apiPost<{ ok: boolean; message: string }>('/overlay/generator/test', choice) });
}

/** The generations, polled every 3 s while one is under way (a finished character then shows in the picker). */
export function useGenerations() {
  const client = useQueryClient();
  return useQuery({
    queryKey: ['character-generations'],
    queryFn: async ({ signal }) => {
      const jobs = await apiGet<GenerationJob[]>('/overlay/generations', signal);
      if (jobs.some((j) => j.type === 'character' && j.state === 'done')) void client.invalidateQueries({ queryKey: ['overlay-characters'] });
      return jobs;
    },
    refetchInterval: (q) => ((q.state.data ?? []).some(isActive) ? 3000 : false),
  });
}

export function useStartGeneration() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (start: { type: 'stills' | 'character'; prompt?: string; kind?: string; name?: string; from?: string; still?: number; actions?: string[]; faces?: number }) =>
      apiPost<GenerationJob>('/overlay/generations', start),
    onSuccess: () => client.invalidateQueries({ queryKey: ['character-generations'] }),
  });
}

export function useForgetGeneration() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiRequest('DELETE', `/overlay/generations/${encodeURIComponent(id)}`),
    onSuccess: () => client.invalidateQueries({ queryKey: ['character-generations'] }),
  });
}

export const stillUrl = (job: Pick<GenerationJob, 'id'>, n: number) => `/api/v1/overlay/generations/${encodeURIComponent(job.id)}/stills/${n}`;
