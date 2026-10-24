import { motion, useReducedMotion } from 'motion/react';
import { Maximize, Plus } from 'lucide-react';
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { TabPill } from '@/components/ui/tab-indicator';
import type { OverlayDisplay, OverlayPanel, OverlaySource } from '@/lib/api/types';
import { EASE } from '@/lib/motion';
import { cn } from '@/lib/utils';
import { DESIGNS, type PanelDesign } from './designs';
import { HISTORY_SECONDS, needsHistory, visualOf, type History } from './model';
import { PanelPreview, ShrunkPreview } from './PanelPreview';
import { MiniPanel } from './ScreenCanvas';
import { SCREEN_DESIGNS, SCREEN_SIZES, fitScreenDesign, shapeDistance, sizeOf, type ScreenDesign } from './screens';

export interface DesignGalleryProps {
  open: boolean;
  onClose: () => void;
  /** This PC's values now (the previews show them live). */
  sources: readonly OverlaySource[];
  /** The screen the design is added to. */
  screen: OverlayDisplay;
  /** Its size for panels (at 100 % scaling) in the orientation being edited. */
  box: { width: number; height: number };
  /** The panels on that screen now (a whole-screen design replaces them). */
  panelCount: number;
  /** No more panels fit on that screen. */
  full: boolean;
  /** A design was chosen; with its size at 100 % when it was measured (to shrink it to fit a small screen). */
  onAdd: (design: PanelDesign, natural?: { width: number; height: number }) => void;
  /** A whole-screen design was chosen for the screen. */
  onUseScreen: (design: ScreenDesign) => void;
}

/** A backdrop like a desktop or a game, so see-through designs read as they will on a screen. */
const WALLPAPER = 'radial-gradient(120% 90% at 15% 10%, #1f4e79 0%, transparent 55%), radial-gradient(90% 80% at 90% 90%, #6d2a5c 0%, transparent 60%), linear-gradient(160deg, #0b1220, #111827)';

/** A whole-screen design whose shape is this far from the screen's reads as stretched there. */
const OTHER_SHAPE = 0.2;

type Tab = 'panels' | 'screen';

/**
 * The designed panels and whole-screen designs, each previewed live with this PC's values: add a panel to the screen,
 * or fill the screen with a design made for its size (fitted to it), then change anything.
 */
