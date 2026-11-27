import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import { useMemo } from 'react';
import { TimeSeriesChart } from '@/components/charts/TimeSeriesChart';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { useToast } from '@/components/ui/toast';
import { apiGet, apiPost } from '@/lib/api/client';
import type { ClassifyResponse, FrameDrop, FrameSecond, GameSession, GameSessionResponse, GameSessionsResponse, QualityMinute } from '@/lib/api/types';
import { formatDateTime, formatTime } from '@/lib/format';
import { duration, Findings, ms, Stat, toChart } from '@/features/quality/shared';
import { useStatus } from '@/features/status/useStatus';
import type { GamingSearch } from './search';

const DAYS = 30;

/** What a kind of finding is about, as the drops' summary says it. */
const KIND_TEXT: Record<string, string> = {
  gpu: 'the GPU at its limit',
  cpu: 'the CPU at its limit',
  thermal: 'heat or power limits',
  memory: 'memory running short',
  storage: 'slow drive reads',
  driver: 'drivers holding a processor',
  program: 'another program getting busy',
  display: 'frames not reaching the screen',
  game: 'nothing else changing (the game itself)',
};

/** Why a program counted as a game. */
const RECOGNIZED: Record<string, string> = {
  'full screen': 'it covered the screen',
  windows: 'Windows knows it as a game',
  library: 'it is installed in a game library',
  graphics: 'it kept the 3D engine busy',
  user: 'you chose it',
};

const COLUMNS: Column<GameSession>[] = [
  { id: 'start', header: 'When', value: (s) => Date.parse(s.start), groupable: false, firstDesc: true },
  { id: 'game', header: 'Game', value: (s) => s.game },
  { id: 'fps', header: 'Average fps', value: (s) => s.fpsAvg ?? 0, groupable: false, firstDesc: true },
  { id: 'drops', header: 'Drop seconds', value: (s) => s.dropSeconds, groupable: false, firstDesc: true },
  { id: 'stutters', header: 'Stutters', value: (s) => s.stutters, groupable: false, firstDesc: true },
];

/**
 * Games: every session of a game in front (full screen, borderless or windowed) with its frame rate, the frames that
 * reached the screen, 1 % low, stutters and drops; a session's chart, what the session shows, and each drop with what
 * else was measured while it lasted.
 */
export function GamingPage() {
  const search = useSearch({ strict: false }) as GamingSearch;
  const navigate = useNavigate();
  const { data, error } = useQuery({
    queryKey: ['gaming-sessions', DAYS],
    queryFn: ({ signal }) => apiGet<GameSessionsResponse>(`/gaming/sessions?days=${DAYS}`, signal),
    refetchInterval: 60_000,
  });
  const select = (id: number | undefined) => void navigate({ to: '/gaming', search: id ? { session: id } : {} });
  const agent = useStatus().data?.agent.status;
  const idle = agent != null && agent.state !== 'Recording' && !agent.watching;

  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-xl font-semibold">Gaming</h1>
        <p className="text-sm text-muted">
          Frame rates of the games you play, full screen or in a window: the frames each game sends, from the graphics card&apos;s own present events, and the frames
          that reach the screen, from PresentMon (nothing is added to the game). A drop or a run of stutters becomes an incident with the minutes before it; below, each
          drop of a recorded session shows what else changed while it lasted.
        </p>
      </header>

      {idle && (
        <p className="rounded-lg border border-warning/40 bg-warning/5 p-3 text-sm" role="status">
          The recorder is neither recording nor watching for problems, so games are measured only while this dashboard is open. Turn on <i>Watch for problems
          when not recording</i> in Settings › Recording to measure every game you play.
        </p>
      )}
      {error && <p className="text-sm text-danger">The game sessions could not be loaded.</p>}
      {!data && !error && <p className="text-sm text-muted">Loading…</p>}
      {data && data.sessions.length === 0 && (
        <Card>
          <CardContent className="py-4 text-sm text-muted">
            No game sessions in the last {DAYS} days. Frame rates are counted while a game is in front, with the recorder running as administrator (Settings ›
            Recording › Game frame rates). A game is recognized when it covers the screen, when Windows knows it as a game, when it is installed in a game
            library (Steam, Epic, Riot, GOG, Xbox, EA, Ubisoft…) or when it keeps the graphics card&apos;s 3D engine busy; you can add one below.
          </CardContent>
        </Card>
      )}
      {data && data.sessions.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Sessions in the last {DAYS} days</CardTitle>
          </CardHeader>
          <CardContent>
            <DataTable
              viewKey="gaming-sessions"
              label="Game sessions"
              rows={data.sessions}
              columns={COLUMNS}
              rowKey={(s) => String(s.id)}
              defaultView={{ groupBy: [], sort: [{ id: 'start', desc: true }] }}
              listClassName="gap-2"
              renderRow={(s) => <SessionRow session={s} selected={s.id === search.session} now={Date.parse(data.now)} onSelect={() => select(s.id === search.session ? undefined : s.id)} />}
            />
          </CardContent>
        </Card>
      )}
      {search.session && <SessionDetail id={search.session} />}
      <ChosenGames />
    </div>
  );
}

