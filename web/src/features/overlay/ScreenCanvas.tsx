import { Expand, LayoutGrid, Maximize2, Minimize2, Move, Scaling } from 'lucide-react';
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { createPortal } from 'react-dom';
import type { OverlayDisplay, OverlayPanel, OverlaySource } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { GRIDS, arrange, fittedToScreen, inCell, maximized, placement, restored, shareOf, type History, type ScreenBox } from './model';
import { PanelPreview, ShrunkPreview, metrics } from './PanelPreview';
import { CharacterSprite } from './characters/CharacterSprite';
import { Company } from './characters/company';
import { useCharacterWeather } from './characters/weather';

/** The width an element has (follows resizes). */
function useWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(0);
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return undefined;
    // Without a layout (tests, a hidden tab) assume a common width.
    const measure = () => setWidth(element.clientWidth || 960);
    measure();
    if (typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  return [ref, width] as const;
}

const resolution = (d: OverlayDisplay) => `${d.width} × ${d.height}${d.refreshHz ? ` · ${Math.round(d.refreshHz)} Hz` : ''}${d.scale !== 1 ? ` · ${Math.round(d.scale * 100)} %` : ''}`;

export interface ScreenMapProps {
  displays: OverlayDisplay[];
  panelsOf: (key: string) => OverlayPanel[];
  /** A USB screen's background color (it shows no desktop). */
  backdropOf?: (key: string) => string;
  sources: ReadonlyMap<string, OverlaySource>;
  history?: History;
  selected: string | null;
  onSelect: (key: string) => void;
}

/** A USB screen's state as its badge reads. */
const DEVICE_STATUS: Record<string, { text: string; tone: string }> = {
  on: { text: 'Showing your panels', tone: 'bg-success/20 text-success' },
  available: { text: 'Not used', tone: 'bg-white/10 text-white/70' },
  busy: { text: 'In use by another program', tone: 'bg-warning/20 text-warning' },
  problem: { text: 'Problem', tone: 'bg-danger/20 text-danger' },
};

/**
 * All monitors as they are arranged on the desktop, each with a miniature of its panels, and the USB screens under
 * them (they are not on the desktop), each at its own shape; choosing one edits it.
 */
