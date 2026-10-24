import { ArrowDown, ArrowUp, ChevronDown, Plus, Sparkles, Trash2, Upload } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { Dialog } from '@/components/ui/dialog';
import type { CharacterInfo, CharacterMovement, CharacterPlace, OverlayCharacter, OverlayPanel, OverlaySource } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { visualOf } from '../model';
import { Check, Section, Segmented, Slider } from '../PanelInspector';
import { CharacterGenerate, Generations } from './CharacterGenerate';
import { CharacterImport } from './CharacterImport';
import { CharacterThumb } from './CharacterSprite';
import { useAllActions } from './animations';
import { useCharacters, useDeleteCharacter } from './library';
import { clipFor } from './pack';
import { EFFECTS, KINDS, STATUSES, kindOf, ordered, propFor, reactionFor, reactionsFor, readingFor, statusOf, type ReactionStatus, type ReadingKind } from './reactions';
import { PROPS } from './props';

/** The most characters a panel has. */
export const MAX_CHARACTERS = 8;

export const PLACES: readonly (readonly [CharacterPlace, string])[] = [
  ['top', 'On top'],
  ['inside', 'Inside'],
  ['left', 'Left'],
  ['right', 'Right'],
  ['screen', 'Screen bottom'],
  ['around', 'All around'],
  ['spot', 'Where dropped'],
  ['wheel', 'In its ring'],
  ['needle', 'On its needle'],
  ['bar', 'Pushing its bar'],
  ['graph', 'Surfing its graph'],
];

/** Where a screen's own characters play. */
export const SCREEN_PLACES: readonly (readonly [CharacterPlace, string])[] = [
  ['screen', 'Along the bottom'],
  ['top', 'Along the top'],
  ['spot', 'Where dropped'],
  ['wheel', 'In a ring'],
  ['needle', 'On a needle'],
  ['bar', 'Pushing a bar'],
  ['graph', 'Surfing a graph'],
];

/** The visuals a character on a gauge plays on, by its place. */
const GAUGES: Record<string, string[]> = { wheel: ['ring', 'arc', 'speed'], needle: ['ring', 'arc', 'speed'], bar: ['bar'], graph: ['graph', 'line'] };

export const MOVEMENTS: readonly (readonly [CharacterMovement, string])[] = [
  ['stay', 'Stay'],
  ['pace', 'Pace'],
  ['wander', 'Wander'],
];

export const ACTIONS: readonly (readonly [string, string])[] = [
  ['idle', 'Idle'],
  ['sit', 'Sit'],
  ['sleep', 'Sleep'],
  ['play', 'Play'],
  ['jump', 'Jump'],
  ['dance', 'Dance'],
  ['walk', 'Walk'],
  ['run', 'Run'],
  // The emotions (made by the generator in Animations; until then each plays the closest clip a character has).
  ['worried', 'Worried'],
  ['panic', 'Panic'],
  ['tired', 'Tired'],
  ['cheer', 'Cheer'],
];

/**
 * A new character of a panel: on its top, wandering, reacting to the panel's most urgent reading; a shipped pet small,
 * an imported character (often a person) taller.
 */
export function newCharacter(character: string, taken: readonly OverlayCharacter[], info?: Pick<CharacterInfo, 'shipped'>, place: CharacterPlace = 'top'): OverlayCharacter {
  let n = taken.length + 1;
  while (taken.some((c) => c.id === `char-${n}`)) n++;
  return { id: `char-${n}`, character, place, movement: 'wander', action: 'idle', x: 0.5, size: info && !info.shipped ? 80 : 48, speed: 100, flip: false, behind: false, opacity: 100, react: 'auto' };
}

/** What a character plays for an action it has no clip for (its closest), or null when it has one. */
export function playsInstead(info: Pick<CharacterInfo, 'actions'> | undefined, action: string): string | null {
  if (!info) return null;
  const played = clipFor({ clips: info.actions.map((a) => ({ action: a, fps: 1, frames: [] })) }, action).action;
  return played === action ? null : played;
}

/**
 * A panel's characters (Characters tab): each with where it plays, how it moves, what it does, its size, speed and
 * facing; adding one from the gallery (animated while the mouse is on it), removing one.
 */