/** Counts a program as a game, or as not a game, or lets the recognizer decide again. */
function useClassify() {
  const queryClient = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: (body: { game: string; isGame: boolean | null }) => apiPost<ClassifyResponse>('/gaming/classify', body),
    onSuccess: (_answer, body) => {
      toast.show({
        title: body.isGame === null ? `${body.game} is recognized as before.` : body.isGame ? `${body.game} counts as a game from now on.` : `${body.game} is not counted as a game from now on.`,
        tone: 'success',
      });
      void queryClient.invalidateQueries({ queryKey: ['settings'] });
    },
    onError: () => toast.show({ title: 'The choice could not be saved.', tone: 'error' }),
  });
}

/** The programs you counted as games or not, and adding one the recognizer misses. */
function ChosenGames() {
  const classify = useClassify();
  const { data } = useQuery({
    queryKey: ['settings'],
    queryFn: ({ signal }) => apiGet<{ settings: { recorder: { games?: string[]; notGames?: string[] } } }>('/settings', signal),
  });
  const games = data?.settings.recorder.games ?? [];
  const notGames = data?.settings.recorder.notGames ?? [];
  return (
    <Card>
      <CardHeader>
        <CardTitle>Games you chose</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3 text-sm">
        <form
          className="flex flex-wrap items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            const input = e.currentTarget.elements.namedItem('game') as HTMLInputElement;
            const game = input.value.trim().replace(/\.exe$/i, '');
            if (game) {
              classify.mutate({ game, isGame: true });
              input.value = '';
            }
          }}
        >
          <label htmlFor="add-game" className="text-muted">
            Count a program as a game (its file name, e.g. MyGame.exe):
          </label>
          <input id="add-game" name="game" className="rounded-md border border-border bg-surface px-2 py-1" autoComplete="off" />
          <Button type="submit" size="sm" variant="outline" disabled={classify.isPending}>
            Add
          </Button>
        </form>
        {games.length + notGames.length === 0 ? (
          <p className="text-muted">None: every game is recognized by itself.</p>
        ) : (
          <ul className="flex flex-wrap gap-2">
            {games.map((g) => (
              <li key={`game-${g}`}>
                <Badge tone="success">
                  {g} counts
                  <button type="button" className="ml-1" aria-label={`Recognize ${g} as before`} onClick={() => classify.mutate({ game: g, isGame: null })}>
                    ×
                  </button>
                </Badge>
              </li>
            ))}
            {notGames.map((g) => (
              <li key={`not-${g}`}>
                <Badge>
                  {g} not a game
                  <button type="button" className="ml-1" aria-label={`Recognize ${g} as before`} onClick={() => classify.mutate({ game: g, isGame: null })}>
                    ×
                  </button>
                </Badge>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

function SessionRow({ session, selected, now, onSelect }: { session: GameSession; selected: boolean; now: number; onSelect: () => void }) {
  const playing = now - Date.parse(session.end) < 120_000;
  const perMinute = session.seconds > 0 ? (session.stutters * 60) / session.seconds : 0;
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      className={`mb-2 flex w-full flex-wrap items-center justify-between gap-3 rounded-lg border p-3 text-left text-sm ${selected ? 'border-accent bg-accent/5' : 'border-border bg-surface hover:border-accent/50'}`}
    >
      <span className="flex flex-col">
        <span className="font-medium">
          {session.game}
          {playing && <span className="ml-2 text-xs text-success">playing now</span>}
          {(session.windowedPercent ?? 0) >= 50 && <span className="ml-2 text-xs text-muted">windowed</span>}
        </span>
        <span className="text-xs text-muted">
          {formatDateTime(session.start)} · {duration(session.seconds)}
        </span>
      </span>
      <span className="flex flex-wrap gap-4 tabular-nums">
        <span>{session.fpsAvg != null ? `${Math.round(session.fpsAvg)} fps` : '–'}</span>
        {session.displayedFps != null && <span className="text-muted">{Math.round(session.displayedFps)} shown</span>}
        <span className="text-muted">1 % low {session.low1Fps != null ? Math.round(session.low1Fps) : '–'}</span>
        <span className={session.dropSeconds > 0 ? 'text-warning' : 'text-muted'}>{session.dropSeconds} s of drops</span>
        <span className={perMinute >= 1 ? 'text-warning' : 'text-muted'}>{perMinute.toFixed(1)} stutters/min</span>
      </span>
    </button>
  );
}

function SessionDetail({ id }: { id: number }) {
  const { data, error } = useQuery({
    queryKey: ['gaming-session', id],
    queryFn: ({ signal }) => apiGet<GameSessionResponse>(`/gaming/sessions/${id}`, signal),
    refetchInterval: 60_000,
  });
  const classify = useClassify();
  const chart = useMemo(() => (data ? sessionChart(data.seconds, data.minutes) : null), [data]);
  if (error) return <p className="text-sm text-danger">This session could not be loaded.</p>;
  if (!data || !chart) return <p className="text-sm text-muted">Loading…</p>;
  const { session, drops, patterns, incidents } = data;
  const droppedPerSecond = session.dropped != null && session.seconds > 0 ? session.dropped / session.seconds : null;
  return (
    <Card>
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2">
        <CardTitle>
          {session.game}, {formatDateTime(session.start)} to {formatTime(session.end)}
        </CardTitle>
        <Button size="sm" variant="outline" disabled={classify.isPending} onClick={() => classify.mutate({ game: session.game, isGame: false })}>
          Not a game
        </Button>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <p className="text-xs text-muted">
          Counted because {RECOGNIZED[session.recognized ?? ''] ?? 'it covered the screen'}
          {(session.windowedPercent ?? 0) >= 50 ? '; played in a window' : ''}
          {session.presentMode ? `; frames shown as "${session.presentMode}"` : ''}.
        </p>
        <div className="flex flex-wrap gap-2">
          <Stat label="Sent" value={session.fpsAvg != null ? `${Math.round(session.fpsAvg)} fps` : '–'} hint="frames the game presented" />
          <Stat
            label="On the screen"
            value={session.displayedFps != null ? `${Math.round(session.displayedFps)} fps` : '–'}
            hint={droppedPerSecond != null ? `${droppedPerSecond.toFixed(1)} a second never shown` : 'not measured'}
          />
          <Stat label="1 % low" value={session.low1Fps != null ? `${Math.round(session.low1Fps)} fps` : '–'} hint="slowest 1 % of frames" />
          <Stat label="Slowest seconds" value={session.fpsP1 != null ? `${Math.round(session.fpsP1)} fps` : '–'} hint="its slowest 1 % of seconds" />
          <Stat label="Stutters" value={String(session.stutters)} hint={`${((session.stutters * 60) / Math.max(1, session.seconds)).toFixed(1)} a minute`} />
          <Stat label="Drops" value={`${session.dropSeconds} s`} hint="40 % or more below its usual" />
          <Stat label="Longest frame" value={ms(session.worstMs)} />
          {session.displayLatencyMs != null && <Stat label="Display latency" value={ms(session.displayLatencyMs)} hint="from a frame's start to the screen" />}
          {session.gpuBusyPercent != null && (
            <Stat label="GPU's share" value={`${Math.round(session.gpuBusyPercent)} %`} hint={session.cpuBusyPercent != null ? `CPU ${Math.round(session.cpuBusyPercent)} % of each frame` : 'of each frame'} />
          )}
        </div>
        {data.findings && data.findings.length > 0 && <Findings findings={data.findings} />}
        <TimeSeriesChart result={chart.result} catalog={chart.catalog} label={`${session.game} frame rate`} className="h-72" />
        {patterns.length > 0 && (
          <p className="text-sm" role="status">
            Of its {drops.length} drop{drops.length === 1 ? '' : 's'},{' '}
            {patterns.map((p, i) => `${p.drops} coincided with ${KIND_TEXT[p.kind] ?? p.kind}${i < patterns.length - 1 ? ', ' : '.'}`).join('')}
          </p>
        )}
        {data.seconds.length === 0 ? (
          <p className="text-sm text-muted">
            This session was not recorded second by second (the recorder only watched), so its drops are counted by minute and cannot be explained here. Each drop
            or run of stutters became an incident with the minutes before it{incidents.length > 0 ? ': see below.' : '.'}
          </p>
        ) : drops.length === 0 ? (
          <p className="text-sm text-muted">No drop of 40 % or more below its usual frame rate, and no frame of 100 ms or more.</p>
        ) : (
          <ol className="flex flex-col gap-3">
            {drops.map((d) => (
              <DropItem key={d.from} drop={d} />
            ))}
          </ol>
        )}
        {incidents.length > 0 && (
          <div className="flex flex-col gap-1 text-sm">
            <span className="font-medium">Incidents during this session</span>
            {incidents.map((i) => (
              <Link key={i.key} to="/incidents/$key" params={{ key: i.key }} className="text-accent hover:underline">
                {formatTime(i.at)} {i.title}
              </Link>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function DropItem({ drop }: { drop: FrameDrop }) {
  const span = drop.from === drop.to ? `at ${formatTime(drop.from)}` : `${formatTime(drop.from)} to ${formatTime(drop.to)}`;
  return (
    <li className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-3">
      <span className="text-sm font-medium">
        {Math.round(drop.lowestFps)} fps {span}
        {drop.usualFps != null && <span className="text-muted"> (usual {Math.round(drop.usualFps)} fps)</span>}
        {drop.worstMs != null && drop.worstMs >= 100 && <span className="text-muted">, a frame of {Math.round(drop.worstMs)} ms</span>}
      </span>
      <Findings findings={drop.findings} />
    </li>
  );
}

/** The session's chart: its seconds when recorded, else its minutes (average with the lowest second). */
function sessionChart(seconds: FrameSecond[], minutes: QualityMinute[]) {
  if (seconds.length > 0) {
    return toChart(
      seconds,
      (s) => s.at,
      [
        { id: 'fps', label: 'Frames sent', unit: 'fps', value: (s) => s.fps },
        { id: 'shown', label: 'Frames on the screen', unit: 'fps', value: (s) => s.displayedFps },
        { id: 'usual', label: 'Usual frame rate', unit: 'fps', value: (s) => s.usualFps },
        { id: 'worst', label: 'Longest frame', unit: 'ms', value: (s) => s.worstMs },
      ],
      'Gaming',
    );
  }

  return toChart(
    minutes.filter((m) => m.fpsAvg != null),
    (m) => m.at,
    [
      { id: 'fps', label: 'Frames sent (minute average)', unit: 'fps', value: (m) => m.fpsAvg, low: (m) => m.fpsMin },
      { id: 'shown', label: 'Frames on the screen', unit: 'fps', value: (m) => m.displayedFps },
      { id: 'worst', label: 'Longest frame', unit: 'ms', value: (m) => m.worstMs },
    ],
    'Gaming',
  );
}
