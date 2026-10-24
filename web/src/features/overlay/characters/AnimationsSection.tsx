import { Pencil, Power, RefreshCw, Sparkles, Trash2, Wand2 } from 'lucide-react';
import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import type { AnimationClip, CharacterAnimation } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { EMOTION_IDS, isEmotion, progressOf, useAnimations, useEditAnimation, useMakeAnimation, useMakeEmotions, useRegenerate, useRemoveAnimation, useRemoveClip, useRetryAnimation, useSetActive } from './animations';
import { CharacterThumb } from './CharacterSprite';
import { useCharacters } from './library';

type Kind = 'animation' | 'emotion';

const WORDS: Record<Kind, { title: string; intro: string; name: string; prompt: string; make: string; none: string }> = {
  animation: {
    title: 'Animations',
    intro: 'Something every character can do, made from what you describe; it is listed with what characters do and how they react. Each character is animated from its own pose.',
    name: 'Wave hello',
    prompt: 'The character waves one hand hello, smiling, standing in place.',
    make: 'Make it for every character',
    none: 'No animation yet.',
  },
  emotion: {
    title: 'Emotions',
    intro: 'A face and a mood for every character, made from what you describe. The reactions use worried, panic, tired and cheer; your own emotions are listed with what characters do and how they react.',
    name: 'Angry',
    prompt: 'The character is angry: a deep frown, gritted teeth, its fists clenched, stamping in place, no smile.',
    make: 'Make it for every character',
    none: 'No emotion yet.',
  },
};

const STATE: Record<AnimationClip['state'] | 'none', { text: string; tone: string }> = {
  done: { text: 'Made', tone: 'text-success' },
  running: { text: 'Making', tone: 'text-accent' },
  queued: { text: 'Waiting', tone: 'text-muted' },
  failed: { text: 'Failed', tone: 'text-danger' },
  none: { text: 'Not made', tone: 'text-muted' },
};

/**
 * New animations or emotions made by the character generator from a prompt, for every character (the generator's
 * page, a tab each): a new one asked for, and each with its characters as previews (each playing it once made), each
 * character's clip made, made again, turned on or off or removed, and the whole of it renamed, described again, made
 * again for everyone, turned on or off or removed.
 */
export function AnimationsSection({ kind = 'animation' }: { kind?: Kind }) {
  const animations = useAnimations();
  const make = useMakeAnimation();
  const emotions = useMakeEmotions();
  const words = WORDS[kind];
  const [name, setName] = useState('');
  const [prompt, setPrompt] = useState('');
  const all = animations.data?.animations ?? [];
  const list = all.filter((a) => (kind === 'emotion') === isEmotion(a));
  const characters = animations.data?.characters ?? [];
  const builtInsMade = EMOTION_IDS.every((id) => {
    const a = all.find((x) => x.id === id);
    return a && characters.every((c) => a.clips[c]);
  });
  return (
    <section aria-label={words.title} className="flex flex-col gap-3">
      <p className="text-xs text-muted">{words.intro}</p>
      {kind === 'emotion' && (
        <div className="flex flex-wrap items-center gap-2 rounded-md border border-border p-2 text-xs">
          <span className="flex-1">
            <span className="font-medium">The reactions' emotions</span>: worried and tired when a reading is bad, panic when it is critical, cheer when it is good again. Until a character has one it does the closest it has.
          </span>
          <Button size="sm" variant="outline" disabled={builtInsMade || emotions.isPending} onClick={() => emotions.mutate(undefined)}>
            <Sparkles aria-hidden /> {builtInsMade ? 'Made for every character' : 'Make them'}
          </Button>
        </div>
      )}
      <div className="flex flex-col gap-2">
        <label className="flex flex-col gap-1 text-xs text-muted">
          Name
          <input value={name} maxLength={40} onChange={(e) => setName(e.target.value)} placeholder={words.name} aria-label={`The new ${kind}'s name`} className="h-8 rounded-md border border-border bg-background px-2 text-sm text-foreground" />
        </label>
        <label className="flex flex-col gap-1 text-xs text-muted">
          {kind === 'emotion' ? 'How it looks and feels' : 'What it does'}
          <textarea value={prompt} maxLength={300} rows={2} onChange={(e) => setPrompt(e.target.value)} placeholder={words.prompt} aria-label={`Describe the new ${kind}`} className="rounded-md border border-border bg-background p-2 text-sm text-foreground" />
        </label>
        <div className="flex items-center gap-2">
          <span className="flex-1 text-xs text-muted">Made for {characters.length} characters.</span>
          <Button
            size="sm"
            disabled={!name.trim() || prompt.trim().length < 3 || make.isPending}
            onClick={() =>
              make.mutate(
                { name, prompt, kind },
                {
                  onSuccess: () => {
                    setName('');
                    setPrompt('');
                  },
                },
              )
            }
          >
            <Wand2 aria-hidden /> {words.make}
          </Button>
        </div>
        {(make.error || emotions.error) && (
          <p role="alert" className="text-xs text-danger">
            {(make.error ?? emotions.error)?.message}
          </p>
        )}
      </div>
      {list.length === 0 ? (
        <p className="text-xs text-muted">
          {words.none}
          {all.length > 0 && ` ${all.length} ${kind === 'emotion' ? `animation${all.length === 1 ? ' is' : 's are'} on the Animations tab` : `emotion${all.length === 1 ? ' is' : 's are'} on the Emotions tab`}.`}
        </p>
      ) : (
        <ul aria-label={`The ${kind === 'emotion' ? 'emotions' : 'animations'}`} className="flex flex-col gap-3">
          {[...list].reverse().map((a) => (
            <AnimationItem key={a.id} animation={a} characters={characters} />
          ))}
        </ul>
      )}
    </section>
  );
}