export function CharactersTab({
  panel,
  onChange,
  sources = new Map(),
  screen = false,
  focus = null,
}: {
  panel: OverlayPanel;
  onChange: (characters: OverlayCharacter[]) => void;
  sources?: ReadonlyMap<string, OverlaySource>;
  /** A screen's own characters (on the whole screen; `panel` is its stand-in with every reading it shows). */
  screen?: boolean;
  /** A character to open (clicked on the screen): its id, and a count that changes each time it is asked. */
  focus?: { id: string; at: number } | null;
}) {
  const actions = useAllActions();
  const characters = panel.characters ?? [];
  const places = screen ? SCREEN_PLACES : PLACES;
  const [open, setOpen] = useState<string | null>(focus?.id ?? characters[0]?.id ?? null);
  useEffect(() => {
    if (focus) setOpen(focus.id);
  }, [focus]);
  const [picking, setPicking] = useState<'add' | string | null>(null);
  const known = useCharacters().data ?? [];
  const confirm = useConfirm();
  const infoOf = (id: string) => known.find((k) => k.id === id);
  const change = (id: string, patch: Partial<OverlayCharacter>) => onChange(characters.map((c) => (c.id === id ? { ...c, ...patch } : c)));
  return (
    <Section title={screen ? 'Characters on this screen' : 'Characters'} detail={`${characters.length} of ${MAX_CHARACTERS}`}>
      <p className="text-xs text-muted">
        {screen
          ? 'Characters of the screen itself, apart from any panel: along its bottom or its top, reacting to the readings of all its panels.'
          : 'Characters playing on, in or around this panel, on the screen as here. Several can share a panel. For characters of the whole screen, click an empty spot of the screen.'}
      </p>
      <ol aria-label={screen ? 'Characters of this screen' : 'Characters of this panel'} className="flex flex-col gap-2">
        {characters.map((c) => {
          const info = infoOf(c.character);
          const name = info?.name ?? (known.length ? 'A character no longer here' : 'Character');
          const expanded = open === c.id;
          return (
            <li key={c.id} className={cn('rounded-md border', expanded ? 'border-accent/50' : 'border-border')} aria-label={name}>
              <div className="flex items-center gap-2 p-1.5">
                <button type="button" className="flex min-w-0 flex-1 items-center gap-2 text-left" aria-expanded={expanded} onClick={() => setOpen(expanded ? null : c.id)}>
                  <span className="motion-hover flex size-10 shrink-0 items-center justify-center rounded bg-surface-muted">
                    <CharacterThumb id={c.character} action={c.action} size={36} />
                  </span>
                  <span className="min-w-0">
                    <span className="block truncate text-sm font-medium">{name}</span>
                    <span className="block truncate text-xs text-muted">
                      {places.find(([id]) => id === c.place)?.[1]} · {c.place === 'around' ? 'running around' : MOVEMENTS.find(([id]) => id === c.movement)?.[1]} · {actions.find(([id]) => id === c.action)?.[1] ?? c.action}
                    </span>
                  </span>
                </button>
                <Button size="icon" variant="ghost" aria-label={`Remove ${name}`} title="Remove" onClick={async () => (await confirm({ title: `Remove ${name}?`, description: screen ? 'It is taken off this screen (it stays in the library).' : 'It is taken off this panel (it stays in the library).', action: 'Remove' })) && onChange(characters.filter((x) => x.id !== c.id))}>
                  <Trash2 aria-hidden />
                </Button>
              </div>
              {expanded && <CharacterSettings character={c} info={info} panel={panel} sources={sources} places={places} screen={screen} onChange={(patch) => change(c.id, patch)} onPick={() => setPicking(c.id)} />}
            </li>
          );
        })}
      </ol>
      <Generations compact />
      <Button size="sm" variant="outline" className="self-start" disabled={characters.length >= MAX_CHARACTERS} onClick={() => setPicking('add')}>
        <Plus aria-hidden /> Add a character
      </Button>
      {picking && (
        <CharacterPicker
          current={picking === 'add' ? null : (characters.find((c) => c.id === picking)?.character ?? null)}
          onClose={() => setPicking(null)}
          onPick={(id) => {
            if (picking === 'add') {
              const made = newCharacter(id, characters, infoOf(id) ?? { shipped: !id.endsWith('.char') }, screen ? 'screen' : 'top');
              onChange([...characters, made]);
              setOpen(made.id);
            } else {
              change(picking, { character: id });
            }
            setPicking(null);
          }}
        />
      )}
    </Section>
  );
}

