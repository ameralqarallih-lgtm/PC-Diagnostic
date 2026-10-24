import { ChevronDown, Play, Settings2, Sparkles, Square, Trash2 } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { Dialog } from '@/components/ui/dialog';
import { cn } from '@/lib/utils';
import { CharacterThumb } from './CharacterSprite';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import {
  GENERATOR_SECTION,
  isActive,
  showGeneratorSection,
  stillUrl,
  useForgetGeneration,
  useGenerations,
  useGenerator,
  useGeneratorStatus,
  useSaveGenerator,
  useStartGeneration,
  useStartGenerator,
  usePrerequisites,
  useRunSetup,
  useSetupRun,
  useStopGenerator,
  useTestGenerator,
  type GenerationJob,
} from './generation';

const ACTIONS: readonly (readonly [string, string])[] = [
  ['idle', 'Idle'],
  ['walk', 'Walk'],
  ['run', 'Run'],
  ['jump', 'Jump'],
  ['play', 'Play'],
  ['dance', 'Dance'],
  ['sit', 'Sit'],
  ['sleep', 'Sleep'],
];

/**
 * Generating a character from a prompt with an AI generator: set up once (the generator on this PC, or another
 * service), then a prompt makes four stills, the one chosen is animated, and the character lands in the library.
 * Each generation runs in the background, followed by PC Diagnostic, and is listed here with its progress.
 */
export function CharacterGenerate({ onClose }: { onClose: () => void }) {
  const generator = useGenerator();
  const off = generator.data?.kind === 'off';
  // The generator is set up, started and stopped in its own section of the On-screen page.
  const toSetup = () => {
    onClose();
    showGeneratorSection();
  };
  return (
    <Dialog open title="Generate a character" description="Describe a character; an AI model draws it and animates it. It carries on in the background." onClose={onClose} className="max-w-3xl">
      <div className="flex max-h-[72vh] flex-col gap-4 overflow-y-auto pr-1 text-sm">
        {generator.data && off ? (
          <section aria-label="No generator" className="flex flex-col items-start gap-2 rounded-md border border-border p-3">
            <p>No character generator is set up yet.</p>
            <Button size="sm" onClick={toSetup}>
              <Settings2 aria-hidden /> Set up the character generator
            </Button>
            <p className="text-xs text-muted">It is in “Character generator” on this page: on this PC, or another service.</p>
          </section>
        ) : (
          <>
            <GeneratorState onSetup={toSetup} />
            <PromptForm onSetup={toSetup} />
          </>
        )}
        <Generations />
      </div>
    </Dialog>
  );
}

/** The generator's state in one line: running or not, with Start on this PC (and the way to its section). */
export function GeneratorState({ onSetup }: { onSetup: () => void }) {
  const status = useGeneratorStatus();
  const start = useStartGenerator();
  if (!status.data) return null;
  return (
    <p className="flex flex-wrap items-center gap-2 text-xs" role="status">
      <span className={cn('size-2 rounded-full', status.data.running ? 'bg-success' : 'bg-warning')} aria-hidden />
      <span className="text-muted">{start.isPending ? 'Starting the generator…' : status.data.message}</span>
      {!status.data.running && status.data.canStart && (
        <Button size="sm" variant="outline" disabled={start.isPending} onClick={() => start.mutate()}>
          <Play aria-hidden /> Start it
        </Button>
      )}
      <Button size="sm" variant="ghost" onClick={onSetup}>
        Generator settings
      </Button>
    </p>
  );
}

/**
 * The character generator's section of the On-screen page: where characters are generated (this PC or another
 * service), set up once and used by "Generate a character"; and the generator on this PC started and stopped here.
 */