function AnimationItem({ animation: a, characters }: { animation: CharacterAnimation; characters: readonly string[] }) {
  const regenerate = useRegenerate();
  const setActive = useSetActive();
  const remove = useRemoveAnimation();
  const retry = useRetryAnimation();
  const editing = useEditAnimation();
  const confirm = useConfirm();
  const [edit, setEdit] = useState<{ name: string; prompt: string } | null>(null);
  const p = progressOf(a);
  const on = a.active !== false;
  // Every character there is now, and any it was made for that is gone since.
  const shown = [...new Set([...characters, ...Object.keys(a.clips)])];
  return (
    <li className={cn('flex flex-col gap-2 rounded-md border border-border p-2 text-xs', !on && 'opacity-70')} aria-label={a.name}>
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="text-sm font-medium">{a.name}</span>
        <span className="text-muted">{a.id}</span>
        {!on && <span className="rounded bg-surface-muted px-1.5 text-muted">Off</span>}
        <span className="flex-1" />
        <span className="tabular-nums text-muted">
          {p.done} of {shown.length} made{p.failed ? ` · ${p.failed} failed` : ''}
          {p.busy ? ` · ${p.busy} under way` : ''}
        </span>
        {(p.failed > 0 || characters.some((c) => !a.clips[c])) && (
          <Button size="sm" variant="ghost" aria-label={`Make ${a.name} where it failed or is missing`} onClick={() => retry.mutate(a.id)}>
            <Wand2 aria-hidden /> Make the rest
          </Button>
        )}
        <Button size="icon" variant="ghost" aria-label={on ? `Turn ${a.name} off` : `Turn ${a.name} on`} title={on ? 'Off: the characters do the closest they have (its clips kept)' : 'On'} onClick={() => setActive.mutate({ id: a.id, active: !on })}>
          <Power aria-hidden className={on ? 'text-success' : 'text-muted'} />
        </Button>
        <Button size="icon" variant="ghost" aria-label={`Edit ${a.name}`} onClick={() => setEdit(edit ? null : { name: a.name, prompt: a.prompt })}>
          <Pencil aria-hidden />
        </Button>
        <Button
          size="icon"
          variant="ghost"
          aria-label={`Make ${a.name} again for every character`}
          title="Again for every character (each plays its last clip until the new one is made)"
          onClick={async () => {
            if (await confirm({ title: `Make ${a.name} again for every character?`, description: 'Each character keeps playing its clip until the new one is made.', action: 'Make again', danger: false })) regenerate.mutate({ id: a.id });
          }}
        >
          <RefreshCw aria-hidden />
        </Button>
        <Button
          size="icon"
          variant="ghost"
          aria-label={`Remove ${a.name}`}
          onClick={async () => {
            if (await confirm({ title: `Remove ${a.name}?`, description: 'Its clips are removed; characters set to do it do the closest they have.', action: 'Remove' })) remove.mutate(a.id);
          }}
        >
          <Trash2 aria-hidden />
        </Button>
      </div>
      {edit ? (
        <div className="flex flex-col gap-1.5">
          <input value={edit.name} maxLength={40} onChange={(e) => setEdit({ ...edit, name: e.target.value })} aria-label="Its name" className="h-8 rounded-md border border-border bg-background px-2 text-sm text-foreground" />
          <textarea value={edit.prompt} maxLength={300} rows={2} onChange={(e) => setEdit({ ...edit, prompt: e.target.value })} aria-label="Its description" className="rounded-md border border-border bg-background p-2 text-sm text-foreground" />
          <div className="flex gap-2">
            <Button size="sm" variant="outline" disabled={editing.isPending} onClick={() => editing.mutate({ id: a.id, name: edit.name, prompt: edit.prompt }, { onSuccess: () => setEdit(null) })}>
              Save
            </Button>
            <Button
              size="sm"
              disabled={editing.isPending}
              onClick={() =>
                editing.mutate(
                  { id: a.id, name: edit.name, prompt: edit.prompt },
                  {
                    onSuccess: () => {
                      setEdit(null);
                      regenerate.mutate({ id: a.id });
                    },
                  },
                )
              }
            >
              Save and make it again
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setEdit(null)}>
              Cancel
            </Button>
          </div>
        </div>
      ) : (
        <p className="text-muted">{a.prompt}</p>
      )}
      <ul aria-label={`${a.name}: each character`} className="grid grid-cols-[repeat(auto-fill,minmax(7.5rem,1fr))] gap-2">
        {shown.map((c) => (
          <CharacterClip key={c} animation={a} character={c} />
        ))}
      </ul>
    </li>
  );
}