export function ScreenMap({ displays, panelsOf, backdropOf, sources, history, selected, onSelect }: ScreenMapProps) {
  const monitors = displays.filter((d) => d.kind !== 'device');
  const devices = displays.filter((d) => d.kind === 'device');
  return (
    <div className="flex flex-col gap-3">
      {monitors.length > 0 && <MonitorMap displays={monitors} panelsOf={panelsOf} sources={sources} history={history} selected={selected} onSelect={onSelect} />}
      {devices.length > 0 && (
        <div className="flex flex-col gap-1.5" role="group" aria-label="USB screens">
          <span className="text-xs font-medium uppercase tracking-wide text-muted">USB screens</span>
          <div className="flex flex-wrap items-end gap-3">
            {devices.map((display) => {
              const panels = panelsOf(display.key).filter((p) => !p.hidden);
              // A row's height for all; a long bar is narrowed to fit.
              const h = Math.min(72, (300 * display.height) / Math.max(1, display.width));
              const w = (h * display.width) / Math.max(1, display.height);
              const status = DEVICE_STATUS[display.status ?? 'available'] ?? DEVICE_STATUS.available!;
              return (
                <button
                  key={display.key}
                  type="button"
                  onClick={() => onSelect(display.key)}
                  aria-pressed={selected === display.key}
                  aria-label={`USB screen ${display.number}: ${display.name}, ${status.text}, ${panels.length} panel${panels.length === 1 ? '' : 's'}`}
                  className="flex flex-col items-start gap-1 text-left"
                >
                  <span
                    className={cn('relative block overflow-hidden rounded-[5px] border-[3px]', selected === display.key ? 'border-accent shadow-[0_0_0_3px_rgba(56,189,248,0.25)]' : 'border-[#30363d] hover:border-accent/60')}
                    style={{ width: w, height: h, background: backdropOf?.(display.key) ?? '#000000' }}
                  >
                    {panels.map((panel) => (
                      <MiniPanel key={panel.id} panel={panel} sources={sources} history={history} scale={(w / Math.max(1, display.width)) * display.scale} monitor={{ width: w - 6, height: h - 6 }} />
                    ))}
                  </span>
                  <span className="flex items-center gap-1.5 text-xs">
                    <span className="rounded bg-surface-muted px-1.5 tabular-nums">{display.number}</span>
                    <span className="max-w-[16rem] truncate">{display.name}</span>
                    <span className={cn('rounded px-1.5 py-px text-[10px]', status.tone)}>{display.status === 'busy' && display.detail ? display.detail : status.text}</span>
                  </span>
                </button>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}

/** The monitors as they are arranged on the desktop. */
function MonitorMap({ displays, panelsOf, sources, history, selected, onSelect }: ScreenMapProps) {
  const [ref, width] = useWidth<HTMLDivElement>();
  const height = Math.max(160, Math.min(300, width * 0.32));
  const { scale, rects } = arrange(displays, width, height, 16);
  return (
    <div ref={ref} className="relative w-full" style={{ height }} role="group" aria-label="Your screens">
      {width > 0 &&
        rects.map(({ display, left, top, width: w, height: h }) => {
          const panels = panelsOf(display.key).filter((p) => !p.hidden);
          return (
            <button
              key={display.key}
              type="button"
              onClick={() => onSelect(display.key)}
              aria-pressed={selected === display.key}
              aria-label={`Screen ${display.number}: ${display.name}, ${panels.length} panel${panels.length === 1 ? '' : 's'}`}
              className={cn(
                'absolute overflow-hidden rounded-[6px] border-[3px] bg-[#0b1220] text-left transition-shadow',
                selected === display.key ? 'border-accent shadow-[0_0_0_3px_rgba(56,189,248,0.25)]' : 'border-[#30363d] hover:border-accent/60',
              )}
              style={{ left: left + 3, top: top + 3, width: w - 6, height: h - 6, backgroundImage: 'linear-gradient(160deg, #13233f 0%, #0b1220 60%)' }}
            >
              <span className="absolute left-1.5 top-1 z-10 flex items-center gap-1 text-[11px] font-medium text-white/90">
                <span className="rounded bg-white/15 px-1.5 tabular-nums">{display.number}</span>
                <span className="truncate">{display.name}</span>
                {display.primary && <span className="text-white/50">main</span>}
              </span>
              {panels.map((panel) => (
                <MiniPanel key={panel.id} panel={panel} sources={sources} history={history} scale={scale * display.scale} monitor={{ width: w - 6, height: h - 6 }} />
              ))}
            </button>
          );
        })}
    </div>
  );
}

/** A panel on a screen's miniature, where it will be. */
export function MiniPanel({ panel, sources, history, scale, monitor }: { panel: OverlayPanel; sources: ReadonlyMap<string, OverlaySource>; history?: History; scale: number; monitor: { width: number; height: number } }) {
  const [size, setSize] = useState({ width: 0, height: 0 });
  const { left, top } = placement(panel, monitor, size, metrics(panel.style, scale).margin);
  // Drawn at a readable size and shrunk, so the miniature keeps the panel's true proportions.
  return <ShrunkPreview panel={panel} sources={sources} history={history} scale={scale} onSize={setSize} className="pointer-events-none" style={{ position: 'absolute', left, top }} />;
}

export interface ScreenEditorProps {
  display: OverlayDisplay;
  panels: OverlayPanel[];
  sources: ReadonlyMap<string, OverlaySource>;
  history?: History;
  selected: string | null;
  onSelect: (id: string | null) => void;
  onMove: (id: string, x: number, y: number) => void;
  onRemove: (id: string) => void;
  /** The selected panel's corner was dragged: its new fixed size at 100 % scaling, and the place that keeps its top-left corner. */
  onResize?: (id: string, width: number, height: number, x: number, y: number) => void;
  /** Panels drawn larger than true scale so they are easy to grab (positions keep their meaning). */
  zoom?: number;
  /** A USB screen's background color, instead of a desktop. */
  backdrop?: string;
  /** A panel changed by its control box (filled the screen, put in a grid cell, fitted, restored). */
  onPanel?: (panel: OverlayPanel) => void;
  /** A reading's value clicked on a panel: its settings opened. */
  onReading?: (panel: string, index: number) => void;
  /** The panels' characters are shown (the layout's switch). */
  characters?: boolean;
  /** The screen's own characters (on the whole screen, in front of its panels), on their stand-in panel. */
  stage?: OverlayPanel | null;
  /** The most height the screen may take at Fit (the v2 editor fits it in the window). */
  fitHeight?: number;
  /** A character clicked: its panel's id (null for the screen's own) and its id. */
  onCharacter?: (panel: string | null, character: string) => void;
}

/**
 * One monitor, large, with its panels where they will be: drag a panel (or select it and use the arrow keys, Shift for
 * bigger steps) to place it. Its position is kept as a share of the room it leaves, so a panel at an edge stays at
 * that edge whatever its size.
 */
export function ScreenEditor({ display, panels, sources, history, selected, onSelect, onMove, onRemove, onResize, zoom = 1, backdrop, onPanel, onReading, characters: showCharacters = true, stage = null, fitHeight, onCharacter }: ScreenEditorProps) {
  const [ref, width] = useWidth<HTMLDivElement>();
  // Zooming enlarges the whole screen, panels with it (scrolled within the card), so panels always keep their true
  // size against the screen they are on.
  const aspect = display.width / Math.max(1, display.height);
  const canvasWidth = Math.max(0, fitHeight ? Math.min(width - 24, fitHeight * aspect) : width - 24) * zoom;
  const canvasHeight = canvasWidth * (display.height / Math.max(1, display.width));
  const scale = canvasWidth / Math.max(1, display.width);
  const sizes = useRef(new Map<string, { width: number; height: number }>());
  const [, rerender] = useState(0);
  const drag = useRef<{ id: string; startX: number; startY: number; left: number; top: number; moving?: boolean } | null>(null);
  const pixels = scale * display.scale;
  // The screen's characters together: the mouse over it, where each was drawn (so two can meet), the weather.
  const company = useRef(new Company()).current;
  useCharacterWeather(company);
  const pointAt = (e: PointerEvent<HTMLDivElement>) => {
    const r = e.currentTarget.getBoundingClientRect();
    // The screen may be drawn zoomed: back to its own pixels.
    const k = e.currentTarget.offsetWidth ? r.width / e.currentTarget.offsetWidth : 1;
    company.cursor = { x: (e.clientX - r.left) / k, y: (e.clientY - r.top) / k };
  };

  const sizeOf = (id: string) => sizes.current.get(id) ?? { width: 0, height: 0 };
  // Panels are placed by their box (their drawn size less the shadow's margin), as the recorder places them.
  const marginOf = (panel: OverlayPanel) => metrics(panel.style, pixels).margin;
  const boxOf = (panel: OverlayPanel) => ({ width: Math.max(0, sizeOf(panel.id).width - 2 * marginOf(panel)), height: Math.max(0, sizeOf(panel.id).height - 2 * marginOf(panel)) });
  const room = (id: string) => {
    const panel = panels.find((p) => p.id === id);
    const box = panel ? boxOf(panel) : { width: 0, height: 0 };
    return { width: canvasWidth - box.width, height: canvasHeight - box.height };
  };

  const begin = (e: PointerEvent<HTMLDivElement>, panel: OverlayPanel) => {
    const at = placement(panel, { width: canvasWidth, height: canvasHeight }, sizeOf(panel.id), marginOf(panel));
    // Where its box is (the drag moves the box).
    const left = at.left + marginOf(panel);
    const top = at.top + marginOf(panel);
    drag.current = { id: panel.id, startX: e.clientX, startY: e.clientY, left, top };
    e.currentTarget.setPointerCapture?.(e.pointerId);
    onSelect(panel.id);
  };
  const move = (e: PointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d) return;
    // A click is not a move: a few pixels' wobble while choosing a panel left it moved (and asked to save).
    if (!d.moving && Math.hypot(e.clientX - d.startX, e.clientY - d.startY) < 4) return;
    d.moving = true;
    const r = room(d.id);
    onMove(d.id, shareOf(d.left + e.clientX - d.startX, r.width), shareOf(d.top + e.clientY - d.startY, r.height));
  };
  const end = () => {
    drag.current = null;
  };
  // The characters of the panels, around each panel's box (behind the panels or in front of them).
  const characters = (behind: boolean) =>
    panels.flatMap((panel) => {
      if (!showCharacters || !panel.characters?.length || sizeOf(panel.id).width === 0) return [];
      const at = placement(panel, { width: canvasWidth, height: canvasHeight }, sizeOf(panel.id), marginOf(panel));
      const box = boxOf(panel);
      const left = at.left + marginOf(panel);
      const top = at.top + marginOf(panel);
      const area = { left, top, right: left + box.width, bottom: top + box.height };
      return panel.characters
        .filter((c) => (c.behind && c.place !== 'inside') === behind)
        .map((c) => <CharacterSprite key={`${panel.id}-${c.id}`} onClick={onCharacter && (() => onCharacter(panel.id, c.id))} character={c} panel={area} screen={{ left: 0, top: 0, right: canvasWidth, bottom: canvasHeight }} pixels={pixels} readings={panel.readings} sources={sources} company={company} self={`${panel.id}/${c.id}`} />);
    });

  const keys = (e: KeyboardEvent<HTMLDivElement>, panel: OverlayPanel) => {
    const step = e.shiftKey ? 0.1 : 0.01;
    const moves: Record<string, [number, number]> = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
    if (moves[e.key]) {
      e.preventDefault();
      const [dx, dy] = moves[e.key]!;
      onMove(panel.id, Math.min(1, Math.max(0, panel.x + dx)), Math.min(1, Math.max(0, panel.y + dy)));
    } else if (e.key === 'Delete' || e.key === 'Backspace') {
      e.preventDefault();
      onRemove(panel.id);
    }
  };

  return (
    <div ref={ref} className="w-full">
      {canvasWidth > 0 && (
        <div className={cn('flex flex-col', zoom > 1 ? 'max-h-[70vh] overflow-auto' : 'items-center')}>
        <div className="mx-auto flex w-fit flex-col items-center">
          {/* The monitor: bezel, screen, stand. */}
          <div className="rounded-[14px] border border-[#2d333b] bg-[#161b22] p-3 shadow-lg">
            <div
              role="group"
              aria-label={`Screen ${display.number}: ${display.name}`}
              className="relative overflow-hidden rounded-[4px]"
              style={{ width: canvasWidth, height: canvasHeight, ...(backdrop ? { background: backdrop } : { backgroundImage: 'linear-gradient(160deg, #1d3b66 0%, #0b1220 55%, #111827 100%)' }) }}
              onPointerDown={(e) => {
                if (e.target === e.currentTarget) onSelect(null);
              }}
              onPointerMove={pointAt}
              onPointerLeave={() => {
                company.cursor = null;
              }}
            >
              {characters(true)}
              {panels.map((panel) => (
                <EditablePanel
                  key={panel.id}
                  panel={panel}
                  sources={sources}
                  history={history}
                  scale={scale * display.scale}
                  canvas={{ width: canvasWidth, height: canvasHeight }}
                  selected={selected === panel.id}
                  onSize={(size) => {
                    const old = sizes.current.get(panel.id);
                    if (!old || old.width !== size.width || old.height !== size.height) {
                      sizes.current.set(panel.id, size);
                      rerender((n) => n + 1);
                    }
                  }}
                  onPointerDown={(e) => begin(e, panel)}
                  onPointerMove={move}
                  onPointerUp={end}
                  onPanel={onPanel}
                  onReading={onReading ? (index) => onReading(panel.id, index) : undefined}
                  onKeyDown={(e) => keys(e, panel)}
                  onFocus={() => onSelect(panel.id)}
                  onResize={
                    onResize
                      ? (width, height, boxLeft, boxTop) =>
                          // The top-left corner stays where it was: its share of the room the new size leaves.
                          onResize(panel.id, width, height, shareOf(boxLeft, canvasWidth - width * pixels), shareOf(boxTop, canvasHeight - height * pixels))
                      : undefined
                  }
                  label={`${panel.title || 'Panel'} (${panel.readings.length} reading${panel.readings.length === 1 ? '' : 's'}) on ${display.name}`}
                />
              ))}
              {characters(false)}
              {showCharacters &&
                (stage?.characters ?? []).map((c) => (
                  <CharacterSprite key={`screen-${c.id}`} onClick={onCharacter && (() => onCharacter(null, c.id))} character={c} panel={{ left: 0, top: 0, right: canvasWidth, bottom: canvasHeight }} screen={{ left: 0, top: 0, right: canvasWidth, bottom: canvasHeight }} pixels={pixels} readings={stage!.readings} sources={sources} company={company} self={`~screen/${c.id}`} />
                ))}
              {onPanel &&
                panels
                  .filter((p) => p.id === selected)
                  .map((p) => (
                    <PanelControls
                      key={p.id}
                      panel={p}
                      sources={sources}
                      at={placement(p, { width: canvasWidth, height: canvasHeight }, sizeOf(p.id), marginOf(p))}
                      size={sizeOf(p.id)}
                      canvas={{ width: canvasWidth, height: canvasHeight }}
                      box={{ width: display.width / display.scale, height: display.height / display.scale }}
                      pixels={pixels}
                      onPanel={onPanel}
                    />
                  ))}
              {panels.length === 0 && <p className="absolute inset-0 flex items-center justify-center text-sm text-white/50">No readings on this screen yet: add a panel.</p>}
            </div>
          </div>
          <div className="h-5 w-24 bg-gradient-to-b from-[#2d333b] to-[#161b22]" style={{ clipPath: 'polygon(20% 0, 80% 0, 100% 100%, 0 100%)' }} aria-hidden />
          <div className="h-2 w-44 rounded-full bg-[#2d333b]" aria-hidden />
          <p className="mt-1.5 text-xs text-muted">
            Screen {display.number}: {display.name} · {resolution(display)}
            {display.primary ? ' · main display' : ''}
          </p>
        </div>
        </div>
      )}
    </div>
  );
}

/**
 * The selected panel's control box, at its top right inside the screen: fill the screen, put it in a cell of a grid
 * (a picker of grids, each cell a button), fit it to the screen keeping its proportions, and restore its size and
 * place from before.
 */
function PanelControls({ panel, sources, at, size, canvas, box, pixels, onPanel }: { panel: OverlayPanel; sources: ReadonlyMap<string, OverlaySource>; at: { left: number; top: number }; size: { width: number; height: number }; canvas: { width: number; height: number }; box: ScreenBox; pixels: number; onPanel: (panel: OverlayPanel) => void }) {
  const [grids, setGrids] = useState(false);
  // Its readings at their own size (100 %, automatic size): filling a smaller box makes its text smaller to fit.
  const [content, setContent] = useState<ScreenBox | undefined>(undefined);
  const bar = 4 * 26 + 8;
  const barHeight = 34;
  // Beside the panel, never over it (nor its resize grip): above it, else below it; over its corner only when it fills
  // the screen.
  const left = Math.max(2, Math.min(canvas.width - bar - 2, at.left + size.width - bar - 12));
  const above = at.top - barHeight - 8;
  const below = at.top + size.height + 8;
  const top = above >= 2 ? above : below + barHeight <= canvas.height - 2 ? below : Math.max(2, at.top + 4);
  const stop = (e: PointerEvent) => e.stopPropagation();
  const fit = () => {
    // Its size now at 100 % scaling (its fixed size, or its readings' own), and the part that does not follow the text.
    const margin = metrics(panel.style, pixels).margin;
    const drawn = panel.width && panel.height ? { width: panel.width, height: panel.height } : (content ?? { width: (size.width - 2 * margin) / pixels, height: (size.height - 2 * margin) / pixels });
    onPanel(fittedToScreen(panel, box, drawn, 2 * panel.style.padding));
  };
  const button = 'flex size-6 items-center justify-center rounded text-white/90 hover:bg-white/15 disabled:opacity-40';
  return (
    <div className="absolute z-20" style={{ left, top }} onPointerDown={stop} onKeyDown={(e) => e.key === 'Escape' && setGrids(false)}>
      <div role="toolbar" aria-label={`Controls of ${panel.title || 'the panel'}`} className="flex items-center gap-0.5 rounded-md border border-white/15 bg-black/75 p-1 shadow-lg backdrop-blur">
        <button type="button" className={button} aria-label="Fill the screen" title="Fill the screen" onClick={() => onPanel(maximized(panel, box, content))}>
          <Maximize2 className="size-3.5" aria-hidden />
        </button>
        <button type="button" className={button} aria-label="Put in a grid cell" title="Put in a grid cell" aria-expanded={grids} onClick={() => setGrids((g) => !g)}>
          <LayoutGrid className="size-3.5" aria-hidden />
        </button>
        <button type="button" className={button} aria-label="Fit to the screen" title="As large as fits the screen, keeping its proportions" onClick={fit}>
          <Expand className="size-3.5" aria-hidden />
        </button>
        <button type="button" className={button} aria-label="Restore size and place" title="Back to its size and place before" disabled={!panel.restore} onClick={() => onPanel(restored(panel))}>
          <Minimize2 className="size-3.5" aria-hidden />
        </button>
      </div>
      {createPortal(
        <div aria-hidden className="pointer-events-none fixed left-[-10000px] top-0 opacity-0">
          <NaturalSize panel={panel} sources={sources} onSize={setContent} />
        </div>,
        document.body,
      )}
      {grids && (
        <div role="group" aria-label="Grid cells" className="mt-1 flex flex-col gap-2 rounded-md border border-white/15 bg-black/85 p-2 text-[10px] text-white/70 shadow-lg backdrop-blur">
          {GRIDS.map((grid) => (
            <div key={grid.name} className="flex items-center gap-2">
              <span
                className="grid gap-0.5"
                style={{ gridTemplateColumns: `repeat(${grid.columns}, 1fr)`, width: 48, aspectRatio: `${box.width} / ${box.height}` }}
              >
                {Array.from({ length: grid.columns * grid.rows }, (_, i) => (
                  <button
                    key={i}
                    type="button"
                    aria-label={`${grid.name}: cell ${i + 1}`}
                    title={`${grid.name}: cell ${i + 1}`}
                    className="rounded-[2px] bg-white/20 hover:bg-accent"
                    onClick={() => {
                      onPanel(inCell(panel, box, grid, i % grid.columns, Math.floor(i / grid.columns), content));
                      setGrids(false);
                    }}
                  />
                ))}
              </span>
              <span>{grid.name}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/** A panel's box at its readings' own size (automatic, 100 %), measured off screen. */
function NaturalSize({ panel, sources, onSize }: { panel: OverlayPanel; sources: ReadonlyMap<string, OverlaySource>; onSize: (size: ScreenBox) => void }) {
  const ref = useRef<HTMLDivElement>(null);
  const auto = { ...panel, width: null, height: null, restore: null };
  const margin = metrics(panel.style, 1).margin;
  const last = useRef({ width: -1, height: -1 });
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element || element.offsetWidth <= 0) return;
    const size = { width: element.offsetWidth - 2 * margin, height: element.offsetHeight - 2 * margin };
    if (size.width !== last.current.width || size.height !== last.current.height) {
      last.current = size;
      onSize(size);
    }
  });
  return <PanelPreview ref={ref} panel={auto} sources={sources} scale={1} />;
}

interface EditablePanelProps {
  panel: OverlayPanel;
  sources: ReadonlyMap<string, OverlaySource>;
  history?: History;
  scale: number;
  canvas: { width: number; height: number };
  selected: boolean;
  label: string;
  onSize: (size: { width: number; height: number }) => void;
  onPointerDown: (e: PointerEvent<HTMLDivElement>) => void;
  onPointerMove: (e: PointerEvent<HTMLDivElement>) => void;
  onPointerUp: () => void;
  /** The panel changed here (a value moved or sized from its menu). */
  onPanel?: (panel: OverlayPanel) => void;
  /** A value clicked: its reading's index. */
  onReading?: (index: number) => void;
  onKeyDown: (e: KeyboardEvent<HTMLDivElement>) => void;
  onFocus: () => void;
  /** A new fixed size at 100 % scaling, from dragging the corner, and where its box was when the drag began (canvas pixels). */
  onResize?: (width: number, height: number, boxLeft: number, boxTop: number) => void;
}

function EditablePanel({ panel, sources, history, scale, canvas, selected, label, onSize, onPointerDown, onPointerMove, onPointerUp, onKeyDown, onFocus, onResize, onPanel, onReading }: EditablePanelProps) {
  const margin = metrics(panel.style, scale).margin;
  // A reading's value on the selected panel: pointed at, a small menu offers to move it (it follows the mouse, sitting on
  // the left, middle or right third of its tile, and its top, middle or bottom third) or to size it (the mouse up or down);
  // a click sets it, Escape puts it back. A value double-clicked opens its reading's settings.
  const [hover, setHover] = useState<{ index: number; left: number; top: number } | null>(null);
  const [mode, setMode] = useState<{ kind: 'move' | 'size'; index: number; startY: number; startSize: number; before: OverlayPanel['readings'][number]['look'] } | null>(null);
  const valueAt = (index: number) => ref.current?.querySelectorAll<HTMLElement>('[data-value]')[index] ?? null;
  const indexOf = (target: EventTarget | null) => {
    const value = (target as HTMLElement | null)?.closest?.<HTMLElement>('[data-value]');
    return value && ref.current?.contains(value) ? [...ref.current.querySelectorAll('[data-value]')].indexOf(value) : -1;
  };
  const visualOfReading = (index: number) => {
    const reading = panel.readings[index];
    return reading ? (reading.visual ?? (reading.bar ? 'bar' : 'none')) : 'none';
  };
  const setLook = (index: number, look: OverlayPanel['readings'][number]['look']) =>
    onPanel?.({ ...panel, readings: panel.readings.map((x, i) => (i === index ? { ...x, look } : x)) });
  const follow = (e: PointerEvent<HTMLDivElement>) => {
    if (!mode) return false;
    const reading = panel.readings[mode.index];
    const value = valueAt(mode.index);
    if (!reading || !value) return true;
    if (mode.kind === 'size') {
      const size = Math.round(Math.min(300, Math.max(50, mode.startSize + (mode.startY - e.clientY))) / 5) * 5;
      if (size !== (reading.look?.valueSize ?? 100)) setLook(mode.index, { ...(reading.look ?? {}), valueSize: size === 100 ? null : size });
      return true;
    }
    const tile = value.closest<HTMLElement>('[data-tile]');
    const room = (tile ?? value).getBoundingClientRect();
    if (room.width <= 0) return true;
    const third = (at: number, from: number, size: number) => Math.min(2, Math.max(0, Math.floor(((at - from) / size) * 3)));
    const visual = visualOfReading(mode.index);
    const align = (['left', 'center', 'right'] as const)[third(e.clientX, room.left, room.width)];
    const vertical = tile && visual !== 'graph' && visual !== 'line' && visual !== 'columns' ? (['top', 'middle', 'bottom'] as const)[third(e.clientY, room.top, room.height)] : undefined;
    if (align !== reading.look?.align || (vertical && vertical !== reading.look?.vertical)) setLook(mode.index, { ...(reading.look ?? {}), align, ...(vertical ? { vertical } : {}) });
    return true;
  };
  useEffect(() => {
    if (!mode) return undefined;
    const keys = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape') {
        setLook(mode.index, mode.before);
        setMode(null);
      }
    };
    // A click anywhere else sets it too.
    const elsewhere = (e: globalThis.PointerEvent) => {
      if (!(e.target instanceof Node) || !ref.current?.parentElement?.contains(e.target)) setMode(null);
    };
    window.addEventListener('keydown', keys);
    window.addEventListener('pointerdown', elsewhere);
    return () => {
      window.removeEventListener('keydown', keys);
      window.removeEventListener('pointerdown', elsewhere);
    };
  });
  const resizing = useRef<{ x: number; y: number; width: number; height: number; left: number; top: number } | null>(null);
  const beginResize = (e: PointerEvent<HTMLSpanElement>) => {
    e.stopPropagation();
    e.preventDefault();
    resizing.current = { x: e.clientX, y: e.clientY, width: size.width - 2 * margin, height: size.height - 2 * margin, left: left + margin, top: top + margin };
    e.currentTarget.setPointerCapture?.(e.pointerId);
  };
  const moveResize = (e: PointerEvent<HTMLSpanElement>) => {
    const r = resizing.current;
    if (!r || !onResize) return;
    e.stopPropagation();
    onResize(Math.round(Math.min(4000, Math.max(40, (r.width + e.clientX - r.x) / scale))), Math.round(Math.min(3000, Math.max(20, (r.height + e.clientY - r.y) / scale))), r.left, r.top);
  };
  const ref = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return;
    const next = { width: element.offsetWidth, height: element.offsetHeight };
    if (next.width !== size.width || next.height !== size.height) setSize(next);
  });
  useEffect(() => {
    onSize(size);
  }, [size, onSize]);
  const { left, top } = placement(panel, canvas, size, margin);
  return (
    <div
      role="button"
      tabIndex={0}
      aria-label={label}
      aria-pressed={selected}
      className={cn('absolute cursor-move touch-none outline-none', panel.hidden && 'opacity-40')}
      onPointerDown={(e) => {
        if (mode) {
          // Set where it is.
          e.stopPropagation();
          setMode(null);
          return;
        }
        onPointerDown(e);
      }}
      onPointerMove={(e) => {
        if (follow(e)) return;
        if (selected && onPanel && !e.buttons) {
          const index = indexOf(e.target);
          const value = index >= 0 ? valueAt(index) : null;
          const root = e.currentTarget.getBoundingClientRect();
          if (value) {
            const r = value.getBoundingClientRect();
            if (hover?.index !== index) setHover({ index, left: r.left - root.left, top: r.top - root.top });
          } else if (hover && !(e.target as HTMLElement).closest?.('[data-value-menu]')) setHover(null);
        }
        onPointerMove(e);
      }}
      onPointerUp={() => onPointerUp()}
      onDoubleClick={(e) => {
        // A value double-clicked: its reading's settings opened.
        const index = indexOf(e.target);
        if (index >= 0) onReading?.(index);
      }}
      onPointerCancel={() => onPointerUp()}
      onPointerLeave={() => !mode && setHover(null)}
      onKeyDown={onKeyDown}
      onFocus={onFocus}
      style={{ left, top, cursor: mode ? (mode.kind === 'move' ? 'move' : 'ns-resize') : undefined }}
    >
      <PanelPreview ref={ref} panel={panel} sources={sources} history={history} scale={scale} selected={selected} />
      {selected && onPanel && (hover || mode) && (
        <span
          data-value-menu
          role="toolbar"
          aria-label={`${panel.readings[(mode ?? hover)!.index]?.label || panel.readings[(mode ?? hover)!.index]?.source || 'Value'}: its value`}
          className="absolute z-10 flex items-center gap-0.5 rounded-md border border-border bg-surface p-0.5 text-[11px] shadow-lg"
          style={{ left: hover?.left ?? 0, top: Math.max(0, (hover?.top ?? 0) - 26) }}
          onPointerDown={(e) => e.stopPropagation()}
        >
          {mode ? (
            <span className="px-1.5 py-0.5 text-muted">{mode.kind === 'move' ? 'Move the mouse, click to set (Esc: back)' : 'Mouse up or down, click to set (Esc: back)'}</span>
          ) : (
            <>
              {visualOfReading(hover!.index) !== 'ring' && visualOfReading(hover!.index) !== 'arc' && visualOfReading(hover!.index) !== 'speed' && (
                <button
                  type="button"
                  className="flex items-center gap-1 rounded px-1.5 py-0.5 hover:bg-surface-muted"
                  onClick={(e) => setMode({ kind: 'move', index: hover!.index, startY: e.clientY, startSize: 100, before: panel.readings[hover!.index]?.look })}
                >
                  <Move className="size-3" aria-hidden /> Move
                </button>
              )}
              <button
                type="button"
                className="flex items-center gap-1 rounded px-1.5 py-0.5 hover:bg-surface-muted"
                onClick={(e) => setMode({ kind: 'size', index: hover!.index, startY: e.clientY, startSize: panel.readings[hover!.index]?.look?.valueSize ?? 100, before: panel.readings[hover!.index]?.look })}
              >
                <Scaling className="size-3" aria-hidden /> Size
              </button>
            </>
          )}
        </span>
      )}
      {selected && onResize && (
        <span
          role="presentation"
          title="Drag to resize (the panel then keeps that size)"
          className="absolute size-3.5 cursor-nwse-resize rounded-sm border-2 border-accent bg-background shadow"
          style={{ right: margin - 6, bottom: margin - 6 }}
          onPointerDown={beginResize}
          onPointerMove={moveResize}
          onPointerUp={(e) => {
            e.stopPropagation();
            resizing.current = null;
          }}
        />
      )}
    </div>
  );
}
