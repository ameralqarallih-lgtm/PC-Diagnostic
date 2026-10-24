import { CheckCircle2, CircleDot, Hash, Save, LayoutGrid, LayoutTemplate, MonitorSmartphone, Move, Plus, RectangleHorizontal, RectangleVertical, Sparkles, Trash2, Undo2 } from 'lucide-react';
import { AnimatePresence, motion } from 'motion/react';
import { useBlocker } from '@tanstack/react-router';
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Dialog } from '@/components/ui/dialog';
import { SaveBar } from '@/components/ui/save-bar';
import { Menu } from '@/components/ui/menu';
import type { OverlayConfig, OverlayDisplay, OverlayPanel, OverlaySource, OverlayStyle } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { emptyUndo, recordChange, redoStep, undoKey, undoStep, type UndoStack } from './undo';
import { needsHistory } from './model';
import { CharactersTab } from './characters/CharactersTab';
import { screenCharactersOf, screenStage, withScreenCharacters } from './model';
import { ANCHORS, DEFAULT_STYLE, GRIDS, HISTORY_SECONDS, INTERVALS, MAX_PANELS, NEW_PANEL_PRESET, TEMPLATES, arrangedInGrid, deviceOf, emptyConfig, everyPanel, fitWithin, fittedToScreen, gridFor, hasPortrait, isPortrait, newId, newPanel, panelsOf, presetsOf, screensOf, visualOf, withDevice, withPanels, withPortrait, type History } from './model';
import { DriversCard } from '@/features/devices/DriversPage';
import { DeviceScreenSettings } from './DeviceScreens';
import { DesignGallery } from './DesignGallery';
import type { PanelDesign } from './designs';
import { fitScreenDesign, logicalSize, type ScreenDesign } from './screens';
import { PanelInspector } from './PanelInspector';
import { ShrunkPreview, metrics } from './PanelPreview';
import { artSignature } from './art';
import { bakeArt, isGenerative } from './liveArt';
import { ScreenEditor, ScreenMap } from './ScreenCanvas';
import { WeatherPlace } from './WeatherPlace';
import { uploadBlob, useArrange, useIdentify, useOverlay, useOverlayState, useSaveOverlay } from './useOverlay';

interface PanelRow {
  screen: string;
  screenName: string;
  connected: boolean;
  panel: OverlayPanel;
}

const PANEL_COLUMNS: Column<PanelRow>[] = [
  { id: 'screen', header: 'Screen', value: (r) => r.screenName },
  { id: 'title', header: 'Panel', value: (r) => r.panel.title || 'Untitled panel', groupable: false },
  { id: 'readings', header: 'Readings', value: (r) => r.panel.readings.length, groupable: false, align: 'right', firstDesc: true },
  { id: 'place', header: 'Place', value: (r) => ANCHORS.find((a) => a.x === r.panel.x && a.y === r.panel.y)?.label ?? 'Placed by hand' },
  { id: 'shown', header: 'Shown', value: (r) => (r.panel.hidden ? 'Hidden' : r.connected ? 'On screen' : 'Screen not connected') },
];

/**
 * Readings on the screens: the monitors as they are arranged, each with its own panels of readings that the recorder
 * draws over everything (click-through). Add, remove, move and style the panels, then save: nothing changes on the
 * screens until then, and leaving with changes not saved asks first.
 */