function CharacterSettings({
  character: c,
  info,
  panel,
  sources,
  places,
  screen = false,
  onChange,
  onPick,
}: {
  character: OverlayCharacter;
  info: CharacterInfo | undefined;
  panel: OverlayPanel;
  sources: ReadonlyMap<string, OverlaySource>;
  places: readonly (readonly [CharacterPlace, string])[];
  screen?: boolean;
  onChange: (patch: Partial<OverlayCharacter>) => void;
  onPick: () => void;
}) {
  const actions = useAllActions();
  const onGauge = c.place in GAUGES;
  const fixed = c.place === 'around' || c.place === 'left' || c.place === 'right' || onGauge;
  const gauges = panel.readings.filter((r) => GAUGES[c.place]?.includes(visualOf(r)));
  return (
    <div className="flex flex-col gap-2 border-t border-border p-2">
      <Button size="sm" variant="outline" className="self-start" onClick={onPick}>
        Another character…
      </Button>
      <span className="text-xs text-muted">Plays</span>
      <Segmented label="Where it plays" value={c.place} options={places} onChange={(place) => onChange({ place })} />
      {onGauge && (
        <label className="flex items-center gap-3 text-xs text-muted">
          <span className="w-32 shrink-0">{({ wheel: 'Runs in', needle: 'Rides', bar: 'Pushes', graph: 'Surfs' } as Record<string, string>)[c.place] ?? 'On'}</span>
          <select className="h-8 flex-1 rounded-md border border-border bg-background px-2 text-xs text-foreground" value={c.gauge ?? ''} aria-label="Its gauge" onChange={(e) => onChange({ gauge: e.target.value || null })}>
            <option value="">The most urgent, in its order (critical, then high, then the first)</option>
            {gauges.map((r) => (
              <option key={r.source} value={r.source}>
                {sources.get(r.source)?.label ?? r.source}
              </option>
            ))}
          </select>
        </label>
      )}
      {onGauge && gauges.length === 0 && <p className="text-xs text-warning">{screen ? 'No reading on this screen' : 'No reading of this panel'} is shown as {c.place === 'bar' ? 'a bar' : c.place === 'graph' ? 'a line graph' : 'a ring, arc or speedometer'}: it plays on the top edge until one is.</p>}
      {!fixed && <Segmented label="How it moves" value={c.movement} options={MOVEMENTS} onChange={(movement) => onChange({ movement })} />}
      <label className="flex items-center gap-3 text-xs text-muted">
        <span className="w-32 shrink-0">{c.movement === 'stay' || fixed ? 'Does' : 'Between walks'}</span>
        <select className="h-8 flex-1 rounded-md border border-border bg-background px-2 text-xs text-foreground" value={c.action} aria-label="What it does" onChange={(e) => onChange({ action: e.target.value })}>
          {actions.map(([id, name]) => {
            const instead = playsInstead(info, id);
            return (
              <option key={id} value={id}>
                {name}
                {(id === 'walk' || id === 'run') && c.movement !== 'stay' && !fixed ? ' (its pace)' : ''}
                {instead ? ` (plays ${actions.find(([a]) => a === instead)?.[1].toLowerCase() ?? instead})` : ''}
              </option>
            );
          })}
        </select>
      </label>
      {c.movement === 'stay' && (c.place === 'top' || c.place === 'inside' || c.place === 'screen') && (
        <Slider label="Where along it" value={Math.round(c.x * 100)} min={0} max={100} step={1} unit=" %" onChange={(v) => onChange({ x: v / 100 })} />
      )}
      {c.place === 'spot' && <Slider label="Height on the screen" value={Math.round((c.y ?? 1) * 100)} min={0} max={100} step={1} unit=" %" onChange={(v) => onChange({ y: v / 100 })} />}
      <Slider label="Height" value={c.size} min={24} max={240} step={2} unit=" px" onChange={(size) => onChange({ size })} />
      {(c.movement !== 'stay' || c.place === 'around') && <Slider label="Moving speed" value={c.speed} min={25} max={300} step={5} unit=" %" onChange={(speed) => onChange({ speed })} />}
      <Slider label="Animation speed" value={c.tempo ?? c.speed} min={25} max={300} step={5} unit=" %" onChange={(tempo) => onChange({ tempo })} />
      {(c.movement !== 'stay' || c.place === 'around') && <Slider label="Pauses last" value={c.pause ?? 100} min={25} max={400} step={5} unit=" %" onChange={(pause) => onChange({ pause })} />}
      {c.movement !== 'stay' && !fixed && (
        <>
          <Slider label="Keeps from" value={Math.round((c.from ?? 0) * 100)} min={0} max={95} step={1} unit=" %" onChange={(v) => onChange({ from: Math.min(v / 100, (c.to ?? 1) - 0.05) })} />
          <Slider label="Keeps to" value={Math.round((c.to ?? 1) * 100)} min={5} max={100} step={1} unit=" %" onChange={(v) => onChange({ to: Math.max(v / 100, (c.from ?? 0) + 0.05) })} />
        </>
      )}
      <Animations character={c} info={info} onChange={onChange} />
      <Slider label="Opacity" value={c.opacity} min={20} max={100} step={5} unit=" %" onChange={(opacity) => onChange({ opacity })} />
      <div className="flex flex-wrap gap-x-4 gap-y-1">
        {c.movement === 'stay' && !fixed && <Check label="Faces left" checked={c.flip} onChange={(flip) => onChange({ flip })} />}
        {c.place !== 'inside' && !screen && <Check label="Behind the panel" checked={c.behind} onChange={(behind) => onChange({ behind })} />}
        <Check label="Drag it on the screen" checked={c.draggable ?? false} onChange={(draggable) => onChange({ draggable })} />
        <Check label="Notices the mouse" checked={c.mouse ?? true} onChange={(mouse) => onChange({ mouse })} />
        <Check label="Answers a click" checked={c.clickable ?? false} onChange={(clickable) => onChange({ clickable })} />
      </div>
      {c.draggable && <p className="text-xs text-muted">On a monitor, drag it with the mouse to where it plays: it stays there, or keeps to the area around it when it walks.</p>}
      {(c.mouse ?? true) && <p className="text-xs text-muted">It watches the mouse near it and comes over when it rests there; rub the mouse back and forth over it to pet it.</p>}
      {(c.clickable || c.draggable) && <p className="text-xs text-muted">A click on it gets a jump and a word (dizzy after many). It takes the mouse where it is drawn, so a click there does not reach what is under it.</p>}
      <Reactions character={c} panel={panel} sources={sources} screen={screen} onChange={onChange} />
    </div>
  );
}

