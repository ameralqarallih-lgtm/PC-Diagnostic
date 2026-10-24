import { Link } from '@tanstack/react-router';
import { MonitorSmartphone, Plus, Sparkles, Trash2, Upload } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import { useEffect, useMemo, useState } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { SaveBar } from '@/components/ui/save-bar';
import type { CharacterInfo, OverlayConfig, OverlaySource } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { emptyConfig, everyPanel, isPortrait, panelsOf, screenCharactersOf, screensOf, screenStage, withScreenCharacters } from '../model';
import { sameLayout } from '../OverlayPage';
import { useOverlay, useOverlayState, useSaveOverlay } from '../useOverlay';
import { CharacterImport } from './CharacterImport';
import { CharacterThumb } from './CharacterSprite';
import { ACTIONS, CharactersTab, MAX_CHARACTERS, newCharacter } from './CharactersTab';
import { useCharacters, useDeleteCharacter } from './library';

/** Where a character is used: on which screens, and on which of their panels. */
export function placesOf(config: OverlayConfig, id: string): string[] {
  return screensOf(config).flatMap((sc) => [
    ...((sc.characters ?? []).some((c) => c.character === id) ? [`${sc.name ?? 'A screen'}`] : []),
    ...everyPanel(sc)
      .filter((p) => (p.characters ?? []).some((c) => c.character === id))
      .map((p) => `${p.title || 'A panel'} on ${sc.name ?? 'a screen'}`),
  ]);
}

/**
 * The characters (UI v2): the library as animated cards (shipped, imported, generated), the one chosen in detail
 * (its animations, where it is, put on a screen), and the characters of each screen with their placement, movement
 * and reactions, saved with the save bar. Panels' own characters stay in the Screen editor.
 */