export function DesignGallery({ open, onClose, sources, screen, box, panelCount, full, onAdd, onUseScreen }: DesignGalleryProps) {
  const reduced = useReducedMotion() ?? false;
  // A USB screen opens on its whole-screen designs: that is what it is for.
  const [tab, setTab] = useState<Tab>(screen.kind === 'device' ? 'screen' : 'panels');
  useEffect(() => {
    if (open) setTab(screen.kind === 'device' ? 'screen' : 'panels');
  }, [open, screen.kind]);
  const [madeFor, setMadeFor] = useState<string | null>(null);
  const [asMade, setAsMade] = useState(false);
  const [allShapes, setAllShapes] = useState(false);
  const [onlyFitting, setOnlyFitting] = useState(false);
  const target = box;

  const built = useMemo(() => DESIGNS.map((design) => ({ design, panel: { id: design.id, x: 0, y: 0, ...design.build(sources) } as OverlayPanel })), [sources]);
  // Each design's size at 100 % (measured once, off screen): it fits this screen, or is shrunk to fit when added.
  const [natural, setNatural] = useState<Record<string, { width: number; height: number }>>({});
  const fitOf = (id: string) => {
    const size = natural[id];
    if (!size || size.width <= 0 || size.height <= 0) return 'unknown' as const;
    return size.width <= target.width && size.height <= target.height ? ('fits' as const) : ('shrunk' as const);
  };
  const panels = built
    .map((b, order) => ({ ...b, fit: fitOf(b.design.id), order }))
    .filter((b) => !onlyFitting || b.fit !== 'shrunk')
    .sort((a, b) => (a.fit === 'shrunk' ? 1 : 0) - (b.fit === 'shrunk' ? 1 : 0) || a.order - b.order);
  const screenDesigns = useMemo(
    () =>
      [...SCREEN_DESIGNS]
        .sort((a, b) => shapeDistance(a, target) - shapeDistance(b, target))
        .map((design) => {
          const size = asMade ? sizeOf(design) : target;
          const fitted = fitScreenDesign(design, sources, size).map((p, i) => ({ ...p, id: `${design.id}-${i}` }) as OverlayPanel);
          return { design, size, panels: fitted, other: shapeDistance(design, target) > OTHER_SHAPE };
        }),
    [sources, target, asMade],
  );
  // Those made for this screen's shape, unless all are asked for (or none is).
  const sameShape = screenDesigns.filter((d) => !d.other);
  const shapeFiltered = allShapes || madeFor || sameShape.length === 0 ? screenDesigns : sameShape;
  const shown = shapeFiltered.filter((d) => !madeFor || d.design.size === madeFor);

  // The graphs fill as the values come (one point per refresh of the page's values).
  const [history, setHistory] = useState<History>(() => new Map());
  const graphed = useMemo(
    () => [...new Set([...built.map((p) => p.panel), ...screenDesigns.flatMap((d) => d.panels)].flatMap((p) => p.readings.filter((r) => needsHistory(visualOf(r))).map((r) => r.source)))],
    [built, screenDesigns],
  );
  useEffect(() => {
    if (!open) return;
    const now = Date.now();
    setHistory((old) => {
      const next = new Map<string, (readonly [number, number])[]>();
      for (const id of graphed) {
        const value = sources.find((s) => s.id === id)?.value;
        const kept = (old.get(id) ?? []).filter(([t]) => t >= now - HISTORY_SECONDS * 1000);
        next.set(id, [...kept, [now, value ?? Number.NaN] as const]);
      }
      return next;
    });
  }, [open, sources, graphed]);
  const sourceMap = useMemo(() => new Map(sources.map((s) => [s.id, s])), [sources]);
  const sizes = SCREEN_SIZES.filter((s) => SCREEN_DESIGNS.some((d) => d.size === s.id));

  return (
    <Dialog open={open} title="Designs" description={`For ${screen.name}: add a designed panel, or fill the whole screen with a dashboard made for its size. Then change anything about it.`} onClose={onClose} className="max-w-6xl">
      <div className="flex flex-col gap-3">
        <div role="tablist" aria-label="Kind of design" className="flex w-fit gap-1 rounded-md bg-surface-muted p-1 text-sm">
          {(
            [
              ['panels', `Panels (${DESIGNS.length})`],
              ['screen', `Whole screen (${SCREEN_DESIGNS.length})`],
            ] as const
          ).map(([id, text]) => (
            <button key={id} type="button" role="tab" aria-selected={tab === id} onClick={() => setTab(id)} className={cn('relative isolate rounded px-3 py-1 transition-colors', tab === id ? 'font-medium' : 'text-muted hover:text-foreground')}>
              {tab === id && <TabPill group="design-tabs" />}
              {text}
            </button>
          ))}
        </div>

        {tab === 'panels' ? (
          <>
            <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted">
              <span>
                For {screen.name} at {target.width} × {target.height}
                {target.height > target.width ? ' (upright)' : ''}: those that fit come first; a larger one is shrunk to fit when added.
              </span>
              <label className="flex items-center gap-1.5">
                <input type="checkbox" checked={onlyFitting} onChange={(e) => setOnlyFitting(e.target.checked)} className="size-3.5 accent-[var(--accent)]" />
                Only those that fit this screen
              </label>
            </div>
            {open && (
              <div aria-hidden className="pointer-events-none fixed left-[-10000px] top-0 opacity-0">
                {built.map(({ design, panel }) => (
                  <Measure key={design.id} panel={panel} sources={sourceMap} onSize={(size) => setNatural((n) => (n[design.id]?.width === size.width && n[design.id]?.height === size.height ? n : { ...n, [design.id]: size }))} />
                ))}
              </div>
            )}
            <ul className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Designs">
              {panels.map(({ design, panel, fit }, i) => (
                <motion.li
                  key={design.id}
                  initial={reduced ? false : { opacity: 0, y: 10 }}
                  animate={{ opacity: 1, y: 0 }}
                  // Quick, and all within a fraction of a second: the gallery opens as a whole.
                  transition={{ duration: 0.3, ease: EASE.out, delay: reduced ? 0 : Math.min(i, 8) * 0.025 }}
                  className="motion-hover group flex flex-col overflow-hidden rounded-lg border border-border bg-surface transition-[border-color,box-shadow] hover:border-accent/60 hover:shadow-lg"
                >
                  <div className="flex h-48 items-center justify-center overflow-hidden" style={{ background: WALLPAPER }}>
                    {panel.readings.length > 0 ? (
                      <div className="transition-transform duration-300 ease-out group-hover:scale-[1.03]">
                        <ShrunkPreview panel={panel} sources={sourceMap} history={history} scale={1} fit={{ width: 330, height: 176 }} />
                      </div>
                    ) : (
                      <p className="px-4 text-center text-xs text-white/60">This PC has none of the readings this design shows.</p>
                    )}
                  </div>
                  <div className="flex flex-1 items-start justify-between gap-2 p-3">
                    <div className="min-w-0">
                      <h3 className="text-sm font-semibold">{design.name}</h3>
                      <p className="text-xs text-muted">{design.description}</p>
                      {fit !== 'unknown' && <p className={cn('mt-1 text-[11px]', fit === 'fits' ? 'text-success' : 'text-warning')}>{fit === 'fits' ? 'Fits this screen' : 'Larger than this screen: shrunk to fit'}</p>}
                    </div>
                    <Button size="sm" variant="outline" disabled={full || panel.readings.length === 0} onClick={() => onAdd(design, natural[design.id])} aria-label={`Add ${design.name}`}>
                      <Plus aria-hidden /> Add
                    </Button>
                  </div>
                </motion.li>
              ))}
            </ul>
            {full && <p className="text-xs text-warning">{screen.name} has as many panels as a screen takes; delete one to add another.</p>}
          </>
        ) : (
          <>
            <p className="text-xs text-muted">
              Dashboards that fill a screen: for USB screens and small monitors used for readings. Each is made for one size and fitted to {screen.name} ({target.width} × {target.height}); the ones made for its shape come first.
              {panelCount > 0 && ` Using one replaces the ${panelCount} panel${panelCount === 1 ? '' : 's'} on it (you can undo).`}
            </p>
            <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="Made for">
              <span className="mr-1 text-xs text-muted">Made for</span>
              {[{ id: null as string | null, name: 'Any size', title: 'Every design' }, ...sizes.map((s) => ({ id: s.id as string | null, name: s.name, title: `${s.width} × ${s.height}: ${s.examples}` }))].map((s) => (
                <button
                  key={s.id ?? 'any'}
                  type="button"
                  aria-pressed={madeFor === s.id}
                  title={s.title}
                  onClick={() => setMadeFor(s.id)}
                  className={cn('rounded-full border px-2.5 py-0.5 text-xs transition-colors', madeFor === s.id ? 'border-accent bg-accent/15 text-foreground' : 'border-border text-muted hover:text-foreground')}
                >
                  {s.name}
                </button>
              ))}
              <label className="ml-auto flex items-center gap-1.5 text-xs text-muted">
                <input type="checkbox" checked={allShapes} onChange={(e) => setAllShapes(e.target.checked)} className="size-3.5 accent-[var(--accent)]" />
                Show all shapes{sameShape.length > 0 && !allShapes && !madeFor ? ` (${screenDesigns.length - sameShape.length} more)` : ''}
              </label>
              <label className="flex items-center gap-1.5 text-xs text-muted">
                <input type="checkbox" checked={asMade} onChange={(e) => setAsMade(e.target.checked)} className="size-3.5 accent-[var(--accent)]" />
                Preview at the size each is made for
              </label>
            </div>
            <ul className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Whole-screen designs">
              {shown.map(({ design, size, panels: parts, other }, i) => {
                const made = sizeOf(design);
                return (
                  <motion.li
                    key={design.id}
                    initial={reduced ? false : { opacity: 0, y: 10 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ duration: 0.3, ease: EASE.out, delay: reduced ? 0 : Math.min(i, 8) * 0.025 }}
                    className="motion-hover group flex flex-col overflow-hidden rounded-lg border border-border bg-surface transition-[border-color,box-shadow] hover:border-accent/60 hover:shadow-lg"
                  >
                    <div className="flex h-48 items-center justify-center overflow-hidden bg-[#0d1117]">
                      {parts.length > 0 ? <ScreenMiniature size={size} panels={parts} sources={sourceMap} history={history} box={{ width: 330, height: 172 }} /> : <p className="px-4 text-center text-xs text-white/60">This PC has none of the readings this design shows.</p>}
                    </div>
                    <div className="flex flex-1 items-start justify-between gap-2 p-3">
                      <div className="min-w-0">
                        <h3 className="text-sm font-semibold">{design.name}</h3>
                        <p className="text-xs text-muted">{design.description}</p>
                        <p className={cn('mt-1 text-[11px]', other ? 'text-warning' : 'text-muted')}>
                          Made for {made.name}, {made.width} × {made.height}
                          {other ? ': another shape, stretched on this screen' : ''}
                        </p>
                      </div>
                      <Button size="sm" variant="outline" disabled={parts.length === 0} onClick={() => onUseScreen(design)} aria-label={`Use ${design.name} on this screen`}>
                        <Maximize aria-hidden /> Use
                      </Button>
                    </div>
                  </motion.li>
                );
              })}
            </ul>
          </>
        )}
      </div>
    </Dialog>
  );
}