export function OverlayPage() {
  const overlay = useOverlay();
  const live = useOverlayState();
  const save = useSaveOverlay();
  const identify = useIdentify();
  const arrange = useArrange();
  const [draft, setDraft] = useState<OverlayConfig | null>(null);
  const [dirty, setDirty] = useState(0);
  // The layout's revision the draft is based on, and the change count saved last: a newer revision with nothing
  // unsaved here (the panels were arranged on the screens) replaces the draft.
  const revision = useRef<string | null | undefined>(undefined);
  // The change count saved last (a state too, for showing what is not saved yet).
  // The layout as saved (loaded, or saved here): there is something to save only when the draft differs from it (a
  // count of changes made a click that changed nothing ask to save).
  const [savedConfig, setSavedConfig] = useState<OverlayConfig | null>(null);
  const unsaved = useMemo(() => draft !== null && savedConfig !== null && !sameLayout(draft, savedConfig), [draft, savedConfig]);
  const unsavedNow = useRef(false);
  unsavedNow.current = unsaved;
  const savedRef = useRef(savedConfig);
  savedRef.current = savedConfig;
  // What waits for an answer to "save your changes?": another panel chosen.
  const [pendingPick, setPendingPick] = useState<{ id: string | null } | null>(null);
  // A reading whose value was clicked on the screen: its settings opened in the panel's card.
  const [focusReading, setFocusReading] = useState<{ index: number; at: number } | null>(null);
  // A character clicked on the screen: its settings opened (the panel's, or the screen's own).
  const [focusCharacter, setFocusCharacter] = useState<{ id: string; at: number } | null>(null);
  // Leaving the page with changes not saved: asked first (the browser asks when the tab is closed).
  const leaving = useBlocker({ shouldBlockFn: () => unsavedNow.current, enableBeforeUnload: () => unsavedNow.current, withResolver: true });
  const [fromScreens, setFromScreens] = useState(false);
  const [gallery, setGallery] = useState(false);
  // The screen's panels before a whole-screen design replaced them (for undoing it).
  const [replaced, setReplaced] = useState<{ key: string; name: string; design: string; panels: OverlayPanel[]; portrait: boolean } | null>(null);
  // The orientation being edited (null: the screen's own now).
  const [view, setView] = useState<'landscape' | 'portrait' | null>(null);
  const [screen, setScreen] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [zoom, setZoom] = useState(1);
  const [history, setHistory] = useState<History>(() => new Map());
  // A moving background being made for the recorder (a few seconds): how far it is.
  const [baking, setBaking] = useState<number | null>(null);
  const saving = useRef(save);
  saving.current = save;
  // v2: the part of the page shown (the editor, all panels, options, the generator, screen drivers).
  const [tab, setTab] = useState<EditorTab>('editor');
  const confirm = useConfirm();
  // Undo and redo of the layout (Ctrl+Z, Ctrl+Y): quick changes in a row (a drag, a slider) are one step.
  const undo = useRef<UndoStack<OverlayConfig>>(emptyUndo());
  const steps = useRef<{ undo: () => void; redo: () => void }>({ undo: () => undefined, redo: () => undefined });
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const which = undoKey(e);
      if (!which) return;
      e.preventDefault();
      steps.current[which]();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);
  // The height the v2 editor has for the screen (the canvas fits it).
  const [paneRef, paneSize] = useBoxSize();
  const presets = useMemo(() => presetsOf(overlay.data?.presets), [overlay.data?.presets]);

  // Graphs in the preview: the values of the readings shown as graphs, one point per refresh of the live values.
  const graphed = useMemo(
    () => [...new Set(screensOf(draft ?? emptyConfig()).flatMap((sc) => sc.panels.flatMap((p) => p.readings.filter((r) => needsHistory(visualOf(r))).map((r) => r.source))))].sort().join('|'),
    [draft],
  );
  useEffect(() => {
    const data = live.data;
    if (!data) return;
    const now = live.dataUpdatedAt || Date.now();
    setHistory((old) => {
      const next = new Map<string, (readonly [number, number])[]>();
      for (const id of graphed ? graphed.split('|') : []) {
        const value = data.sources.find((src) => src.id === id)?.value;
        const kept = (old.get(id) ?? []).filter(([t]) => t >= now - HISTORY_SECONDS * 1000 && t < now);
        next.set(id, [...kept, [now, value ?? Number.NaN] as const]);
      }
      return next;
    });
  }, [live.data, live.dataUpdatedAt, graphed]);

  useEffect(() => {
    if (overlay.data && draft === null) {
      setDraft(overlay.data.config ?? emptyConfig());
      setSavedConfig(overlay.data.config ?? emptyConfig());
      revision.current = overlay.data.revision;
    }
  }, [overlay.data, draft]);

  // Saved when asked (Save), then shown on the screens.
  const saveNow = (then?: () => void) => {
    if (!draft) return;
    const sent = draft;
    saving.current.mutate(sent, {
      onSuccess: (response) => {
        revision.current = response?.revision ?? revision.current;
        setSavedConfig(sent);
        then?.();
      },
    });
  };
  // The changes not saved undone: the layout as saved (itself a step that can be undone).
  const discard = () => {
    if (!savedConfig) return;
    if (draft) undo.current = recordChange(undo.current, draft, Number.NEGATIVE_INFINITY);
    setDraft(savedConfig);
  };

  // Changed elsewhere (on the screens): load it, unless there is something here not saved yet.
  const seen = live.data?.revision;
  const refetchOverlay = overlay.refetch;
  useEffect(() => {
    if (!seen || revision.current === undefined || seen === revision.current || save.isPending || unsavedNow.current) return;
    revision.current = seen;
    void refetchOverlay().then((result) => {
      if (result.data && result.data.revision === seen) {
        setDraft(result.data.config ?? emptyConfig());
        setSavedConfig(result.data.config ?? emptyConfig());
        undo.current = emptyUndo();
        setFromScreens(true);
      }
    });
  }, [seen, save.isPending, dirty, refetchOverlay]);

  // Pictures made from a panel's art (gauge faces, grids, frames around its readings; a generated scene or a picture
  // with glass cards over them): made and saved for the recorder for a panel that has none yet, and again when its
  // size, readings or look change. The editor draws the art live meanwhile, so it always lines up here.
  const artMade = useRef(new Map<string, string>());
  useEffect(() => {
    if (!draft) return undefined;
    const due: { screen: string; panel: OverlayPanel; signature: string }[] = [];
    for (const sc of screensOf(draft)) {
      for (const p of everyPanel(sc)) {
        if (!p.style.art || p.width == null || p.height == null) continue;
        const key = `${sc.key}|${p.id}`;
        const signature = artSignature(p);
        // A picture loaded with the layout was made for it.
        if (!artMade.current.has(key) && p.style.image) {
          artMade.current.set(key, signature);
          continue;
        }
        if (!p.style.image || artMade.current.get(key) !== signature) due.push({ screen: sc.key, panel: p, signature });
      }
    }
    if (due.length === 0) return undefined;
    let cancelled = false;
    // An animation is made once the panel has settled (resizing makes it again each time otherwise).
    const moving = due.some(({ panel: p }) => isGenerative(p.style.art) || p.style.art === 'glass');
    const cleanAtStart = !unsavedNow.current;
    const timer = window.setTimeout(() => {
      void (async () => {
        for (const { screen: key, panel: p, signature } of due) {
          try {
            const animated = isGenerative(p.style.art) || p.style.art === 'glass';
            if (animated) setBaking(0);
            const blob = await bakeArt(p, { cancelled: () => cancelled, onProgress: animated ? (done) => !cancelled && setBaking(done) : undefined });
            if (animated) setBaking(null);
            if (!blob || cancelled) return;
            const { id } = await uploadBlob(blob, { generated: true });
            if (cancelled) return;
            artMade.current.set(`${key}|${p.id}`, signature);
            setDraft((d) => (d ? withArtPicture(d, key, p.id, signature, id) : d));
            // Made for a layout as it was saved (nothing changed here): saved with it at once, so the recorder draws it
            // too (it is not a change for the user to save).
            if (cleanAtStart && savedRef.current) {
              const next = withArtPicture(savedRef.current, key, p.id, signature, id);
              setSavedConfig(next);
              saving.current.mutate(next, {
                onSuccess: (response) => {
                  revision.current = response?.revision ?? revision.current;
                },
              });
            }
            setDirty((n) => n + 1);
          } catch {
            // Made again at the next change.
            setBaking(null);
          }
        }
      })();
    }, moving ? 1500 : 600);
    return () => {
      cancelled = true;
      setBaking(null);
      window.clearTimeout(timer);
    };
  }, [draft]);

  const state = live.data ?? overlay.data?.state ?? null;
  const arranging = state?.arranging ?? false;
  const displays = useMemo(() => state?.displays ?? [], [state]);
  const sources = useMemo<OverlaySource[]>(() => state?.sources ?? [], [state]);
  const sourceMap = useMemo(() => new Map(sources.map((s) => [s.id, s])), [sources]);
  const current = displays.find((d) => d.key === screen) ?? displays.find((d) => d.primary) ?? displays[0] ?? null;

  if (overlay.error) return <p className="text-sm text-danger">The on-screen readings could not be loaded.</p>;
  if (!draft) return <p className="text-sm text-muted">Loading…</p>;

  // Another panel chosen (or none) with changes not saved: asked first.
  const pick = (id: string | null) => {
    if (unsaved && selected !== null && id !== selected) setPendingPick({ id });
    else setSelected(id);
  };

  // Removing a panel asks first.
  const confirmPanel = (p: OverlayPanel | undefined) =>
    confirm({ title: `Remove ${p?.title || 'this panel'}?`, description: 'It is taken off the screen with its readings and characters (until you save, Undo brings it back).', action: 'Remove' });
  const change = (next: OverlayConfig) => {
    undo.current = recordChange(undo.current, draft, Date.now());
    setDraft(next);
    setDirty((n) => n + 1);
  };
  const step = (which: 'undo' | 'redo') => {
    const r = (which === 'undo' ? undoStep : redoStep)(undo.current, draft);
    if (!r) return;
    undo.current = r.stack;
    setDraft(r.value);
    setDirty((n) => n + 1);
  };
  steps.current = { undo: () => step('undo'), redo: () => step('redo') };
  const canUndo = undo.current.past.length > 0;
  const canRedo = undo.current.future.length > 0;
  const changes = savedConfig ? changedCount(draft, savedConfig) : 0;
  // The orientation edited: the screen's own now unless chosen; the other one is edited in its shape.
  const nowPortrait = current ? isPortrait(current) : false;
  const portraitView = view ? view === 'portrait' : nowPortrait;
  const shown = current && portraitView !== nowPortrait ? { ...current, width: current.height, height: current.width } : current;
  const ownPortrait = current ? hasPortrait(draft, current.key) : false;
  const box = shown ? logicalSize(shown) : { width: 1920, height: 1080 };
  const panels = current ? panelsOf(draft, current.key, portraitView) : [];
  const setPanels = (display: { key: string; name: string | null }, next: OverlayPanel[], portrait = display.key === current?.key && portraitView) =>
    change(withPanels(draft, display.key, display.name, next, portrait));
  // Ids are unique across both orientations of a screen.
  const screenIds = (key: string) => everyPanel(screensOf(draft).find((s) => s.key === key) ?? { key, name: null, panels: [] }).map((p) => p.id);
  const panel = panels.find((p) => p.id === selected) ?? null;
  const disconnected = screensOf(draft).filter((s) => !displays.some((d) => d.key === s.key));
  const rows: PanelRow[] = screensOf(draft).flatMap((s) =>
    everyPanel(s).map((p) => {
      const display = displays.find((d) => d.key === s.key);
      const upright = s.portrait?.includes(p) ? ' · upright' : '';
      return { screen: s.key, screenName: display ? `${display.number}: ${display.name}${upright}` : `${s.name ?? 'Screen'} (not connected)${upright}`, connected: !!display, panel: p };
    }),
  );

  // A new panel takes the look of the last one on the screen (a picture made for that one stays with it).
  const newPanelStyle: OverlayStyle = (() => {
    const last = panels[panels.length - 1]?.style;
    return last ? { ...last, art: null, image: last.art ? null : last.image } : (presets.find((p) => p.id === NEW_PANEL_PRESET)?.style ?? DEFAULT_STYLE);
  })();
  const addPanel = (template: string) => {
    if (!current || panels.length >= MAX_PANELS) return;
    const made = newPanel(template, sources, panels, newPanelStyle);
    const created = { ...made, id: newId(screenIds(current.key)) };
    setPanels(current, [...panels, created]);
    setSelected(created.id);
  };
  const addDesign = (design: PanelDesign, natural?: { width: number; height: number }) => {
    if (!current || panels.length >= MAX_PANELS) return;
    // Its place and id as any new panel's; everything else from the design; shrunk to fit a screen smaller than it.
    const placed = newPanel('empty', sources, panels);
    const built: OverlayPanel = { ...placed, ...design.build(sources), id: newId(screenIds(current.key)), design: design.id };
    const created =
      natural && (natural.width > box.width || natural.height > box.height)
        ? { ...fittedToScreen(built, box, natural, 2 * (built.style.padding + metrics(built.style, 1).margin)), restore: null }
        : built;
    setPanels(current, [...panels, created]);
    setSelected(created.id);
    setGallery(false);
  };
  const fillWithDesign = (design: ScreenDesign) => {
    if (!current) return;
    // Fitted to the screen (in the orientation edited) at 100 % scaling, in place of its panels.
    const ids = screenIds(current.key).filter((id) => !panels.some((p) => p.id === id));
    const created = fitScreenDesign(design, sources, box).map((p) => {
      const id = newId(ids);
      ids.push(id);
      return { ...p, id } as OverlayPanel;
    });
    setReplaced({ key: current.key, name: current.name, design: design.name, panels, portrait: portraitView });
    setPanels(current, created);
    setSelected(created[0]?.id ?? null);
    setGallery(false);
  };
  const applyToAll = (look: OverlayStyle) => change({ ...draft, screens: screensOf(draft).map((sc) => ({ ...sc, panels: sc.panels.map((p) => ({ ...p, style: { ...look } })) })) });
  const moveToScreen = (target: OverlayDisplay, p: OverlayPanel) => {
    if (!current) return;
    const upright = isPortrait(target);
    const ids = screenIds(target.key);
    const moved = { ...p, id: ids.includes(p.id) ? newId(ids) : p.id };
    const withoutHere = withPanels(draft, current.key, current.name, panels.filter((x) => x.id !== p.id), portraitView);
    change(withPanels(withoutHere, target.key, target.name, [...panelsOf(withoutHere, target.key, upright), moved], upright));
    setScreen(target.key);
    setSelected(moved.id);
  };

  const shownOn = displays.filter((d) => panelsOf(draft, d.key, isPortrait(d)).some((p) => !p.hidden)).length;
  // A layout of its own for when the screen is upright: its panels copied (new ids), each shrunk to fit if it must.
  const makePortrait = () => {
    if (!current) return;
    const ids = screenIds(current.key);
    const upright = nowPortrait ? logicalSize(current) : logicalSize({ ...current, width: current.height, height: current.width });
    const copies = panelsOf(draft, current.key, false).map((p) => {
      const id = newId(ids);
      ids.push(id);
      return fitWithin({ ...p, id, restore: null }, upright);
    });
    change(withPortrait(draft, current.key, current.name, copies));
    setView('portrait');
    setSelected(null);
  };
  const arrangeAll = (grid: { columns: number; rows: number }) => current && setPanels(current, arrangedInGrid(panels, box, grid));
  const status = !state
    ? { tone: 'neutral' as const, text: 'Recorder not reachable' }
    : state.problem
      ? { tone: 'warning' as const, text: state.problem }
      : !draft.enabled
        ? { tone: 'neutral' as const, text: 'Off' }
        : !state.visible
          ? { tone: 'neutral' as const, text: `Hidden with ${draft.hotkey}` }
          : { tone: 'success' as const, text: shownOn ? `On ${shownOn} screen${shownOn === 1 ? '' : 's'}` : 'On, no panels yet' };

  const optionsCard = (
      <Card>
        <CardContent className="flex flex-wrap items-center gap-x-6 gap-y-3 pt-4">
          <label className="flex items-center gap-2 text-sm font-medium">
            <input type="checkbox" role="switch" checked={draft.enabled} onChange={(e) => change({ ...draft, enabled: e.target.checked })} className="size-4 accent-[var(--accent)]" />
            Show readings on my screens
          </label>
          <Badge tone={status.tone}>{status.text}</Badge>
          <label className="flex items-center gap-2 text-sm">
            <span className="text-muted">Show/hide hotkey</span>
            <input className="h-8 w-40 rounded-md border border-border bg-background px-2 text-sm" value={draft.hotkey} onChange={(e) => change({ ...draft, hotkey: e.target.value })} aria-label="Show/hide hotkey" />
          </label>
          <label className="flex items-center gap-2 text-sm">
            <span className="text-muted">Arrange hotkey</span>
            <input className="h-8 w-40 rounded-md border border-border bg-background px-2 text-sm" value={draft.arrangeHotkey ?? 'Ctrl+Alt+Shift+A'} onChange={(e) => change({ ...draft, arrangeHotkey: e.target.value })} aria-label="Arrange hotkey" />
          </label>
          <label className="flex items-center gap-2 text-sm">
            <span className="text-muted">Refresh</span>
            <select className="h-8 rounded-md border border-border bg-background px-2 text-sm" value={draft.intervalMs} onChange={(e) => change({ ...draft, intervalMs: Number(e.target.value) })} aria-label="Refresh">
              {INTERVALS.map((ms) => (
                <option key={ms} value={ms}>
                  {ms < 1000 ? `${ms} ms` : `${ms / 1000} s`}
                </option>
              ))}
            </select>
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={draft.hideFromCapture} onChange={(e) => change({ ...draft, hideFromCapture: e.target.checked })} className="size-4 accent-[var(--accent)]" />
            Keep out of screenshots and recordings
          </label>
          <label className="flex items-center gap-2 text-sm" title="Panels with a moving background move on the screens (still while the PC is locked, on battery saver or with Windows' animations off)">
            <input type="checkbox" checked={draft.motion ?? true} onChange={(e) => change({ ...draft, motion: e.target.checked })} className="size-4 accent-[var(--accent)]" />
            Moving backgrounds
          </label>
          <label className="flex items-center gap-2 text-sm" title="Numbers count and bars, rings and dials sweep to a new value over about half a second instead of jumping">
            <input type="checkbox" checked={draft.smoothValues ?? true} onChange={(e) => change({ ...draft, smoothValues: e.target.checked })} className="size-4 accent-[var(--accent)]" />
            Smooth value changes
          </label>
          <label className="flex items-center gap-2 text-sm" title="The characters panels have (Characters tab of a panel), on the screens">
            <input type="checkbox" checked={draft.characters ?? true} onChange={(e) => change({ ...draft, characters: e.target.checked })} className="size-4 accent-[var(--accent)]" />
            Characters
          </label>
          <WeatherPlace value={draft.weather} onChange={(weather) => change({ ...draft, weather })} />
          <Button size="sm" variant="outline" onClick={() => identify.mutate()} disabled={!state?.running}>
            <Hash aria-hidden /> Identify screens
          </Button>
          <Button size="sm" variant={arranging ? 'default' : 'outline'} onClick={() => arrange.mutate(!arranging)} disabled={!state?.running || arrange.isPending || (unsaved && !arranging)} title={unsaved && !arranging ? 'Save your changes first: the screens arrange the saved panels' : undefined} aria-pressed={arranging}>
            <Move aria-hidden /> {arranging ? 'Done arranging' : 'Arrange on the screens'}
          </Button>
          <span className="ml-auto text-xs text-muted" role="status">
            {baking != null ? `Making the moving background… ${Math.round(baking * 100)} %` : save.isPending ? 'Saving…' : save.error ? <span className="text-danger">{(save.error as Error).message}</span> : unsaved ? (
              <span className="flex items-center gap-1 text-warning">
                <CircleDot className="size-3.5" aria-hidden /> Changes not saved
              </span>
            ) : dirty ? (
              <span className="flex items-center gap-1">
                <CheckCircle2 className="size-3.5 text-success" aria-hidden /> Saved
              </span>
            ) : (
              ''
            )}
          </span>
          <Button size="sm" variant="ghost" onClick={discard} disabled={!unsaved || save.isPending}>
            <Undo2 aria-hidden /> Discard changes
          </Button>
          <Button size="sm" onClick={() => saveNow()} disabled={!unsaved || save.isPending}>
            <Save aria-hidden /> Save changes
          </Button>
        </CardContent>
      </Card>
  );
  const notices = (
<>
      {overlay.data?.recorderProblem && <p className="rounded-lg border border-warning/40 bg-warning/10 px-3 py-2 text-sm">{overlay.data.recorderProblem}</p>}

      {arranging && (
        <p role="status" className="flex flex-wrap items-center gap-x-2 rounded-lg border border-accent/50 bg-accent/10 px-3 py-2 text-sm">
          <span className="relative flex size-2.5" aria-hidden>
            <span className="absolute inline-flex size-full rounded-full bg-accent opacity-60 motion-safe:animate-ping" />
            <span className="relative inline-flex size-2.5 rounded-full bg-accent" />
          </span>
          <strong>Arranging on the screens.</strong> Drag a panel to move it (onto another screen too), its corner to resize it, a reading onto another to reorder; its buttons hide it or open its options, and right-clicking a reading changes its design, how it reads or removes it. What you change there appears here. Press Esc or Done to finish
          {draft.arrangeHotkey ? ` (or ${draft.arrangeHotkey})` : ''}.
        </p>
      )}
      {fromScreens && !arranging && (
        <p role="status" className="flex items-center justify-between gap-2 rounded-lg border border-border bg-surface-muted px-3 py-2 text-sm">
          <span>The layout was changed on the screens; this page shows the changes.</span>
          <button type="button" className="text-xs text-muted hover:text-foreground" onClick={() => setFromScreens(false)}>
            Dismiss
          </button>
        </p>
      )}
</>
  );
  const screensCard = (
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <MonitorSmartphone className="size-4 text-accent" aria-hidden /> Your screens
          </CardTitle>
          <span className="text-xs text-muted">As they are arranged in Windows. Choose one to edit its readings.</span>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {displays.length === 0 ? (
            <p className="text-sm text-muted">{state ? 'The recorder sees no screens (it runs without a desktop).' : 'Waiting for the recorder…'}</p>
          ) : (
            <ScreenMap displays={displays} panelsOf={(key) => panelsOf(draft, key, displays.some((d) => d.key === key && isPortrait(d)))} backdropOf={(key) => deviceOf(draft, key).background} sources={sourceMap} history={history} selected={current?.key ?? null} onSelect={(key) => { setScreen(key); setSelected(null); setView(null); setTab('editor'); }} />
          )}
          {disconnected.length > 0 && (
            <ul className="flex flex-col gap-1 text-xs text-muted" aria-label="Screens not connected">
              {disconnected.map((s) => (
                <li key={s.key} className="flex flex-wrap items-center gap-2">
                  <span>
                    {s.name ?? 'A screen'} is not connected: its {s.panels.length} panel{s.panels.length === 1 ? '' : 's'} come back with it.
                  </span>
                  {current && (
                    <button type="button" className="text-accent hover:underline" onClick={() => change(withPanels(withPanels(draft, s.key, s.name, []), current.key, current.name, [...panels, ...s.panels.map((p, i) => ({ ...p, id: newId([...panels.map((x) => x.id), ...s.panels.slice(0, i).map((x) => x.id)]) }))].slice(0, MAX_PANELS)))}>
                      Move them to {current.name}
                    </button>
                  )}
                  <button type="button" className="text-danger hover:underline" onClick={async () => (await confirm({ title: `Delete the ${s.panels.length} panel${s.panels.length === 1 ? '' : 's'} of ${s.name ?? 'this screen'}?`, description: 'They are taken out of the layout (until you save, Undo brings them back).' })) && change(withPanels(draft, s.key, s.name, []))}>
                    Delete them
                  </button>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
  );
  const canvasCard = current && (
          <Card className="xl:overflow-y-auto">
            <CardHeader className="flex-wrap">
              <CardTitle>
                {current.kind === 'device' ? 'USB screen' : 'Screen'} {current.number}: {current.name}
              </CardTitle>
              <div className="flex flex-wrap items-center gap-2">
                <div role="radiogroup" aria-label="Zoom" title="Enlarge the screen and its panels together (they keep their true size on it)" className="flex gap-0.5 rounded-md bg-surface-muted p-0.5 text-xs">
                  {[
                    [1, 'Fit'],
                    [2, '2×'],
                    [3, '3×'],
                  ].map(([value, text]) => (
                    <button key={value} type="button" role="radio" aria-checked={zoom === value} onClick={() => setZoom(value as number)} className={cn('rounded px-2 py-0.5', zoom === value ? 'bg-surface font-medium shadow-sm' : 'text-muted')}>
                      {text}
                    </button>
                  ))}
                </div>
                <div role="radiogroup" aria-label="Orientation" title="Each screen can have a layout for when it is wide and one for when it is upright; the right one shows by itself" className="flex gap-0.5 rounded-md bg-surface-muted p-0.5 text-xs">
                  {(['landscape', 'portrait'] as const).map((o) => (
                    <button
                      key={o}
                      type="button"
                      role="radio"
                      aria-checked={(portraitView ? 'portrait' : 'landscape') === o}
                      onClick={() => {
                        setView(o);
                        setSelected(null);
                      }}
                      className={cn('flex items-center gap-1 rounded px-2 py-0.5', (portraitView ? 'portrait' : 'landscape') === o ? 'bg-surface font-medium shadow-sm' : 'text-muted')}
                    >
                      {o === 'landscape' ? <RectangleHorizontal className="size-3.5" aria-hidden /> : <RectangleVertical className="size-3.5" aria-hidden />}
                      {o === 'landscape' ? 'Wide' : 'Upright'}
                      {(nowPortrait ? 'portrait' : 'landscape') === o && <span className="text-[10px] text-success">now</span>}
                    </button>
                  ))}
                </div>
                <span className="text-xs text-muted">
                  {panels.length} of {MAX_PANELS} panels
                </span>
                <Menu
                  label="Arrange all panels"
                  className="border border-border px-2 py-1 text-foreground"
                  items={[
                    { label: `Automatic grid for ${panels.filter((p) => !p.hidden).length} panels`, onSelect: () => arrangeAll(gridFor(panels.filter((p) => !p.hidden).length, box)), disabled: panels.every((p) => p.hidden) },
                    ...GRIDS.map((g) => ({ label: `${g.name} (${g.columns} × ${g.rows})`, onSelect: () => arrangeAll(g), disabled: panels.every((p) => p.hidden) })),
                  ]}
                >
                  <span className="flex items-center gap-1 text-sm">
                    <LayoutGrid className="size-4" aria-hidden /> Arrange all
                  </span>
                </Menu>
                <Button size="sm" variant="outline" onClick={() => setGallery(true)}>
                  <Sparkles aria-hidden /> Designs
                </Button>
                <Menu
                  label="Add a panel"
                  className="border border-border px-2 py-1 text-foreground"
                  width={570}
                  columns={3}
                  items={[
                    {
                      label: 'Browse designed panels…',
                      description: 'Ready-made panels and whole-screen designs, many with pictures made for them',
                      preview: <Sparkles className="size-6 text-accent" />,
                      onSelect: () => setGallery(true),
                      disabled: panels.length >= MAX_PANELS,
                    },
                    ...TEMPLATES.map((t) => ({
                      label: t.name,
                      description: t.description,
                      // The panel it adds, in the look it will have.
                      preview: <ShrunkPreview panel={{ ...newPanel(t.id, sources, [], newPanelStyle), id: `preview-${t.id}` }} sources={sourceMap} history={history} scale={1} fit={{ width: 170, height: 70 }} />,
                      onSelect: () => addPanel(t.id),
                      disabled: panels.length >= MAX_PANELS,
                    })),
                  ]}
                >
                  <span className="flex items-center gap-1 text-sm">
                    <Plus className="size-4" aria-hidden /> Add a panel
                  </span>
                </Menu>
              </div>
            </CardHeader>
            <CardContent className="flex flex-col gap-3">
              {replaced?.key === current.key && (
                <p role="status" className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-accent/40 bg-accent/10 px-3 py-2 text-sm">
                  <span>
                    <strong>{replaced.design}</strong> now fills {replaced.name}
                    {replaced.panels.length > 0 ? ` in place of its ${replaced.panels.length} panel${replaced.panels.length === 1 ? '' : 's'}` : ''}.
                  </span>
                  <span className="flex items-center gap-2">
                    {replaced.panels.length > 0 && (
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => {
                          setPanels({ key: replaced.key, name: replaced.name }, replaced.panels, replaced.portrait);
                          setSelected(null);
                          setReplaced(null);
                        }}
                      >
                        <Undo2 aria-hidden /> Undo
                      </Button>
                    )}
                    <button type="button" className="text-xs text-muted hover:text-foreground" onClick={() => setReplaced(null)}>
                      Dismiss
                    </button>
                  </span>
                </p>
              )}
              {portraitView && (
                <p role="status" className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-border bg-surface-muted px-3 py-2 text-sm">
                  {ownPortrait ? (
                    <>
                      <span>{current.name} has its own panels for when it is upright{nowPortrait ? ' (it is upright now)' : ''}.</span>
                      <button
                        type="button"
                        className="text-xs text-muted hover:text-foreground"
                        onClick={() => {
                          change(withPortrait(draft, current.key, current.name, null));
                          setSelected(null);
                        }}
                      >
                        Use the wide layout when upright too
                      </button>
                    </>
                  ) : (
                    <>
                      <span>Upright, {current.name} shows the same panels as when it is wide.</span>
                      <Button size="sm" variant="outline" onClick={makePortrait}>
                        <RectangleVertical aria-hidden /> Make a layout for upright
                      </Button>
                    </>
                  )}
                </p>
              )}
              {current.kind === 'device' && <DeviceScreenSettings display={current} settings={deviceOf(draft, current.key)} onChange={(patch) => change(withDevice(draft, current.key, patch))} />}
              <ScreenEditor
                fitHeight={paneSize.height > 0 ? Math.max(200, paneSize.height - 170) : undefined}
                zoom={zoom}
                characters={draft.characters ?? true}
                stage={screenStage(panels, screenCharactersOf(draft, current.key))}
                display={shown ?? current}
                backdrop={current.kind === 'device' ? deviceOf(draft, current.key).background : undefined}
                panels={panels}
                sources={sourceMap}
                history={history}
                selected={selected}
                onSelect={pick}
                onCharacter={(id, character) => {
                  pick(id);
                  setFocusCharacter({ id: character, at: Date.now() });
                }}
                onReading={(id, index) => {
                  pick(id);
                  setFocusReading({ index, at: Date.now() });
                }}
                // Moved or resized by hand: that is its size and place now (what Restore goes back to after the next fill, grid or fit).
                onMove={(id, x, y) => setPanels(current, panels.map((p) => (p.id === id ? { ...p, x, y, restore: null } : p)))}
                onResize={(id, width, height, x, y) => setPanels(current, panels.map((p) => (p.id === id ? { ...p, width, height, x, y, restore: null } : p)))}
                onRemove={async (id) => {
                  if (!(await confirmPanel(panels.find((p) => p.id === id)))) return;
                  setPanels(current, panels.filter((p) => p.id !== id));
                  setSelected(null);
                }}
                onPanel={(next) => setPanels(current, panels.map((p) => (p.id === next.id ? next : p)))}
              />
            </CardContent>
          </Card>
  );
  const inspectorCard = current && (
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <LayoutTemplate className="size-4 text-accent" aria-hidden /> {panel ? panel.title || 'Panel' : current.name || 'This screen'}
                {panel && (
                  // The screen's own settings and characters, with a panel chosen.
                  <Button size="sm" variant="outline" className="ml-auto" onClick={() => pick(null)}>
                    <MonitorSmartphone aria-hidden /> This screen
                  </Button>
                )}
              </CardTitle>
            </CardHeader>
            <CardContent>
              <Swap id={panel?.id ?? 'screen'}>
              {panel ? (
                <PanelInspector
                  panel={panel}
                  focusReading={focusReading}
                  focusCharacter={focusCharacter}
                  sources={sources}
                  sourceMap={sourceMap}
                  screens={displays}
                  screenKey={current.key}
                  presets={presets}
                  history={history}
                  onApplyToAll={applyToAll}
                  onChange={(next) => setPanels(current, panels.map((p) => (p.id === next.id ? next : p)))}
                  onRemove={async () => {
                    if (!(await confirmPanel(panel))) return;
                    setPanels(current, panels.filter((p) => p.id !== panel.id));
                    setSelected(null);
                  }}
                  onDuplicate={() => {
                    if (panels.length >= MAX_PANELS) return;
                    const copy = { ...panel, id: newId(panels.map((p) => p.id)), x: Math.min(1, panel.x + 0.05), y: Math.min(1, panel.y + 0.05) };
                    setPanels(current, [...panels, copy]);
                    setSelected(copy.id);
                  }}
                  onMoveToScreen={(key) => {
                    const target = displays.find((d) => d.key === key);
                    if (target && target.key !== current.key) moveToScreen(target, panel);
                  }}
                />
              ) : (
                <div className="flex flex-col gap-4">
                  <p className="text-sm text-muted">{panels.length ? 'Select a panel on the screen to change what it shows and how it looks.' : 'Add a panel to this screen.'}</p>
                  <CharactersTab
                    screen
                    focus={focusCharacter}
                    panel={screenStage(panels, screenCharactersOf(draft, current.key))}
                    sources={sourceMap}
                    onChange={(characters) => change(withScreenCharacters(draft, current.key, current.name, characters))}
                  />
                </div>
              )}
              </Swap>
            </CardContent>
          </Card>
  );
  const galleryPart = (
      <>{current && <DesignGallery open={gallery} onClose={() => setGallery(false)} sources={sources} screen={current} box={box} panelCount={panels.length} full={panels.length >= MAX_PANELS} onAdd={addDesign} onUseScreen={fillWithDesign} />}</>
  );
  const panelsCard = (
      <Card>
        <CardHeader>
          <CardTitle>All panels</CardTitle>
        </CardHeader>
        <CardContent>
          <DataTable
            viewKey="overlay.panels"
            label="All panels"
            rows={rows}
            columns={[
              ...PANEL_COLUMNS,
              {
                id: 'actions',
                header: '',
                value: () => null,
                sortable: false,
                groupable: false,
                className: 'w-20 text-right',
                cell: (r) => (
                  <span className="flex justify-end gap-1">
                    {r.connected && (
                      <Button size="sm" variant="ghost" onClick={() => { setScreen(r.screen); pick(r.panel.id); }}>
                        Edit
                      </Button>
                    )}
                    <Button size="icon" variant="ghost" aria-label={`Remove ${r.panel.title || 'panel'}`} onClick={async () => (await confirmPanel(r.panel)) && change(withPanels(draft, r.screen, screensOf(draft).find((s) => s.key === r.screen)?.name ?? null, panelsOf(draft, r.screen).filter((p) => p.id !== r.panel.id)))}>
                      <Trash2 aria-hidden />
                    </Button>
                  </span>
                ),
              },
            ]}
            rowKey={(r) => `${r.screen}|${r.panel.id}`}
            defaultView={{ groupBy: ['screen'], sort: [] }}
            emptyText="No panels yet."
            rowClassName={(r) => cn(!r.connected && 'opacity-60')}
          />
        </CardContent>
      </Card>
  );
  const saveDialog = (
      <>{(pendingPick || leaving.status === 'blocked') && (
        <Dialog
          open
          title="Save your changes?"
          description="The panels have changes that are not saved yet; the screens show them once saved."
          onClose={() => (pendingPick ? setPendingPick(null) : leaving.reset?.())}
          footer={
            <>
              <Button variant="ghost" onClick={() => (pendingPick ? setPendingPick(null) : leaving.reset?.())}>
                Keep editing
              </Button>
              <Button
                variant="outline"
                onClick={() => {
                  discard();
                  if (pendingPick) {
                    setSelected(pendingPick.id);
                    setPendingPick(null);
                  } else leaving.proceed?.();
                }}
              >
                Don’t save
              </Button>
              <Button
                disabled={save.isPending}
                onClick={() =>
                  saveNow(() => {
                    if (pendingPick) {
                      setSelected(pendingPick.id);
                      setPendingPick(null);
                    } else leaving.proceed?.();
                  })
                }
              >
                <Save aria-hidden /> Save
              </Button>
            </>
          }
        >
          {save.error && <p className="text-sm text-danger">{(save.error as Error).message}</p>}
        </Dialog>
      )}</>
  );

  const tabs: [EditorTab, string][] = [
    ['editor', 'Editor'],
    ['screens', disconnected.length ? `Screens (${disconnected.length} not connected)` : 'Screens'],
    ['panels', `All panels (${rows.length})`],
    ['options', 'Options'],
    ['drivers', 'Screen drivers'],
  ];
  return (
    <div className="flex flex-col gap-3 xl:h-[calc(100dvh-6.5rem)] xl:overflow-hidden">
      <header className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <h1 className="text-xl font-semibold">Screen editor</h1>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" role="switch" checked={draft.enabled} onChange={(e) => change({ ...draft, enabled: e.target.checked })} className="size-4 accent-[var(--accent)]" />
          Show on my screens
        </label>
        <Badge tone={status.tone}>{status.text}</Badge>
        <nav aria-label="Screen editor" className="flex gap-0.5 rounded-md border border-border bg-surface-muted p-0.5 text-sm">
          {tabs.map(([id, text]) => (
            <button key={id} type="button" aria-current={tab === id ? 'page' : undefined} onClick={() => setTab(id)} className={cn('relative rounded px-3 py-1 transition-colors duration-[120ms]', tab === id ? 'text-foreground' : 'text-muted hover:text-foreground')}>
              {tab === id && <motion.span layoutId="editor-tab" aria-hidden className="absolute inset-0 rounded bg-surface shadow-sm" transition={{ type: 'spring', stiffness: 380, damping: 32 }} />}
              <span className="relative">{text}</span>
            </button>
          ))}
        </nav>
        <div className="ml-auto flex items-center gap-2">
          <Button size="sm" variant="outline" onClick={() => identify.mutate()} disabled={!state?.running}>
            <Hash aria-hidden /> Identify
          </Button>
          <Button size="sm" variant={arranging ? 'default' : 'outline'} onClick={() => arrange.mutate(!arranging)} disabled={!state?.running || arrange.isPending || (unsaved && !arranging)} title={unsaved && !arranging ? 'Save your changes first: the screens arrange the saved panels' : 'Move and resize the panels on the screens themselves'} aria-pressed={arranging}>
            <Move aria-hidden /> {arranging ? 'Done arranging' : 'Arrange on the screens'}
          </Button>
        </div>
      </header>
      {notices}
      <AnimatePresence mode="wait" initial={false}>
        <motion.div key={tab} className="flex min-h-0 flex-1 flex-col gap-3" initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -6 }} transition={{ duration: 0.2, ease: [0.16, 1, 0.3, 1] }}>
          {tab === 'editor' && (
            <>
              {displays.length > 1 && (
                <div role="radiogroup" aria-label="Screens" className="flex flex-wrap gap-2">
                  {displays.map((d) => {
                    const on = d.key === current?.key;
                    const count = panelsOf(draft, d.key, isPortrait(d)).length;
                    return (
                      <button
                        key={d.key}
                        type="button"
                        role="radio"
                        aria-checked={on}
                        onClick={() => {
                          setScreen(d.key);
                          setSelected(null);
                          setView(null);
                        }}
                        className={cn('flex items-center gap-2 rounded-md border px-3 py-1.5 text-sm transition-colors duration-[120ms]', on ? 'border-accent/60 bg-accent/10 text-foreground' : 'border-border bg-surface text-muted hover:text-foreground')}
                      >
                        <MonitorSmartphone className={cn('size-4', on && 'text-accent')} aria-hidden />
                        {d.number}: {d.name}
                        <span className="text-xs text-muted">
                          {count} panel{count === 1 ? '' : 's'}
                          {d.kind === 'device' && ` · USB, ${d.status === 'on' ? 'showing your panels' : d.status === 'busy' ? 'in use by another program' : d.status === 'problem' ? 'problem' : 'not used'}`}
                        </span>
                      </button>
                    );
                  })}
                </div>
              )}
              {displays.length === 0 && screensCard}
              {current && (
                <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 xl:grid-cols-[minmax(0,1fr)_26rem] 2xl:grid-cols-[minmax(0,1fr)_28rem]">
                  <div ref={paneRef} className="flex min-h-0 flex-col [&>div]:min-h-0 [&>div]:flex-1">
                    {canvasCard}
                  </div>
                  <div className="flex min-h-0 flex-col overflow-y-auto rounded-xl [&>div]:flex-1">{inspectorCard}</div>
                </div>
              )}
            </>
          )}
          {tab === 'screens' && <div className="min-h-0 flex-1 overflow-y-auto">{screensCard}</div>}
          {tab === 'panels' && <div className="min-h-0 flex-1 overflow-y-auto">{panelsCard}</div>}
          {tab === 'options' && (
            <div className="min-h-0 flex-1 overflow-y-auto">
              {optionsCard}
              {current?.kind === 'device' && <p className="mt-3 text-sm text-muted">A USB screen's own settings are in the Editor, above its screen.</p>}
            </div>
          )}
          {tab === 'drivers' && (
            <div className="min-h-0 flex-1 overflow-y-auto">
              <DriversCard />
            </div>
          )}
        </motion.div>
      </AnimatePresence>
      {galleryPart}
      <SaveBar show={unsaved} changes={changes} saving={save.isPending} error={save.error ? (save.error as Error).message : null} canUndo={canUndo} canRedo={canRedo} onUndo={() => step('undo')} onRedo={() => step('redo')} onDiscard={discard} onSave={() => saveNow()} />
      {saveDialog}
    </div>
  );
}

type EditorTab = 'editor' | 'screens' | 'panels' | 'options' | 'drivers';

/** How many things differ from the layout as saved: each panel changed, added or removed, and the screens' own settings. */
export function changedCount(draft: OverlayConfig, saved: OverlayConfig): number {
  const panelsBy = (c: OverlayConfig) => new Map(screensOf(c).flatMap((sc) => everyPanel(sc).map((p) => [`${sc.key}|${p.id}`, p] as const)));
  const a = panelsBy(draft);
  const b = panelsBy(saved);
  let n = 0;
  for (const key of new Set([...a.keys(), ...b.keys()])) if (!sameLayout(a.get(key), b.get(key))) n++;
  const rest = (c: OverlayConfig) => ({ ...c, screens: screensOf(c).map((sc) => ({ ...sc, panels: [], portrait: null })) });
  if (!sameLayout(rest(draft), rest(saved))) n++;
  return Math.max(n, sameLayout(draft, saved) ? 0 : 1);
}

/** Its children, faded and slid in again whenever `id` changes. */
function Swap({ id, children }: { id: string; children: ReactNode }) {
  return (
    <AnimatePresence mode="wait" initial={false}>
      <motion.div key={id} initial={{ opacity: 0, x: 8 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0, x: -8 }} transition={{ duration: 0.2, ease: [0.16, 1, 0.3, 1] }}>
        {children}
      </motion.div>
    </AnimatePresence>
  );
}

/** An element's size, following it. */
function useBoxSize(): [(el: HTMLDivElement | null) => void, { width: number; height: number }] {
  const [size, setSize] = useState({ width: 0, height: 0 });
  const observer = useRef<ResizeObserver | null>(null);
  const ref = useCallback((el: HTMLDivElement | null) => {
    observer.current?.disconnect();
    observer.current = null;
    if (!el || typeof ResizeObserver === 'undefined') return;
    observer.current = new ResizeObserver(([entry]) => {
      const box = entry?.contentRect;
      if (box) setSize((old) => (Math.abs(old.width - box.width) < 1 && Math.abs(old.height - box.height) < 1 ? old : { width: box.width, height: box.height }));
    });
    observer.current.observe(el);
  }, []);
  return [ref, size];
}

/** The picture made from a panel's art put in its look, if the panel is still as it was drawn for. */
function withArtPicture(config: OverlayConfig, key: string, id: string, signature: string, image: string): OverlayConfig {
  const put = (panels: OverlayPanel[]) => panels.map((p) => (p.id === id && artSignature(p) === signature ? { ...p, style: { ...p.style, image } } : p));
  return { ...config, screens: (config.screens ?? []).map((sc) => (sc.key === key ? { ...sc, panels: put(sc.panels), portrait: sc.portrait ? put(sc.portrait) : sc.portrait } : sc)) };
}

/** Whether two layouts are the same: their values, whatever the order of their keys and with absent and null alike. */
export function sameLayout(a: unknown, b: unknown): boolean {
  const canonical = (v: unknown): unknown => {
    if (Array.isArray(v)) return v.map(canonical);
    if (v && typeof v === 'object') {
      const out: Record<string, unknown> = {};
      for (const key of Object.keys(v).sort()) {
        const x = (v as Record<string, unknown>)[key];
        if (x !== null && x !== undefined) out[key] = canonical(x);
      }
      return out;
    }
    return v;
  };
  return JSON.stringify(canonical(a)) === JSON.stringify(canonical(b));
}