export function GeneratorCard() {
  const generator = useGenerator();
  const status = useGeneratorStatus();
  const start = useStartGenerator();
  const stop = useStopGenerator();
  const confirm = useConfirm();
  const busy = status.data?.busy ?? false;
  return (
    <Card id={GENERATOR_SECTION} className="scroll-mt-4">
      <CardHeader>
        <CardTitle className="flex flex-wrap items-center gap-2">
          <Sparkles className="size-4 text-accent" aria-hidden /> Character generator
          {status.data && (
            <span className="flex items-center gap-1.5 text-xs font-normal text-muted" role="status">
              <span className={cn('size-2 rounded-full', status.data.running ? 'bg-success' : 'bg-warning')} aria-hidden />
              {stop.isPending ? 'Stopping…' : start.isPending && !status.data.starting ? 'Starting…' : status.data.message}
            </span>
          )}
          {status.data?.canStart && (
            <span className="ml-auto flex gap-2">
              <Button size="sm" disabled={status.data.running || status.data.starting || start.isPending} onClick={() => start.mutate()}>
                <Play aria-hidden /> Start
              </Button>
              <Button
                size="sm"
                variant="outline"
                disabled={!(status.data.running || status.data.starting) || stop.isPending}
                onClick={async () => (!busy || (await confirm({ title: 'Stop the generator?', description: 'It is making a character: stopping gives that up.', action: 'Stop' }))) && stop.mutate()}
              >
                <Square aria-hidden /> Stop
              </Button>
            </span>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent className="text-sm">
        {start.data && !start.data.ok && <p className="mb-2 text-xs text-danger">{start.data.message}</p>}
        {generator.data && <GeneratorSetup />}
      </CardContent>
    </Card>
  );
}

function GeneratorSetup() {
  const generator = useGenerator();
  const save = useSaveGenerator();
  const test = useTestGenerator();
  const [kind, setKind] = useState<'off' | 'internal' | 'external'>(generator.data?.kind === 'off' ? 'internal' : (generator.data?.kind ?? 'internal'));
  const [url, setUrl] = useState(generator.data?.kind === 'external' ? (generator.data.url ?? '') : '');
  const [key, setKey] = useState('');
  const [python, setPython] = useState(generator.data?.python ?? '');
  const [folder, setFolder] = useState(generator.data?.folder ?? '');
  const [kit, setKit] = useState(generator.data?.kit ?? '');
  const fields = { python: useRef<HTMLInputElement>(null), kit: useRef<HTMLInputElement>(null), folder: useRef<HTMLInputElement>(null) };
  const choice = { kind, url: kind === 'external' ? url : null, key: key || null, python: python || null, folder: folder || null, kit: kit || null };
  return (
    <section aria-label="Generator setup" className="flex flex-col gap-3 rounded-md border border-border p-3">
      <h3 className="font-medium">Set up a character generator</h3>
      <div role="radiogroup" aria-label="Where characters are generated" className="flex flex-col gap-2 text-xs">
        {(
          [
            ['internal', 'On this PC (internal)', 'Runs the models on this PC: needs an NVIDIA GPU with 16 GB or more and about 45 GB of models.'],
            ['external', 'Another service (external)', 'A generator on another PC, a server or a cloud GPU that answers the PC Diagnostic generator API.'],
            ['off', 'Off', 'No generation; characters can still be imported.'],
          ] as const
        ).map(([id, label, help]) => (
          <label key={id} className={cn('flex cursor-pointer gap-2 rounded-md border p-2', kind === id ? 'border-accent' : 'border-border')}>
            <input type="radio" name="generator" checked={kind === id} onChange={() => setKind(id)} />
            <span>
              <span className="block font-medium text-foreground">{label}</span>
              <span className="text-muted">{help}</span>
            </span>
          </label>
        ))}
      </div>
      {kind === 'internal' && (
        <div className="flex flex-col gap-2 text-xs">
          <p className="text-muted">
            It runs the models on this PC. Go through the steps below: each says what it found; install or download what is missing, or use what this PC already has. Then Save and Test: PC Diagnostic starts the generator when it is not running, and it waits its turn when other programs share the GPU.
          </p>
          <Prerequisites onChoose={(what) => fields[what].current?.focus()} />
          <label className="flex items-center gap-2 text-muted">
            <span className="w-24 shrink-0">Python</span>
            <input ref={fields.python} value={python} onChange={(e) => setPython(e.target.value)} placeholder="…\kitgen\.venv\Scripts\python.exe" aria-label="Generator Python" className="h-8 flex-1 rounded-md border border-border bg-background px-2 font-mono text-foreground" />
          </label>
          <label className="flex items-center gap-2 text-muted">
            <span className="w-24 shrink-0">Folder</span>
            <input ref={fields.folder} value={folder} onChange={(e) => setFolder(e.target.value)} placeholder="…\tools\character-gen" aria-label="Generator folder" className="h-8 flex-1 rounded-md border border-border bg-background px-2 font-mono text-foreground" />
          </label>
          <label className="flex items-center gap-2 text-muted">
            <span className="w-24 shrink-0">Kit</span>
            <input ref={fields.kit} value={kit} onChange={(e) => setKit(e.target.value)} placeholder="The folder with hf\ (models) and models\rife426 (as settings.py names it)" aria-label="Generator kit" className="h-8 flex-1 rounded-md border border-border bg-background px-2 font-mono text-foreground" />
          </label>
          <p className="text-muted">Save to use them, then Check again.</p>
          <p className="text-muted">
            Or start it yourself from that folder: <code className="text-foreground">python serve.py --port 7865</code>
          </p>
        </div>
      )}
      {kind === 'external' && (
        <div className="flex flex-col gap-2 text-xs">
          <label className="flex items-center gap-2 text-muted">
            <span className="w-24 shrink-0">Address</span>
            <input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://generator.example:7865" aria-label="Generator address" className="h-8 flex-1 rounded-md border border-border bg-background px-2 text-foreground" />
          </label>
          <label className="flex items-center gap-2 text-muted">
            <span className="w-24 shrink-0">Key</span>
            <input type="password" value={key} onChange={(e) => setKey(e.target.value)} placeholder={generator.data?.hasKey ? 'Saved (leave empty to keep it)' : 'If the service needs one'} aria-label="Generator key" className="h-8 flex-1 rounded-md border border-border bg-background px-2 text-foreground" />
          </label>
          <p className="text-muted">
            On another PC, run <code>python serve.py --host 0.0.0.0 --key YOUR-SECRET</code> from <code>tools\character-gen</code>, or use any service that answers the same API (documented in serve.py).
          </p>
        </div>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" onClick={() => save.mutate(choice, { onSuccess: () => kind !== 'off' && test.mutate(undefined) })} disabled={save.isPending}>
          Save
        </Button>
        {/* The choice on the page, saved or not. */}
        <Button size="sm" variant="outline" onClick={() => test.mutate(choice)} disabled={kind === 'off' || test.isPending}>
          {test.isPending ? (kind === 'internal' ? 'Starting and testing…' : 'Testing…') : 'Test'}
        </Button>

        <span role="status" className={cn('text-xs', test.data?.ok ? 'text-success' : 'text-danger')}>
          {save.error?.message ?? test.data?.message ?? ''}
        </span>
      </div>
    </section>
  );
}

export function PromptForm({ onSetup: _onSetup }: { onSetup: () => void }) {
  const start = useStartGeneration();
  const [prompt, setPrompt] = useState('');
  const [kind, setKind] = useState<'person' | 'animal'>('person');
  return (
    <section aria-label="A new character" className="flex flex-col gap-2">
      <label className="flex flex-col gap-1 text-xs text-muted">
        Describe it (an original character: the look, hair, clothes, colours)
        <textarea value={prompt} maxLength={400} rows={3} onChange={(e) => setPrompt(e.target.value)} placeholder="1girl, silver hair, ponytail, green jacket, headphones, cheerful" aria-label="Describe the character" className="rounded-md border border-border bg-background p-2 text-sm text-foreground" />
      </label>
      <div className="flex flex-wrap items-center gap-3 text-xs">
        <span className="text-muted">It is</span>
        {(['person', 'animal'] as const).map((k) => (
          <label key={k} className="flex items-center gap-1">
            <input type="radio" name="kind" checked={kind === k} onChange={() => setKind(k)} /> {k === 'person' ? 'A person' : 'An animal'}
          </label>
        ))}
        <span className="flex-1" />
        <Button size="sm" disabled={!prompt.trim() || start.isPending} onClick={() => start.mutate({ type: 'stills', prompt, kind }, { onSuccess: () => setPrompt('') })}>
          <Sparkles aria-hidden /> Draw it
        </Button>
      </div>
      {start.error && (
        <p role="alert" className="text-xs text-danger">
          {start.error.message}
        </p>
      )}
      <p className="text-xs text-muted">
        Four drafts are drawn; choose one and it is animated. Several can be asked for: they wait their turn and are each followed below (open one to see how it goes). Only describe characters you may use: not ones from existing series.
      </p>
    </section>
  );
}

/** A character being made: its drafts (a stills generation) and each animation of a draft (character generations). */
interface CharacterGroup {
  id: string;
  drafts: GenerationJob | null;
  animations: GenerationJob[];
}

/** The generations as characters: each draw with the animations made from it, newest first. */
export function groupGenerations(jobs: readonly GenerationJob[]): CharacterGroup[] {
  const groups = new Map<string, CharacterGroup>();
  for (const j of jobs.filter((x) => x.type === 'stills')) groups.set(j.id, { id: j.id, drafts: j, animations: [] });
  for (const j of jobs.filter((x) => x.type === 'character')) {
    const group = (j.from && groups.get(j.from)) || null;
    if (group) group.animations.push(j);
    else groups.set(j.id, { id: j.id, drafts: null, animations: [j] });
  }
  const newest = (g: CharacterGroup) => Math.max(...[g.drafts, ...g.animations].filter((x): x is GenerationJob => !!x).map((x) => Date.parse(x.created)));
  for (const g of groups.values()) g.animations.sort((a, b) => Date.parse(a.created) - Date.parse(b.created));
  return [...groups.values()].sort((a, b) => newest(b) - newest(a));
}

/** Where a character being made is: its stage, how far, and whether it waits for a choice. */
function stageOf(g: CharacterGroup): { text: string; progress: number | null; tone: 'active' | 'choose' | 'done' | 'failed' } {
  const last = g.animations.at(-1);
  if (last) {
    if (last.state === 'failed') return { text: `Animating failed`, progress: null, tone: 'failed' };
    if (last.state === 'done') return { text: 'In the library', progress: null, tone: 'done' };
    return { text: last.state === 'queued' ? 'Animating: waiting its turn' : `Animating: ${last.step}`, progress: last.progress, tone: 'active' };
  }
  const d = g.drafts!;
  if (d.state === 'failed') return { text: 'Drawing failed', progress: null, tone: 'failed' };
  if (d.state === 'done') return { text: 'Choose a draft', progress: null, tone: 'choose' };
  return { text: d.state === 'queued' ? 'Drafts: waiting their turn' : `Drawing drafts: ${d.step}`, progress: d.progress, tone: 'active' };
}

/**
 * The characters being made, each a group (its name, its stage, how far it is), opened one at a time: inside, its
 * stages, each opened to follow it: the drafts (drawn, then chosen from) and each animation of a draft (its steps and
 * the generator's output, then the character in the library). Animating a draft closes the drafts and opens its
 * animation.
 */
export function Generations({ compact = false }: { compact?: boolean }) {
  const jobs = useGenerations().data ?? [];
  const forget = useForgetGeneration();
  const confirm = useConfirm();
  const groups = groupGenerations(jobs);
  const shown = compact ? groups.filter((g) => [g.drafts, ...g.animations].some((j) => j && isActive(j))) : groups.slice(0, 12);
  // The group open and the stage open in it: by default the newest that is under way or waits for a choice.
  const [openGroup, setOpenGroup] = useState<string | null | undefined>(undefined);
  const [openStage, setOpenStage] = useState<string | null | undefined>(undefined);
  const firstBusy = groups.find((g) => ['active', 'choose'].includes(stageOf(g).tone));
  const groupOpen = openGroup === undefined ? (firstBusy?.id ?? null) : openGroup;
  if (!shown.length) return compact ? null : <p className="text-xs text-muted">No characters being made yet.</p>;
  return (
    <section aria-label="Characters being made" className="flex flex-col gap-2">
      <h3 className="text-xs font-medium text-muted">{compact ? 'Being made' : 'Characters being made'}</h3>
      <ol className="flex flex-col gap-2">
        {shown.map((g) => {
          const stage = stageOf(g);
          const name = g.animations.at(-1)?.name ?? 'New character';
          const prompt = (g.drafts ?? g.animations[0]!).prompt;
          const open = groupOpen === g.id && !compact;
          const stages = [...(g.drafts ? [g.drafts] : []), ...g.animations];
          const lastStage = g.animations.at(-1)?.id ?? g.drafts?.id ?? null;
          const stageOpen = openStage === undefined || (openStage !== null && !stages.some((s) => s.id === openStage)) ? lastStage : openStage;
          const busy = stages.some(isActive);
          return (
            <li key={g.id} aria-label={`${name}: ${stage.text}`} className={cn('rounded-md border text-xs transition-colors', open ? 'border-accent/50 bg-surface-muted/30' : 'border-border')}>
              <div className="flex items-center gap-2 p-2">
                <button
                  type="button"
                  className="flex min-w-0 flex-1 items-center gap-2 text-left"
                  aria-expanded={open}
                  onClick={() => {
                    setOpenGroup(open ? null : g.id);
                    setOpenStage(undefined);
                  }}
                >
                  <ChevronDown className={cn('size-3.5 shrink-0 text-muted transition-transform duration-200', open && 'rotate-180')} aria-hidden />
                  {g.animations.find((a) => a.state === 'done' && a.character) ? (
                    <CharacterThumb id={g.animations.find((a) => a.state === 'done' && a.character)!.character!} size={28} moving={false} />
                  ) : g.drafts?.state === 'done' && g.drafts.stills > 0 ? (
                    <img src={stillUrl(g.drafts, 0)} alt="" className="size-7 shrink-0 rounded bg-white object-contain" />
                  ) : (
                    <span className="flex size-7 shrink-0 items-center justify-center rounded bg-surface-muted">
                      <Sparkles className={cn('size-3.5 text-accent', busy && 'animate-pulse')} aria-hidden />
                    </span>
                  )}
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-medium">{name}</span>
                    <span className="block truncate text-muted">{prompt}</span>
                  </span>
                  <span
                    className={cn(
                      'shrink-0 rounded px-1.5 py-0.5 text-[10px]',
                      stage.tone === 'failed' ? 'bg-danger/15 text-danger' : stage.tone === 'done' ? 'bg-success/15 text-success' : stage.tone === 'choose' ? 'bg-accent/15 text-accent' : 'bg-surface-muted text-muted',
                    )}
                  >
                    {stage.text}
                    {stage.progress !== null && ` ${Math.round(stage.progress * 100)} %`}
                  </span>
                </button>
                {!compact && !busy && (
                  <Button size="icon" variant="ghost" aria-label={`Take ${name} off the list`} title="Take off the list" onClick={async () => (await confirm({ title: `Take ${name} off the list?`, description: 'Its drafts and progress are deleted; a character already in the library stays there.', action: 'Take off' })) && stages.forEach((s) => forget.mutate(s.id))}>
                    <Trash2 aria-hidden />
                  </Button>
                )}
              </div>
              {stage.progress !== null && (
                <div className="mx-2 mb-2 h-1 overflow-hidden rounded bg-surface-muted" role="progressbar" aria-valuenow={Math.round(stage.progress * 100)} aria-valuemin={0} aria-valuemax={100}>
                  <div className="h-full bg-accent transition-all duration-500" style={{ width: `${Math.max(3, stage.progress * 100)}%` }} />
                </div>
              )}
              {open && (
                <ol className="flex flex-col gap-1.5 border-t border-border p-2" aria-label="Its stages">
                  {stages.map((s) => {
                    const sOpen = stageOpen === s.id;
                    const title = s.type === 'stills' ? 'Drafts' : `Animating ${s.name ?? 'it'}`;
                    const state = s.state === 'failed' ? 'Failed' : s.state === 'done' ? (s.type === 'stills' ? `${s.stills} drawn` : 'In the library') : s.state === 'queued' ? 'Waiting its turn' : `${s.step} ${Math.round(s.progress * 100)} %`;
                    return (
                      <li key={s.id} aria-label={`${title}: ${state}`} className="rounded border border-border bg-background/40">
                        <button type="button" className="flex w-full items-center gap-2 px-2 py-1.5 text-left" aria-expanded={sOpen} onClick={() => setOpenStage(sOpen ? null : s.id)}>
                          <ChevronDown className={cn('size-3 shrink-0 text-muted transition-transform duration-200', sOpen && 'rotate-180')} aria-hidden />
                          <span className="flex-1 font-medium">{title}</span>
                          <span className={cn(s.state === 'failed' ? 'text-danger' : s.state === 'done' ? 'text-success' : 'text-muted')}>{state}</span>
                        </button>
                        {sOpen && (
                          <div className="flex flex-col gap-1.5 border-t border-border p-2 motion-safe:animate-[fadein_160ms_ease-out]">
                            {s.error && <p className="whitespace-pre-wrap text-danger">{s.error}</p>}
                            {s.type === 'stills' && s.state === 'done' ? (
                              <ChooseStill
                                job={s}
                                onAnimated={(job) => {
                                  // The drafts close; the new animation opens.
                                  setOpenGroup(g.id);
                                  setOpenStage(job.id);
                                }}
                              />
                            ) : s.type === 'character' && s.state === 'done' && s.character ? (
                              <div className="flex items-center gap-2">
                                <CharacterThumb id={s.character} size={56} />
                                <span className="text-muted">{s.name} is in the library: add it to a panel or a screen from Characters.</span>
                              </div>
                            ) : null}
                            {isActive(s) || s.state === 'failed' ? (
                              s.log?.length ? (
                                <pre className="max-h-48 overflow-y-auto whitespace-pre-wrap break-all rounded bg-background p-2 font-mono text-[10px] text-muted" aria-label="The generator's output">
                                  {s.log.join('\n')}
                                </pre>
                              ) : (
                                <p className="text-muted">{s.state === 'queued' ? 'Waiting its turn (the generator makes one at a time, and waits for the GPU).' : 'Nothing said yet.'}</p>
                              )
                            ) : null}
                            <p className="text-[10px] text-muted">Started {new Date(s.created).toLocaleString()}</p>
                          </div>
                        )}
                      </li>
                    );
                  })}
                </ol>
              )}
            </li>
          );
        })}
      </ol>
    </section>
  );
}

function ChooseStill({ job, onAnimated }: { job: GenerationJob; onAnimated?: (animation: GenerationJob) => void }) {
  const start = useStartGeneration();
  const [still, setStill] = useState<number | null>(null);
  const [name, setName] = useState('');
  const [faces, setFaces] = useState<1 | -1>(1);
  const [actions, setActions] = useState<string[]>(['idle', 'walk', 'run', 'jump', 'play', 'sit', 'sleep']);
  return (
    <div className="mt-2 flex flex-col gap-2">
      <div role="radiogroup" aria-label="Choose a draft" className="grid grid-cols-4 gap-2">
        {Array.from({ length: job.stills }, (_, n) => (
          <button key={n} type="button" role="radio" aria-checked={still === n} aria-label={`Draft ${n + 1}`} onClick={() => setStill(n)} className={cn('overflow-hidden rounded border bg-white', still === n ? 'border-accent ring-2 ring-accent/40' : 'border-border')}>
            <img src={stillUrl(job, n)} alt="" className="aspect-[3/4] w-full object-contain" />
          </button>
        ))}
      </div>
      {still !== null && (
        <div className="flex flex-col gap-2">
          <div className="flex flex-wrap items-center gap-2">
            <input value={name} maxLength={40} onChange={(e) => setName(e.target.value)} placeholder="Its name" aria-label="Its name" className="h-8 w-40 rounded-md border border-border bg-background px-2" />
            <label className="flex items-center gap-1">
              <input type="checkbox" checked={faces === -1} onChange={(e) => setFaces(e.target.checked ? -1 : 1)} /> It looks left
            </label>
          </div>
          <div className="flex flex-wrap gap-2" aria-label="What it does">
            {ACTIONS.map(([id, label]) => (
              <label key={id} className="flex items-center gap-1">
                <input type="checkbox" disabled={id === 'idle'} checked={actions.includes(id)} onChange={(e) => setActions(e.target.checked ? [...actions, id] : actions.filter((a) => a !== id))} /> {label}
              </label>
            ))}
          </div>
          <Button size="sm" className="self-start" disabled={start.isPending} onClick={() => start.mutate({ type: 'character', from: job.id, still, name: name.trim() || 'Character', actions, faces }, { onSuccess: (animation) => animation && onAnimated?.(animation) })}>
            <Sparkles aria-hidden /> Animate it
          </Button>
          {start.error && <p className="text-danger">{start.error.message}</p>}
        </div>
      )}
    </div>
  );
}

/** A setup step's actions as buttons: what each does (installing, downloading, or using what this PC has). */
const SETUP_ACTIONS: Record<string, { label: string; help: string }> = {
  venv: { label: 'Create a Python environment', help: 'Python 3.12 in an environment of its own (with uv, or the Python launcher)' },
  packages: { label: 'Install the packages', help: 'PyTorch with CUDA, diffusers, rembg, OpenCV… (a few GB)' },
  models: { label: 'Download the models', help: 'Animagine XL 4.0, Wan 2.2 I2V A14B and its Lightning LoRA: about 45 GB into the kit' },
  cutout: { label: 'Download it', help: 'isnet-anime, about 170 MB' },
  ffmpeg: { label: 'Install ffmpeg', help: 'with winget' },
  node: { label: 'Install Node.js', help: 'the LTS, with winget' },
};

/**
 * The generator's prerequisites on this PC, one step after another: each checked (what was found, or what is missing),
 * with what can be done about it: install or download it, or use what this PC already has (a Python, a kit folder with
 * the models). What runs is followed below its step.
 */
function Prerequisites({ onChoose }: { onChoose: (what: 'python' | 'kit' | 'folder') => void }) {
  const steps = usePrerequisites();
  const run = useSetupRun(steps.data?.running?.state === 'running');
  const start = useRunSetup();
  const confirm = useConfirm();
  const current = run.data ?? steps.data?.running ?? null;
  const checkAgain = () => void steps.refetch();
  // A step finished: checked again.
  const finished = current?.state;
  useEffect(() => {
    if (finished === 'done' || finished === 'failed') void steps.refetch();
  }, [finished]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <section aria-label="Prerequisites" className="flex flex-col gap-2 rounded-md border border-border p-2 text-xs">
      <div className="flex items-center gap-2">
        <span className="font-medium">What it needs, step by step</span>
        {steps.data?.kit && <span className="truncate text-muted">kit: {steps.data.kit}</span>}
        <Button size="sm" variant="ghost" className="ml-auto" onClick={checkAgain} disabled={steps.isFetching}>
          {steps.isFetching ? 'Checking…' : 'Check again'}
        </Button>
      </div>
      {!steps.data && <p className="text-muted">Checking this PC…</p>}
      <ol className="flex flex-col gap-1.5">
        {(steps.data?.steps ?? []).map((s, i) => (
          <li key={s.id} aria-label={s.title} className="flex flex-col gap-1 rounded bg-surface-muted/50 p-2">
            <div className="flex items-start gap-2">
              <span className={cn('mt-0.5 flex size-4 shrink-0 items-center justify-center rounded-full text-[10px] font-bold', s.ok ? (s.warn ? 'bg-warning/20 text-warning' : 'bg-success/20 text-success') : 'bg-danger/15 text-danger')} aria-hidden>
                {s.ok ? (s.warn ? '!' : '✓') : i + 1}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block font-medium">{s.title}</span>
                <span className="block break-all text-muted" data-status={s.ok ? 'ok' : 'missing'}>
                  {s.detail}
                </span>
              </span>
            </div>
            {s.actions.length > 0 && (
              <div className="flex flex-wrap gap-1.5 pl-6">
                {s.actions.map((a) =>
                  a === 'choose' || a === 'python' ? (
                    <Button key={a} size="sm" variant="outline" onClick={() => onChoose('python')}>
                      Use a Python on this PC…
                    </Button>
                  ) : a === 'kit' ? (
                    <Button key={a} size="sm" variant="outline" onClick={() => onChoose('kit')}>
                      Use a kit folder on this PC…
                    </Button>
                  ) : a === 'folder' ? (
                    <Button key={a} size="sm" variant="outline" onClick={() => onChoose('folder')}>
                      Choose the generator's folder…
                    </Button>
                  ) : SETUP_ACTIONS[a] ? (
                    <Button
                      key={a}
                      size="sm"
                      variant={s.ok ? 'ghost' : 'default'}
                      title={SETUP_ACTIONS[a].help}
                      disabled={current?.state === 'running' || start.isPending}
                      onClick={async () => (a !== 'models' || (await confirm({ title: 'Download the models?', description: 'About 45 GB are downloaded into the kit folder.', action: 'Download', danger: false }))) && start.mutate(a)}
                    >
                      {s.ok ? `${SETUP_ACTIONS[a].label} again` : SETUP_ACTIONS[a].label}
                    </Button>
                  ) : null,
                )}
              </div>
            )}
          </li>
        ))}
      </ol>
      {start.error && <p className="text-danger">{(start.error as Error).message}</p>}
      {current && (
        <div aria-label="Setting up" className="flex flex-col gap-1">
          <p>
            <span className="font-medium">{SETUP_ACTIONS[current.action]?.label ?? current.action}</span>:{' '}
            <span className={cn(current.state === 'failed' ? 'text-danger' : current.state === 'done' ? 'text-success' : 'text-muted')}>{current.state === 'running' ? 'under way…' : current.state}</span>
          </p>
          {current.log.length > 0 && <pre className="max-h-40 overflow-y-auto whitespace-pre-wrap break-all rounded bg-background p-2 font-mono text-[10px] text-muted">{current.log.slice(-60).join('\n')}</pre>}
        </div>
      )}
    </section>
  );
}