/**
 * The animations a character uses (all until some are unticked): it pauses doing only these, walks or runs only when one
 * is ticked (neither: it stays), and hops around a panel only when it may jump.
 */
function Animations({ character: c, info, onChange }: { character: OverlayCharacter; info: CharacterInfo | undefined; onChange: (patch: Partial<OverlayCharacter>) => void }) {
  const actions = useAllActions();
  const all = actions.map(([id]) => id);
  const plays = c.plays ?? all;
  const toggle = (id: string, on: boolean) => {
    const next = all.filter((a) => (a === id ? on : plays.includes(a)));
    // Idle always stays: something has to play.
    onChange({ plays: next.length === all.length ? null : next.includes('idle') ? next : ['idle', ...next] });
  };
  return (
    <div role="group" aria-label="Animations it uses" className="flex flex-col gap-1 text-xs">
      <span className="text-muted">Animations it uses</span>
      <div className="flex flex-wrap gap-x-3 gap-y-1">
        {actions.map(([id, name]) => (
          <label key={id} className="flex items-center gap-1" title={info && !info.actions.includes(id) ? `It has no ${name.toLowerCase()} of its own: the closest plays` : undefined}>
            <input type="checkbox" className="size-3.5 accent-[var(--accent)]" checked={plays.includes(id)} disabled={id === 'idle'} onChange={(e) => toggle(id, e.target.checked)} />
            {name}
          </label>
        ))}
      </div>
      {!plays.includes('walk') && !plays.includes('run') && <span className="text-muted">Without walking or running it stays where it is.</span>}
      {c.plays != null && (
        <button type="button" className="self-start text-accent underline-offset-2 hover:underline" onClick={() => onChange({ plays: null })}>
          Use them all
        </button>
      )}
    </div>
  );
}

const ACTION_NAMES: Record<string, string> = { default: 'Its own way', idle: 'Stands', walk: 'Walks', run: 'Runs', sit: 'Sits', sleep: 'Sleeps', jump: 'Jumps', play: 'Plays', dance: 'Dances', worried: 'Looks worried', panic: 'Panics', tired: 'Looks tired', cheer: 'Cheers' };
const effectName = (id: string | null) => (id ? (EFFECTS.find(([e]) => e === id)?.[1] ?? id) : 'no effect');
const propName = (id: string | null) => (id ? (PROPS.find((p) => p.id === id)?.name ?? id) : 'no prop');