/** A character's clip of an animation: its preview (playing it once made and on), how it is coming along, and what can be done with it. */
function CharacterClip({ animation: a, character: id }: { animation: CharacterAnimation; character: string }) {
  const name = (useCharacters().data ?? []).find((c) => c.id === id)?.name ?? id;
  const regenerate = useRegenerate();
  const setActive = useSetActive();
  const removeClip = useRemoveClip();
  const confirm = useConfirm();
  const clip = a.clips[id];
  const state = STATE[clip?.state ?? 'none'];
  const on = clip?.active !== false;
  const plays = !!clip?.file && on && a.active !== false;
  return (
    <li aria-label={`${name}: ${state.text.toLowerCase()}`} className={cn('flex flex-col items-center gap-1 rounded-md border border-border p-1.5', !on && 'opacity-60')} title={clip?.error ?? clip?.step ?? undefined}>
      <span className="relative">
        <CharacterThumb id={id} action={plays ? a.id : 'idle'} size={72} />
        {clip?.state === 'running' && clip.progress ? <span className="absolute inset-x-0 bottom-0 rounded bg-surface/80 text-center text-[10px] tabular-nums">{Math.round(clip.progress * 100)} %</span> : null}
      </span>
      <span className="max-w-full truncate font-medium">{name}</span>
      <span className={cn('text-[11px]', state.tone)}>
        {state.text}
        {!on ? ' · off' : ''}
      </span>
      <span className="flex gap-0.5">
        <Button size="icon" variant="ghost" className="size-6" aria-label={clip ? `Make ${a.name} again for ${name}` : `Make ${a.name} for ${name}`} title={clip ? 'Make again (the last one plays meanwhile)' : 'Make it'} disabled={clip?.state === 'queued' || clip?.state === 'running'} onClick={() => regenerate.mutate({ id: a.id, character: id })}>
          {clip ? <RefreshCw aria-hidden className="size-3.5" /> : <Wand2 aria-hidden className="size-3.5" />}
        </Button>
        {clip && (
          <Button size="icon" variant="ghost" className="size-6" aria-label={on ? `Turn ${a.name} off for ${name}` : `Turn ${a.name} on for ${name}`} onClick={() => setActive.mutate({ id: a.id, character: id, active: !on })}>
            <Power aria-hidden className={cn('size-3.5', on ? 'text-success' : 'text-muted')} />
          </Button>
        )}
        {clip && (
          <Button
            size="icon"
            variant="ghost"
            className="size-6"
            aria-label={`Remove ${a.name} for ${name}`}
            onClick={async () => {
              if (await confirm({ title: `Remove ${a.name} for ${name}?`, description: `${name} then does the closest it has.`, action: 'Remove' })) removeClip.mutate({ id: a.id, character: id });
            }}
          >
            <Trash2 aria-hidden className="size-3.5" />
          </Button>
        )}
      </span>
    </li>
  );
}