export function CharactersPage() {
  const library = useCharacters();
  const overlay = useOverlay();
  const live = useOverlayState();
  const save = useSaveOverlay();
  const remove = useDeleteCharacter();
  const confirm = useConfirm();
  const [draft, setDraft] = useState<OverlayConfig | null>(null);
  const [saved, setSaved] = useState<OverlayConfig | null>(null);
  const [chosen, setChosen] = useState<string | null>(null);
  const [screenKey, setScreenKey] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);

  useEffect(() => {
    if (overlay.data && draft === null) {
      setDraft(overlay.data.config ?? emptyConfig());
      setSaved(overlay.data.config ?? emptyConfig());
    }
  }, [overlay.data, draft]);

  const characters = library.data ?? [];
  const state = live.data ?? overlay.data?.state ?? null;
  const displays = state?.displays ?? [];
  const sources = useMemo(() => new Map<string, OverlaySource>((state?.sources ?? []).map((s) => [s.id, s])), [state]);
  const display = displays.find((d) => d.key === screenKey) ?? displays.find((d) => d.primary) ?? displays[0] ?? null;
  const info = characters.find((c) => c.id === chosen) ?? characters[0] ?? null;
  const unsaved = draft !== null && saved !== null && !sameLayout(draft, saved);

  if (!draft) return <p className="text-sm text-muted">Loading…</p>;
  const onScreen = display ? screenCharactersOf(draft, display.key) : [];
  const setOnScreen = (next: typeof onScreen) => display && setDraft(withScreenCharacters(draft, display.key, display.name, next));
  const place = (c: CharacterInfo) => {
    if (!display || onScreen.length >= MAX_CHARACTERS) return;
    setOnScreen([...onScreen, newCharacter(c.id, onScreen, c, 'screen')]);
  };

  return (
    <div className="flex flex-col gap-3 xl:h-[calc(100dvh-6.5rem)] xl:overflow-hidden">
      <header className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <h1 className="text-xl font-semibold">Characters</h1>
        <span className="text-sm text-muted">{characters.length} in the library</span>
        <div className="ml-auto flex gap-2">
          <Button size="sm" variant="outline" onClick={() => setImporting(true)}>
            <Upload aria-hidden /> Import
          </Button>
          <Button size="sm" asChild>
            <Link to="/generator">
              <Sparkles aria-hidden /> Generate one
            </Link>
          </Button>
        </div>
      </header>

      <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_26rem]">
        {/* The library. */}
        <Card className="flex min-h-0 flex-col">
          <CardHeader>
            <CardTitle>Library</CardTitle>
          </CardHeader>
          <CardContent className="min-h-0 flex-1 overflow-y-auto">
            <ul aria-label="Library" className="grid grid-cols-[repeat(auto-fill,minmax(7.5rem,1fr))] gap-2">
              {characters.map((c) => (
                <li key={c.id}>
                  <LibraryCard info={c} chosen={info?.id === c.id} onChoose={() => setChosen(c.id)} />
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>

        {/* The one chosen. */}
        <Card className="flex min-h-0 flex-col">
          <AnimatePresence mode="wait" initial={false}>
            {info && (
              <motion.div key={info.id} className="flex min-h-0 flex-1 flex-col" initial={{ opacity: 0, x: 8 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0, x: -8 }} transition={{ duration: 0.2, ease: [0.16, 1, 0.3, 1] }}>
                <CharacterDetail
                  info={info}
                  places={placesOf(draft, info.id)}
                  screenName={display ? `${display.number}: ${display.name}` : null}
                  full={onScreen.length >= MAX_CHARACTERS}
                  onPlace={() => place(info)}
                  onDelete={async () => (await confirm({ title: `Delete ${info.name}?`, description: 'It is deleted from the library, on this PC and in every storage location.' })) && remove.mutate(info.id, { onSuccess: () => setChosen(null) })}
                  deleteError={remove.error?.message ?? null}
                />
              </motion.div>
            )}
          </AnimatePresence>
        </Card>

        {/* The screens' own characters. */}
        <Card className="flex min-h-0 flex-col">
          <CardHeader className="flex-col items-start gap-2">
            <CardTitle className="flex items-center gap-2">
              <MonitorSmartphone className="size-4 text-accent" aria-hidden /> On your screens
            </CardTitle>
            {displays.length > 1 && (
              <div role="radiogroup" aria-label="Screens" className="flex flex-wrap gap-1">
                {displays.map((d) => (
                  <button
                    key={d.key}
                    type="button"
                    role="radio"
                    aria-checked={d.key === display?.key}
                    onClick={() => setScreenKey(d.key)}
                    className={cn('rounded-md border px-2 py-1 text-xs', d.key === display?.key ? 'border-accent/60 bg-accent/10' : 'border-border text-muted hover:text-foreground')}
                  >
                    {d.number}: {d.name}
                  </button>
                ))}
              </div>
            )}
          </CardHeader>
          <CardContent className="min-h-0 flex-1 overflow-y-auto">
            {display ? (
              <CharactersTab screen panel={screenStage(panelsOf(draft, display.key, isPortrait(display)), onScreen)} sources={sources} onChange={setOnScreen} />
            ) : (
              <p className="text-sm text-muted">{state ? 'The recorder sees no screens.' : 'Waiting for the recorder…'}</p>
            )}
          </CardContent>
        </Card>
      </div>

      {importing && <CharacterImport onClose={() => setImporting(false)} onImported={(id) => setChosen(id)} />}
      <SaveBar
        show={unsaved}
        changes={1}
        saving={save.isPending}
        error={save.error ? (save.error as Error).message : null}
        onDiscard={() => saved && setDraft(saved)}
        onSave={() => {
          const sent = draft;
          save.mutate(sent, { onSuccess: () => setSaved(sent) });
        }}
      />
    </div>
  );
}

/** A character in the library: still, moving while pointed at or chosen, with its kind and whether it is used. */
function LibraryCard({ info, chosen, onChoose }: { info: CharacterInfo; chosen: boolean; onChoose: () => void }) {
  const [hover, setHover] = useState(false);
  return (
    <button
      type="button"
      aria-pressed={chosen}
      onClick={onChoose}
      onPointerEnter={() => setHover(true)}
      onPointerLeave={() => setHover(false)}
      className={cn(
        'flex w-full flex-col items-center gap-1 rounded-[10px] border p-2 text-xs transition-colors duration-[120ms]',
        chosen ? 'border-accent/60 bg-accent/10' : 'border-border bg-surface hover:bg-surface-muted',
      )}
    >
      <CharacterThumb id={info.id} action={hover || chosen ? 'walk' : 'idle'} size={72} moving={hover || chosen} />
      <span className="w-full truncate font-medium">{info.name}</span>
      <span className="text-muted">{info.shipped ? 'Built in' : 'Yours'}{info.used ? ' · in use' : ''}</span>
    </button>
  );
}

/** The chosen character: its animations to play, where it is used, and putting it on a screen. */
function CharacterDetail(props: { info: CharacterInfo; places: string[]; screenName: string | null; full: boolean; onPlace: () => void; onDelete: () => void; deleteError: string | null }) {
  const { info } = props;
  const [action, setAction] = useState('walk');
  const actions = ACTIONS.filter(([a]) => info.actions.includes(a));
  return (
    <>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          {info.name}
          <Badge>{info.shipped ? 'Built in' : 'Yours'}</Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto text-sm">
        <div className="flex justify-center rounded-[10px] border border-border bg-surface-muted/40 py-4">
          <CharacterThumb id={info.id} action={action} size={180} />
        </div>
        <div role="radiogroup" aria-label="Animation" className="flex flex-wrap gap-1">
          {actions.map(([a, label]) => (
            <button key={a} type="button" role="radio" aria-checked={action === a} onClick={() => setAction(a)} className={cn('rounded-md px-2 py-1 text-xs', action === a ? 'bg-accent text-accent-foreground' : 'bg-surface-muted text-muted hover:text-foreground')}>
              {label}
            </button>
          ))}
        </div>
        <section aria-label="Where it is" className="flex flex-col gap-1">
          <h3 className="text-xs font-medium text-muted">Where it is</h3>
          {props.places.length ? (
            <ul className="list-inside list-disc text-sm">
              {props.places.map((p) => (
                <li key={p}>{p}</li>
              ))}
            </ul>
          ) : (
            <p className="text-sm text-muted">Not on a screen yet.</p>
          )}
        </section>
        <div className="mt-auto flex flex-wrap items-center gap-2">
          <Button size="sm" disabled={!props.screenName || props.full} onClick={props.onPlace} title={props.full ? `A screen has at most ${MAX_CHARACTERS} characters` : undefined}>
            <Plus aria-hidden /> Put on {props.screenName ?? 'a screen'}
          </Button>
          {!info.shipped && (
            <Button size="sm" variant="ghost" disabled={info.used} title={info.used ? 'Take it off its panels and screens first' : undefined} onClick={props.onDelete}>
              <Trash2 aria-hidden /> Delete
            </Button>
          )}
          {props.deleteError && <span className="text-xs text-danger">{props.deleteError}</span>}
        </div>
      </CardContent>
    </>
  );
}