/** What a status is called for a kind (the bad way's statuses say what they mean). */
export function statusName(kind: ReadingKind | null, status: ReactionStatus): string {
  const down = kind ? KINDS[kind].bad === 'down' : false;
  const names: Record<ReactionStatus, string> = down
    ? { low: 'Low (poor)', normal: 'Normal', high: 'High (great)', critical: 'Critical (very low)' }
    : { low: 'Low (quiet)', normal: 'Normal', high: 'High', critical: 'Critical' };
  return names[status];
}

/**
 * A character's reactions to readings: which it reacts to (its readings in the order set here, the first that is not
 * normal; one of them; or none), what it is doing now, and the readings as items, one open at a time: every reading's
 * reactions (its general ones), and each reading's own over them, for each status what it does, the effect shown and
 * the prop it has (an ice pack on its head, a cone beside it).
 * The readings' order is their priority, the first first, when several are not normal at once.
 */
function Reactions({
  character: c,
  panel,
  sources,
  screen = false,
  onChange,
}: {
  character: OverlayCharacter;
  panel: OverlayPanel;
  sources: ReadonlyMap<string, OverlaySource>;
  screen?: boolean;
  onChange: (patch: Partial<OverlayCharacter>) => void;
}) {
  const made = useAllActions().filter(([id]) => !(id in ACTION_NAMES));
  const labelOf = (source: string) => panel.readings.find((r) => r.source === source)?.label || sources.get(source)?.label || source;
  const now = readingFor(c, panel, sources);
  // The item open: every reading's ('*') or one reading's (its source).
  const [open, setOpen] = useState<string | null>('*');
  const order = ordered(panel.readings, c.priority).map((r) => r.source);
  const move = (source: string, by: number) => {
    const next = [...order];
    const at = next.indexOf(source);
    const to = at + by;
    if (at < 0 || to < 0 || to >= next.length) return;
    [next[at], next[to]] = [next[to]!, next[at]!];
    onChange({ priority: next });
  };
  const tableFor = (source: string | null) => {
    const own = (source ? c.reactionsBy?.[source] : c.reactions) ?? {};
    const reading = source ? sources.get(source) : c.react && c.react !== 'auto' ? sources.get(c.react) : undefined;
    const kind: ReadingKind | null = reading ? kindOf(reading.id, reading.unit) : (now?.kind ?? null);
    const setOwn = (status: ReactionStatus, patch: { action?: string | null; effect?: string | null; prop?: string | null }) => {
      const next = { ...own, [status]: { ...own[status], ...patch } };
      for (const key of Object.keys(next) as ReactionStatus[]) {
        const r = next[key];
        if (r && (r.action ?? null) === null && (r.effect ?? null) === null && (r.prop ?? null) === null) delete next[key];
      }
      const table = Object.keys(next).length ? next : null;
      if (!source) {
        onChange({ reactions: table });
        return;
      }
      const by = { ...(c.reactionsBy ?? {}) };
      if (table) by[source] = table;
      else delete by[source];
      onChange({ reactionsBy: Object.keys(by).length ? by : null });
    };
    const nowStatus = now && (!source || now.source.id === source) ? now.status : null;
    return (
      <>
        <table className="w-full text-xs">
          <thead>
            <tr className="text-left text-muted">
              <th className="font-normal">When it is</th>
              <th className="font-normal">It</th>
              <th className="font-normal">With</th>
              <th className="font-normal">Prop</th>
            </tr>
          </thead>
          <tbody>
            {STATUSES.map((status) => {
              // Under one reading's own: what every reading's would do.
              const fallback = kind ? reactionFor(kind, status, source ? c.reactions : undefined) : null;
              const fallbackProp = kind ? propFor(kind, status, source ? c.reactions : undefined) : null;
              const mine = own[status];
              return (
                <tr key={status} aria-label={statusName(kind, status)} className={nowStatus === status ? 'font-medium text-foreground' : ''}>
                  <td className="py-0.5 pr-2">{statusName(kind, status)}</td>
                  <td className="py-0.5 pr-2">
                    <select className="h-7 w-full rounded border border-border bg-background px-1 text-xs" aria-label={`${statusName(kind, status)}: what it does`} value={mine?.action ?? ''} onChange={(e) => setOwn(status, { action: e.target.value || null })}>
                      <option value="">{fallback ? `${ACTION_NAMES[fallback.action] ?? fallback.action} (usual)` : 'As its reading’s kind'}</option>
                      {[...Object.entries(ACTION_NAMES), ...made].map(([id, name]) => (
                        <option key={id} value={id}>
                          {name}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td className="py-0.5">
                    <select className="h-7 w-full rounded border border-border bg-background px-1 text-xs" aria-label={`${statusName(kind, status)}: its effect`} value={mine?.effect ?? ''} onChange={(e) => setOwn(status, { effect: e.target.value || null })}>
                      <option value="">{fallback ? `${fallback.effect ? effectName(fallback.effect) : 'Nothing'} (usual)` : 'As its reading’s kind'}</option>
                      <option value="none">Nothing</option>
                      {EFFECTS.map(([id, name]) => (
                        <option key={id} value={id}>
                          {name}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td className="py-0.5 pl-1">
                    <select className="h-7 w-full rounded border border-border bg-background px-1 text-xs" aria-label={`${statusName(kind, status)}: its prop`} value={mine?.prop ?? ''} onChange={(e) => setOwn(status, { prop: e.target.value || null })}>
                      <option value="">{kind ? `${fallbackProp ? propName(fallbackProp) : 'Nothing'} (usual)` : 'As its reading’s kind'}</option>
                      <option value="none">Nothing</option>
                      {PROPS.map((p) => (
                        <option key={p.id} value={p.id}>
                          {p.name}
                        </option>
                      ))}
                    </select>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
        {kind && <p className="text-xs text-muted">{KINDS[kind].name}: the reading’s own warning and critical limits count when it has them.</p>}
      </>
    );
  };
  // Each reading's state now (what its table's row would do), so what it reacts to shows.
  const stateOf = (source: string) => {
    const s = sources.get(source);
    if (!s || s.value == null) return null;
    const kind = kindOf(s.id, s.unit);
    const status = statusOf(kind, panel.readings.find((r) => r.source === source), s.value, s.max);
    return { kind, status };
  };
  const valueOf = (source: string) => {
    const s = sources.get(source);
    return s?.value != null ? `${Math.round(s.value * 10) / 10}${s.unit ? ` ${s.unit}` : ''}` : (s?.text ?? '');
  };
  return (
    <section aria-label="Reacts to readings" className="flex flex-col gap-2 rounded-md bg-surface-muted/60 p-2">
      <label className="flex flex-col gap-1 text-xs text-muted">
        <span>Reacts to</span>
        <select className="h-8 w-full min-w-0 rounded-md border border-border bg-background px-2 text-xs text-foreground" value={c.react ?? ''} aria-label="Reacts to" onChange={(e) => onChange({ react: e.target.value || null })}>
          <option value="">Nothing (moves on its own)</option>
          <option value="auto">{screen ? "The screen's readings, in order" : "The panel's readings, in order"}</option>
          {panel.readings.map((r) => (
            <option key={r.source} value={r.source}>
              {labelOf(r.source)}
            </option>
          ))}
        </select>
      </label>
      {c.react && (
        <>
          <p className="text-xs text-muted" role="status">
            {now
              ? (() => {
                  const r = reactionFor(now.kind, now.status, reactionsFor(c, now.source.id));
                  const prop = propFor(now.kind, now.status, reactionsFor(c, now.source.id));
                  return `Now: ${labelOf(now.source.id)} ${now.source.value != null ? `${Math.round(now.source.value * 10) / 10}${now.source.unit ? ` ${now.source.unit}` : ''}` : ''}: ${statusName(now.kind, now.status).toLowerCase()}, so it ${(ACTION_NAMES[r.action] ?? r.action).toLowerCase()}${r.effect ? ` with ${effectName(r.effect).toLowerCase()}` : ''}${prop ? `${r.effect ? ' and' : ' with'} ${/^[aeiou]/i.test(propName(prop)) ? 'an' : 'a'} ${propName(prop).toLowerCase()}` : ''}.`;
                })()
              : 'Waiting for the reading’s value.'}
          </p>
          <ol aria-label="Its reactions" className="flex flex-col gap-1">
            <li className="rounded border border-border bg-background/40">
              <button type="button" className="flex w-full items-center gap-2 px-2 py-1.5 text-left text-xs" aria-expanded={open === '*'} onClick={() => setOpen(open === '*' ? null : '*')}>
                <ChevronDown className={cn('size-3.5 text-muted transition-transform', open === '*' && 'rotate-180')} aria-hidden />
                <span className="flex-1 font-medium">Every reading</span>
                <span className="text-muted">{Object.keys(c.reactions ?? {}).length ? `${Object.keys(c.reactions ?? {}).length} changed` : 'as usual'}</span>
              </button>
              {open === '*' && <div className="border-t border-border p-2">{tableFor(null)}</div>}
            </li>
            {order.map((source, i) => {
              const own = Object.keys(c.reactionsBy?.[source] ?? {}).length;
              const reacting = now?.source.id === source;
              const ignored = c.ignore?.includes(source) ?? false;
              return (
                <li key={source} aria-label={labelOf(source)} className={cn('rounded border bg-background/40', reacting ? 'border-accent/60' : 'border-border', ignored && 'opacity-60')}>
                  <div className="flex items-center gap-1 px-2 py-1">
                    {c.react === 'auto' && (
                      <input
                        type="checkbox"
                        className="size-3.5 shrink-0 accent-[var(--accent)]"
                        aria-label={`Reacts to ${labelOf(source)}`}
                        title={ignored ? 'Not considered: click to react to it' : 'Considered: click to leave it out'}
                        checked={!ignored}
                        onChange={(e) => {
                          const rest = (c.ignore ?? []).filter((x) => x !== source);
                          const next = e.target.checked ? rest : [...rest, source];
                          onChange({ ignore: next.length ? next : null });
                        }}
                      />
                    )}
                    <button type="button" className="flex min-w-0 flex-1 items-center gap-2 text-left text-xs" aria-expanded={open === source} onClick={() => setOpen(open === source ? null : source)}>
                      <ChevronDown className={cn('size-3.5 shrink-0 text-muted transition-transform', open === source && 'rotate-180')} aria-hidden />
                      {c.react === 'auto' && <span className="w-4 shrink-0 text-right tabular-nums text-muted">{i + 1}</span>}
                      <span className="min-w-0 flex-1 truncate font-medium">{labelOf(source)}</span>
                      <span className="shrink-0 tabular-nums text-muted">{valueOf(source)}</span>
                      {(() => {
                        const st = stateOf(source);
                        return st ? (
                          <span className={cn('shrink-0 rounded px-1 text-[10px]', st.status === 'normal' ? 'bg-surface-muted text-muted' : st.status === 'critical' ? 'bg-danger/15 text-danger' : 'bg-warning/15 text-warning')} data-status={st.status}>
                            {statusName(st.kind, st.status).replace(/ \(.*\)$/, '')}
                          </span>
                        ) : null;
                      })()}
                      {own > 0 && <span className="shrink-0 rounded bg-accent/15 px-1 text-[10px] text-accent">{own} own</span>}
                    </button>
                    {c.react === 'auto' && (
                      <>
                        <Button size="icon" variant="ghost" className="size-6" aria-label={`${labelOf(source)} first`} title="Put first (reacted to before the others)" disabled={i === 0} onClick={() => move(source, -1)}>
                          <ArrowUp aria-hidden />
                        </Button>
                        <Button size="icon" variant="ghost" className="size-6" aria-label={`${labelOf(source)} later`} title="Put later" disabled={i === order.length - 1} onClick={() => move(source, 1)}>
                          <ArrowDown aria-hidden />
                        </Button>
                      </>
                    )}
                  </div>
                  {open === source && (
                    <div className="border-t border-border p-2">
                      <p className="mb-1 text-xs text-muted">When it reacts to {labelOf(source)}; anything left as usual follows every reading’s.</p>
                      {tableFor(source)}
                    </div>
                  )}
                </li>
              );
            })}
          </ol>
          {c.react === 'auto' && order.length > 1 && <p className="text-xs text-muted">The first reading ticked in this order that is not normal is the one it reacts to; one not ticked is left out.</p>}
        </>
      )}
    </section>
  );
}

/**
 * The characters to choose from: those shipped and those imported (each moving while the mouse is on it), importing
 * one, and removing an imported one no panel has.
 */
export function CharacterPicker({ current, onClose, onPick }: { current: string | null; onClose: () => void; onPick: (id: string) => void }) {
  const list = useCharacters();
  const remove = useDeleteCharacter();
  const confirm = useConfirm();
  const all = list.data ?? [];
  const [group, setGroup] = useState<'shipped' | 'imported'>(current?.endsWith('.char') ? 'imported' : 'shipped');
  const [importing, setImporting] = useState(false);
  const [generating, setGenerating] = useState(false);
  const shown = all.filter((c) => c.shipped === (group === 'shipped'));
  if (importing) return <CharacterImport onClose={() => setImporting(false)} onImported={(id) => onPick(id)} />;
  if (generating) return <CharacterGenerate onClose={() => setGenerating(false)} />;
  return (
    <Dialog open title="Characters" description="Point at one to see it play; click to choose it." onClose={onClose} className="max-w-2xl">
      <div className="flex flex-col gap-3">
        <div role="tablist" aria-label="Kinds of character" className="flex flex-wrap gap-1 rounded-lg bg-surface-muted p-1 text-sm">
          {(
            [
              ['shipped', 'With PC Diagnostic'],
              ['imported', 'Imported'],
            ] as const
          ).map(([id, name]) => (
            <button key={id} type="button" role="tab" aria-selected={group === id} onClick={() => setGroup(id)} className={cn('flex-1 rounded-md px-3 py-1', group === id ? 'bg-surface font-medium shadow-sm' : 'text-muted hover:text-foreground')}>
              {name} <span className="text-xs text-muted">{all.filter((c) => c.shipped === (id === 'shipped')).length}</span>
            </button>
          ))}
        </div>
        {group === 'imported' && (
          <p className="text-xs text-muted">
            Characters you made or have the right to use, imported from their animations (GIF, WebP, APNG, video, PNG frames or sprite sheets). A Live2D model comes in by exporting its motions as GIF or WebM first.
          </p>
        )}
        {remove.error && (
          <p role="alert" className="text-xs text-danger">
            {remove.error.message}
          </p>
        )}
        <div role="list" aria-label="Characters to choose" className="grid grid-cols-3 gap-2 sm:grid-cols-4">
          {shown.map((c) => (
            <HoverCard key={c.id} info={c} current={c.id === current} onPick={() => onPick(c.id)} onRemove={c.shipped || c.used ? null : async () => (await confirm({ title: `Delete ${c.name}?`, description: 'It is deleted from the library, on this PC and in every storage location.' })) && remove.mutate(c.id)} />
          ))}
          {group === 'imported' && (
            <button type="button" role="listitem" onClick={() => setImporting(true)} className="flex min-h-28 flex-col items-center justify-center gap-1 rounded-lg border border-dashed border-border p-2 text-xs text-muted hover:border-accent/60 hover:text-foreground">
              <Upload aria-hidden className="size-5" />
              Import a character…
            </button>
          )}
          {group === 'imported' && (
            <button type="button" role="listitem" onClick={() => setGenerating(true)} className="flex min-h-28 flex-col items-center justify-center gap-1 rounded-lg border border-dashed border-border p-2 text-xs text-muted hover:border-accent/60 hover:text-foreground">
              <Sparkles aria-hidden className="size-5" />
              Generate a character…
            </button>
          )}
        </div>
      </div>
    </Dialog>
  );
}

function HoverCard({ info, current, onPick, onRemove }: { info: CharacterInfo; current: boolean; onPick: () => void; onRemove: (() => void) | null }) {
  const [hover, setHover] = useState(false);
  return (
    <div role="listitem" className="relative" onPointerEnter={() => setHover(true)} onPointerLeave={() => setHover(false)}>
      <button
        type="button"
        aria-label={info.name}
        aria-pressed={current}
        onClick={onPick}
        className={cn('flex w-full flex-col items-center gap-1 rounded-lg border bg-surface p-2 transition-colors', current ? 'border-accent ring-2 ring-accent/30' : 'border-border hover:border-accent/60')}
      >
        <CharacterThumb id={info.id} action={hover ? (info.actions.includes('play') ? 'play' : info.actions.includes('walk') ? 'walk' : 'idle') : 'idle'} size={72} moving={hover} />
        <span className="max-w-full truncate text-xs font-medium">{info.name}</span>
      </button>
      {onRemove && (
        <button type="button" aria-label={`Delete ${info.name}`} title="Delete from the library" onClick={onRemove} className="absolute right-1 top-1 rounded bg-surface/90 p-1 text-muted shadow-sm hover:text-danger">
          <Trash2 aria-hidden className="size-3.5" />
        </button>
      )}
    </div>
  );
}