/** A design drawn at 100 % off screen, to know its size. */
function Measure({ panel, sources, onSize }: { panel: OverlayPanel; sources: ReadonlyMap<string, OverlaySource>; onSize: (size: { width: number; height: number }) => void }) {
  const ref = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const element = ref.current;
    if (element && element.offsetWidth > 0) onSize({ width: element.offsetWidth, height: element.offsetHeight });
  });
  return <PanelPreview ref={ref} panel={panel} sources={sources} scale={1} glide={false} />;
}

/** A screen of a size with panels on it, shrunk into a box: what a whole-screen design looks like there. */
function ScreenMiniature({ size, panels, sources, history, box }: { size: { width: number; height: number }; panels: OverlayPanel[]; sources: ReadonlyMap<string, OverlaySource>; history: History; box: { width: number; height: number } }) {
  const s = Math.min(box.width / Math.max(1, size.width), box.height / Math.max(1, size.height));
  const screen = { width: size.width * s, height: size.height * s };
  return (
    <span className="relative block overflow-hidden rounded-[3px] bg-black shadow-[0_0_0_3px_#30363d] transition-transform duration-300 ease-out group-hover:scale-[1.03]" style={screen}>
      {panels.map((panel) => (
        <MiniPanel key={panel.id} panel={panel} sources={sources} history={history} scale={s} monitor={screen} />
      ))}
    </span>
  );
}
