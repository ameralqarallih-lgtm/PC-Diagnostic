import { ArrowDown, ArrowLeft, ArrowRight, ArrowUp, Check as CheckIcon, ChevronDown, Copy, Eye, EyeOff, GripVertical, Minus, Palette, Plus, Search, Trash2, Wand2, X, RotateCcw } from 'lucide-react';
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { useConfirm } from '@/components/ui/confirm';
import { TabPill } from '@/components/ui/tab-indicator';
import type { OverlayDisplay, OverlayPanel, OverlayPattern, OverlayReading, OverlaySource, OverlayStyle, OverlayVisual } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { ICON_NAMES, ICON_PATHS, type IconId } from './icons';
import { DESIGNS } from './designs';
import { ANCHORS, FONTS, IMAGE_FITS, LAYOUTS, MAX_READINGS, MAX_TEXT, PATTERNS, SHOWS, VALUE_ALIGNS, VALUE_VERTICALS, VISUALS, formatSource, hasTotal, imageUrl, maxOf, autoMax, needsScale, pack, patternSvg, patternTile, presetOf, readAs, readsAs, shown, valueSourceOf, visualOf, type History, MOTIONS, BACKDROPS } from './model';
import { ART_THEMES, isDrawnArt } from './art';
import { BackgroundChooser } from './BackgroundChooser';
import { GENERATIVE } from './generative';
import { isAnimationId, isGenerative } from './liveArt';
import { ShrunkPreview } from './PanelPreview';
import { CharactersTab } from './characters/CharactersTab';

export interface PanelInspectorProps {
  panel: OverlayPanel;
  /** A reading to open (clicked on the screen): its index, and a count that changes each time it is asked. */
  focusReading?: { index: number; at: number } | null;
  /** A character to open (clicked on the screen). */
  focusCharacter?: { id: string; at: number } | null;
  sources: OverlaySource[];
  sourceMap: ReadonlyMap<string, OverlaySource>;
  screens: OverlayDisplay[];
  screenKey: string;
  /** The ready-made looks. */
  presets: { id: string; name: string; description: string; style: OverlayStyle }[];
  history?: History;
  onChange: (panel: OverlayPanel) => void;
  onRemove: () => void;
  onDuplicate: () => void;
  onMoveToScreen: (key: string) => void;
  /** Gives every panel (on every screen) this panel's look. */
  onApplyToAll: (style: OverlayStyle) => void;
}

type Tab = 'readings' | 'layout' | 'look' | 'characters';

const input = 'h-8 w-full rounded-md border border-border bg-background px-2 text-sm';

/** Everything about one panel: what it shows, where and how it is laid out, and how it looks. */
/**
 * Whether a panel is still as its design makes it: its content compared with empty, false and absent values alike, and
 * its id, place, visibility and saved place left out.
 */
function sameAsDesign(panel: Partial<OverlayPanel>, design: Partial<OverlayPanel>): boolean {
  const left = new Set(['id', 'x', 'y', 'hidden', 'restore']);
  const normal = (value: unknown, top = false): unknown => {
    if (Array.isArray(value)) return value.map((v) => normal(v));
    if (value && typeof value === 'object')
      return Object.fromEntries(
        Object.keys(value)
          .filter((k) => !(top && left.has(k)))
          .map((k) => [k, normal((value as Record<string, unknown>)[k])] as const)
          .filter(([, v]) => v !== null && v !== undefined && v !== false)
          .sort(([a], [b]) => a.localeCompare(b)),
      );
    return value;
  };
  return JSON.stringify(normal(panel, true)) === JSON.stringify(normal(design, true));
}

export function PanelInspector(props: PanelInspectorProps) {
  const { panel, sources, sourceMap, screens, screenKey, presets, history, onChange, onRemove, onDuplicate, onMoveToScreen, onApplyToAll } = props;
  const [tab, setTab] = useState<Tab>('readings');
  const confirm = useConfirm();
  // A reading clicked on the screen: its settings opened, in sight.
  const focus = props.focusReading;
  useLayoutEffect(() => {
    if (focus) setTab('readings');
  }, [focus]);
  useLayoutEffect(() => {
    if (props.focusCharacter) setTab('characters');
  }, [props.focusCharacter]);
  const set = (patch: Partial<OverlayPanel>) => onChange({ ...panel, ...patch });
  // A panel made from a ready-made design, changed since: it can be put back as the design makes it (in its place).
  const origin = panel.design ? DESIGNS.find((d) => d.id === panel.design) : undefined;
  const fromDesign = origin ? { ...origin.build(sources), design: origin.id } : null;
  const changed = fromDesign != null && !sameAsDesign(panel, fromDesign);
  const resetToDesign = async () => {
    if (!origin || !fromDesign) return;
    if (await confirm({ title: `Reset this panel to ${origin.name}?`, description: 'Its readings, look and size go back to the ready-made design; it stays where it is on the screen.', action: 'Reset', danger: false }))
      onChange({ ...fromDesign, id: panel.id, x: panel.x, y: panel.y, hidden: panel.hidden, restore: null } as OverlayPanel);
  };
  const setReading = (index: number, patch: Partial<OverlayReading>) => set({ readings: panel.readings.map((r, i) => (i === index ? { ...r, ...patch } : r)) });
  const moveReading = (index: number, by: number) => {
    const next = [...panel.readings];
    const [item] = next.splice(index, 1);
    next.splice(Math.max(0, Math.min(next.length, index + by)), 0, item!);
    set({ readings: next });
  };

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <label className="min-w-0 flex-1">
          <span className="sr-only">Panel title</span>
          <input className={input} value={panel.title ?? ''} maxLength={MAX_TEXT} placeholder="Title (optional)" onChange={(e) => set({ title: e.target.value || null })} />
        </label>
        {changed && (
          <Button size="sm" variant="outline" title={`Back to the ready-made ${origin!.name} design`} onClick={() => void resetToDesign()}>
            <RotateCcw aria-hidden /> Reset to {origin!.name}
          </Button>
        )}
        <Button size="icon" variant="ghost" aria-label={panel.hidden ? 'Show this panel' : 'Hide this panel'} title={panel.hidden ? 'Show this panel' : 'Hide this panel'} onClick={() => set({ hidden: !panel.hidden })}>
          {panel.hidden ? <EyeOff aria-hidden /> : <Eye aria-hidden />}
        </Button>
        <Button size="icon" variant="ghost" aria-label="Duplicate this panel" title="Duplicate" onClick={onDuplicate}>
          <Copy aria-hidden />
        </Button>
        <Button size="icon" variant="ghost" aria-label="Remove this panel" title="Remove" onClick={onRemove}>
          <Trash2 aria-hidden />
        </Button>
      </div>

      <div role="tablist" aria-label="Panel settings" className="flex gap-1 rounded-md bg-surface-muted p-1 text-sm">
        {(
          [
            ['readings', `Readings (${panel.readings.length})`],
            ['layout', 'Layout'],
            ['look', 'Look'],
            ['characters', panel.characters?.length ? `Characters (${panel.characters.length})` : 'Characters'] as const,
          ] as const
        ).map(([id, text]) => (
          <button key={id} type="button" role="tab" aria-selected={tab === id} onClick={() => setTab(id)} className={cn('relative isolate flex-1 rounded px-2 py-1 transition-colors', tab === id ? 'font-medium' : 'text-muted hover:text-foreground')}>
            {tab === id && <TabPill group="panel-tabs" />}
            {text}
          </button>
        ))}
      </div>

      {tab === 'readings' && (
        <Section title="Readings" detail={`${panel.readings.length} of ${MAX_READINGS}`}>
          {panel.readings.length === 0 && <p className="text-xs text-muted">Add the values this panel shows.</p>}
          <ol className="flex flex-col gap-2" aria-label="Readings in this panel">
            {panel.readings.map((reading, index) => (
              <ReadingRow
                // The same row when a percentage switches to its value's source (so its settings stay open).
                key={`${valueSourceOf(reading.source) ?? reading.source}-${index}`}
                reading={reading}
                source={sourceMap.get(reading.source)}
                valueSource={sourceMap.get(valueSourceOf(reading.source) ?? '')}
                iconsOn={panel.style.icons}
                focus={focus?.index === index ? focus.at : undefined}
                panelStyle={panel.style}
                tiles={panel.layout === 'tiles'}
                first={index === 0}
                last={index === panel.readings.length - 1}
                onChange={(patch) => setReading(index, patch)}
                onMove={(by) => moveReading(index, by)}
                onRemove={async () => (await confirm({ title: `Remove ${reading.label || sourceMap.get(reading.source)?.label || 'this reading'}?`, description: 'It is taken off this panel.', action: 'Remove' })) && set({ readings: panel.readings.filter((_, i) => i !== index) })}
              />
            ))}
          </ol>
          {panel.readings.length < MAX_READINGS && <SourcePicker sources={sources} onPick={(source) => set({ readings: [...panel.readings, { source }] })} />}
        </Section>
      )}

      {tab === 'layout' && <LayoutTab panel={panel} sourceMap={sourceMap} screens={screens} screenKey={screenKey} set={set} onMoveToScreen={onMoveToScreen} />}

      {tab === 'look' && <LookTab panel={panel} sourceMap={sourceMap} presets={presets} history={history} onChange={onChange} onApplyToAll={onApplyToAll} />}
      {tab === 'characters' && <CharactersTab panel={panel} focus={props.focusCharacter} sources={sourceMap} onChange={(characters) => set({ characters })} />}
    </div>
  );
}

function LayoutTab({
  panel,
  sourceMap,
  screens,
  screenKey,
  set,
  onMoveToScreen,
}: {
  panel: OverlayPanel;
  sourceMap: ReadonlyMap<string, OverlaySource>;
  screens: OverlayDisplay[];
  screenKey: string;
  set: (patch: Partial<OverlayPanel>) => void;
  onMoveToScreen: (key: string) => void;
}) {
  const fixed = panel.width != null || panel.height != null;
  return (
    <div className="flex flex-col gap-4">
      <Section title="Layout">
        <div role="radiogroup" aria-label="Layout" className="grid grid-cols-3 gap-2">
          {LAYOUTS.map((l) => (
            <button
              key={l.id}
              type="button"
              role="radio"
              aria-checked={panel.layout === l.id}
              onClick={() => set({ layout: l.id })}
              className={cn('flex flex-col items-center gap-1.5 rounded-md border p-2 text-xs', panel.layout === l.id ? 'border-accent bg-accent/10 font-medium' : 'border-border hover:border-accent/60')}
            >
              <LayoutGlyph layout={l.id} />
              {l.name}
            </button>
          ))}
        </div>
        {panel.layout === 'tiles' && (
          <Slider label="Columns" value={panel.columns ?? 2} min={1} max={6} step={1} onChange={(columns) => set({ columns })} format={(v) => String(v)} />
        )}
        {panel.layout === 'rows' && (
          <Slider label="Columns" value={panel.rowColumns ?? 1} min={1} max={4} step={1} onChange={(rowColumns) => set({ rowColumns })} format={(v) => String(v)} />
        )}
      </Section>
      {panel.layout === 'tiles' && panel.readings.length > 0 && <GridDesigner panel={panel} sourceMap={sourceMap} set={set} />}
      <Section title="Size">
        <Segmented
          label="Size"
          value={fixed ? 'fixed' : 'auto'}
          options={[
            ['auto', 'Automatic'],
            ['fixed', 'Fixed'],
          ]}
          onChange={(v) => set(v === 'auto' ? { width: null, height: null, restore: null } : { width: panel.width ?? 320, height: panel.height ?? 180, restore: null })}
        />
        {fixed ? (
          <div className="grid grid-cols-2 gap-2">
            <Field label="Width (px at 100 %)">
              <input className={input} type="number" min={40} max={4000} value={panel.width ?? ''} placeholder="Automatic" onChange={(e) => set({ width: e.target.value === '' ? null : clamp(Number(e.target.value), 40, 4000), restore: null })} />
            </Field>
            <Field label="Height (px at 100 %)">
              <input className={input} type="number" min={20} max={3000} value={panel.height ?? ''} placeholder="Automatic" onChange={(e) => set({ height: e.target.value === '' ? null : clamp(Number(e.target.value), 20, 3000), restore: null })} />
            </Field>
          </div>
        ) : null}
        <p className="text-xs text-muted">
          {fixed
            ? 'The panel keeps this size; readings that do not fit are cut at its edge. Drag the corner of the selected panel on the screen to resize it.'
            : 'As large as its readings need. It grows when a value needs more room and keeps that size while it is shown, so it never jumps.'}
        </p>
      </Section>
      <Section title="Place">
        <div className="flex flex-wrap items-start gap-4">
          <div role="group" aria-label="Snap to" className="grid grid-cols-3 gap-0.5">
            {ANCHORS.map((a) => (
              <button
                key={a.id}
                type="button"
                title={a.label}
                aria-label={`Snap to ${a.label.toLowerCase()}`}
                onClick={() => set({ x: a.x, y: a.y, restore: null })}
                className={cn('size-6 rounded-sm border', panel.x === a.x && panel.y === a.y ? 'border-accent bg-accent' : 'border-border bg-surface-muted hover:border-accent/60')}
              />
            ))}
          </div>
          {screens.length > 1 && (
            <label className="flex flex-col gap-1 text-xs text-muted">
              Screen
              <select className={cn(input, 'w-48 text-foreground')} value={screenKey} onChange={(e) => onMoveToScreen(e.target.value)} aria-label="Move to screen">
                {screens.map((d) => (
                  <option key={d.key} value={d.key}>
                    {d.number}: {d.name}
                  </option>
                ))}
              </select>
            </label>
          )}
        </div>
        <p className="text-xs text-muted">Drag the panel on the screen, or select it and use the arrow keys (Shift for bigger steps).</p>
      </Section>
      <Section title="On the screen">
        <div role="group" aria-label="On the screen itself" className="flex flex-col gap-1.5 text-sm">
          <Check label="Move it with the mouse" checked={panel.movable ?? false} onChange={(movable) => set({ movable })} />
          <Check label="Resize it from its corner" checked={panel.resizable ?? false} onChange={(resizable) => set({ resizable })} />
          <Check label="Close it with ×" checked={panel.closable ?? false} onChange={(closable) => set({ closable })} />
        </div>
        <p className="text-xs text-muted">
          Rest the mouse on the panel and its handles appear; only they take the click, so what is under the panel still works. They never show in a game that holds the
          mouse. A closed panel comes back with the show/hide hotkey or the tray.
        </p>
      </Section>
    </div>
  );
}

/**
 * The tiles grid, to design: drag a tile onto another to put it there, or use the arrows; make tiles wider or taller
 * (a tile spanning more cells shows its value larger, its ring and graph fill it). Placed as the screen places them.
 */
function GridDesigner({ panel, sourceMap, set }: { panel: OverlayPanel; sourceMap: ReadonlyMap<string, OverlaySource>; set: (patch: Partial<OverlayPanel>) => void }) {
  const columns = Math.max(1, Math.min(6, panel.columns ?? 2));
  const places = pack(panel.readings, columns);
  const rows = Math.max(1, ...places.map((p) => p.row + p.rows));
  const [dragged, setDragged] = useState<number | null>(null);
  // The tile whose size and order the toolbar under the grid changes.
  const [chosen, setChosen] = useState<number | null>(null);
  const update = (index: number, patch: Partial<OverlayReading>) => set({ readings: panel.readings.map((r, i) => (i === index ? { ...r, ...patch } : r)) });
  const move = (from: number, to: number) => {
    if (to < 0 || to >= panel.readings.length || from === to) return;
    const next = [...panel.readings];
    const [item] = next.splice(from, 1);
    next.splice(to, 0, item!);
    set({ readings: next });
    if (chosen === from) setChosen(to);
  };
  const nameOf = (reading: OverlayReading) => reading.label || sourceMap.get(reading.source)?.label || reading.source;
  const current = chosen != null && chosen < panel.readings.length ? chosen : null;
  return (
    <Section title="Grid" detail={`${columns} × ${rows}`}>
      <div
        role="list"
        aria-label="Tiles in the grid"
        className="grid gap-1.5 rounded-md border border-dashed border-border p-1.5"
        style={{ gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`, gridAutoRows: '3.75rem' }}
      >
        {panel.readings.map((reading, index) => {
          const place = places[index]!;
          const source = sourceMap.get(reading.source);
          const name = nameOf(reading);
          const value = shown(source, reading);
          const span = { cols: reading.colSpan ?? 1, rows: reading.rowSpan ?? 1 };
          return (
            <div
              key={`${reading.source}-${index}`}
              role="listitem"
              aria-label={`${name}: ${span.cols} × ${span.rows}`}
              aria-current={current === index ? 'true' : undefined}
              tabIndex={0}
              onClick={() => setChosen(index)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  setChosen(index);
                }
              }}
              draggable
              onDragStart={(e) => {
                setDragged(index);
                e.dataTransfer.effectAllowed = 'move';
              }}
              onDragOver={(e) => {
                if (dragged !== null) e.preventDefault();
              }}
              onDrop={(e) => {
                e.preventDefault();
                if (dragged !== null) move(dragged, index);
                setDragged(null);
              }}
              onDragEnd={() => setDragged(null)}
              className={cn(
                'relative flex min-w-0 cursor-pointer flex-col justify-between overflow-hidden rounded-md border bg-surface-muted p-1.5 text-xs transition-colors',
                dragged === index ? 'border-accent opacity-60' : current === index ? 'border-accent ring-1 ring-accent/50' : 'border-border hover:border-accent/60',
              )}
              style={{ gridColumn: `${place.column + 1} / span ${place.columns}`, gridRow: `${place.row + 1} / span ${place.rows}` }}
            >
              <span className="flex min-w-0 items-center gap-1">
                <GripVertical className="size-3 shrink-0 cursor-grab text-muted" aria-hidden />
                <span className="truncate font-medium">{name}</span>
              </span>
              <span className="flex min-w-0 items-baseline justify-between gap-1 text-muted">
                <span className="truncate tabular-nums">
                  {value.value} {value.unit}
                </span>
                {(span.cols > 1 || span.rows > 1) && (
                  <span className="shrink-0 tabular-nums text-[10px]">
                    {span.cols}×{span.rows}
                  </span>
                )}
              </span>
            </div>
          );
        })}
      </div>
      {current != null ? (
        (() => {
          const reading = panel.readings[current]!;
          const name = nameOf(reading);
          const span = { cols: reading.colSpan ?? 1, rows: reading.rowSpan ?? 1 };
          return (
            <div role="toolbar" aria-label={`Arrange ${name}`} className="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-md border border-border bg-surface-muted px-2.5 py-2 text-xs">
              <span className="min-w-0 max-w-[10rem] truncate font-medium">{name}</span>
              <ToolGroup label="Order">
                <TinyButton label={`Move ${name} earlier`} disabled={current === 0} onClick={() => move(current, current - 1)}>
                  <ArrowLeft />
                </TinyButton>
                <TinyButton label={`Move ${name} later`} disabled={current === panel.readings.length - 1} onClick={() => move(current, current + 1)}>
                  <ArrowRight />
                </TinyButton>
              </ToolGroup>
              <ToolGroup label="Width" value={`${span.cols} of ${columns}`}>
                <TinyButton label={`${name} narrower`} disabled={span.cols <= 1} onClick={() => update(current, { colSpan: span.cols - 1 })}>
                  <Minus />
                </TinyButton>
                <TinyButton label={`${name} wider`} disabled={span.cols >= columns} onClick={() => update(current, { colSpan: span.cols + 1 })}>
                  <Plus />
                </TinyButton>
              </ToolGroup>
              <ToolGroup label="Height" value={`${span.rows}`}>
                <TinyButton label={`${name} shorter`} disabled={span.rows <= 1} onClick={() => update(current, { rowSpan: span.rows - 1 })}>
                  <Minus />
                </TinyButton>
                <TinyButton label={`${name} taller`} disabled={span.rows >= 4} onClick={() => update(current, { rowSpan: span.rows + 1 })}>
                  <Plus />
                </TinyButton>
              </ToolGroup>
            </div>
          );
        })()
      ) : (
        <p className="text-xs text-muted">Choose a tile to change its order and size, or drag a tile onto another to move it there. A larger tile shows its value larger.</p>
      )}
    </Section>
  );
}

/** A labelled group of the grid toolbar's buttons, its current value between them. */
function ToolGroup({ label, value, children }: { label: string; value?: string; children: ReactNode }) {
  const [first, ...rest] = Array.isArray(children) ? children : [children];
  return (
    <span role="group" aria-label={label} className="flex items-center gap-1">
      <span className="text-muted">{label}</span>
      <span className="flex items-center gap-0.5 rounded bg-surface px-0.5 py-0.5 shadow-sm">
        {first}
        {value && <span className="min-w-[2.75rem] text-center tabular-nums">{value}</span>}
        {rest}
      </span>
    </span>
  );
}

function TinyButton({ label, disabled, onClick, children }: { label: string; disabled?: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button type="button" aria-label={label} title={label} disabled={disabled} onClick={onClick} className="flex size-5 items-center justify-center rounded text-muted hover:bg-surface hover:text-foreground disabled:opacity-30 [&>svg]:size-3">
      {children}
    </button>
  );
}

/** A small drawing of a layout. */
function LayoutGlyph({ layout }: { layout: OverlayPanel['layout'] }) {
  const bar = 'rounded-sm bg-current opacity-60';
  return (
    <span aria-hidden className="flex h-9 w-14 items-center justify-center rounded border border-border bg-surface-muted p-1 text-accent">
      {layout === 'rows' && (
        <span className="flex w-full flex-col gap-1">
          {[0, 1, 2].map((i) => (
            <span key={i} className="flex items-center justify-between gap-1">
              <span className={cn(bar, 'h-1 w-4')} />
              <span className={cn(bar, 'h-1 w-2 opacity-100')} />
            </span>
          ))}
        </span>
      )}
      {layout === 'tiles' && (
        <span className="grid w-full grid-cols-3 gap-0.5">
          {[0, 1, 2, 3, 4, 5].map((i) => (
            <span key={i} className="h-3 rounded-sm bg-current opacity-40" />
          ))}
        </span>
      )}
      {layout === 'line' && (
        <span className="flex w-full items-center gap-0.5">
          {[0, 1, 2, 3].map((i) => (
            <span key={i} className={cn(bar, 'h-1 flex-1')} />
          ))}
        </span>
      )}
    </span>
  );
}

function LookTab({ panel, sourceMap, presets, history, onChange, onApplyToAll }: Pick<PanelInspectorProps, 'panel' | 'sourceMap' | 'presets' | 'history' | 'onChange' | 'onApplyToAll'>) {
  const style = panel.style;
  const setStyle = (patch: Partial<OverlayStyle>) => onChange({ ...panel, style: { ...style, ...patch } });
  const current = presetOf(style, presets);
  // The gallery shows this panel itself in each look (a few readings, so the cards stay small).
  const sample: OverlayPanel = { ...panel, readings: panel.readings.slice(0, panel.layout === 'tiles' ? 2 : 3), columns: Math.min(2, panel.columns ?? 2), x: 0, y: 0, width: null, height: null };

  return (
    <div className="flex flex-col gap-4">
      <Section title="Themes" detail={current ? `${presets.find((p) => p.id === current)?.name} · unchanged` : 'Your own look'}>
        <div role="group" aria-label="Ready-made looks" className="grid grid-cols-3 gap-1.5 sm:grid-cols-4">
          {presets.map((preset) => (
            <button
              key={preset.id}
              type="button"
              aria-pressed={current === preset.id}
              aria-label={preset.name}
              title={preset.description}
              onClick={() => onChange({ ...panel, style: { ...preset.style } })}
              className={cn('motion-hover group flex flex-col overflow-hidden rounded-md border text-left transition-colors', current === preset.id ? 'border-accent ring-2 ring-accent/30' : 'border-border hover:border-accent/60')}
            >
              <span className="relative flex h-14 items-center justify-center overflow-hidden" style={{ background: 'radial-gradient(circle at 20% 30%, #7c3aed55, transparent 45%), radial-gradient(circle at 80% 70%, #0ea5e955, transparent 45%), linear-gradient(135deg, #0b1220, #334155)' }}>
                <ThemeSample panel={{ ...sample, style: { ...preset.style } }} sources={sourceMap} history={history} />
                {current === preset.id && (
                  <span className="absolute right-1 top-1 rounded-full bg-accent p-0.5 text-accent-foreground">
                    <CheckIcon className="size-3" aria-hidden />
                  </span>
                )}
              </span>
              <span className="truncate px-1.5 py-0.5 text-[11px] font-medium">{preset.name}</span>
            </button>
          ))}
        </div>
        <Button size="sm" variant="outline" className="self-start" onClick={() => onApplyToAll(style)}>
          <Wand2 aria-hidden /> Use this look for every panel
        </Button>
      </Section>

      <Group title="Text" icon={<Palette className="size-3.5" aria-hidden />} open>
        <div className="grid grid-cols-2 gap-x-3 gap-y-2">
          <Field label="Font">
            <select className={input} value={style.font} onChange={(e) => setStyle({ font: e.target.value })}>
              {FONTS.map((f) => (
                <option key={f} value={f}>
                  {f}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Text size">
            <input className={input} type="number" min={6} max={72} value={style.size} onChange={(e) => setStyle({ size: clamp(Number(e.target.value), 6, 72) })} />
          </Field>
        </div>
        <Slider label="Values' size" value={style.valueScale} min={80} max={300} step={5} unit="%" onChange={(valueScale) => setStyle({ valueScale })} />
        <Field label="Values sit" group>
          <Segmented label="Values sit" value={style.valueAlign ?? 'center'} options={VALUE_ALIGNS.map((a) => [a.id, a.name] as const)} onChange={(valueAlign) => setStyle({ valueAlign })} />
        </Field>
        {panel.layout === 'tiles' && (
          <Field label="Up and down in tiles" group>
            <Segmented
              label="Up and down in tiles"
              value={style.valueVertical ?? 'auto'}
              options={[['auto', 'Automatic'] as const, ...VALUE_VERTICALS.map((a) => [a.id, a.name] as const)]}
              onChange={(v) => setStyle({ valueVertical: v === 'auto' ? null : v })}
            />
          </Field>
        )}
        <p className="text-xs text-muted">Each value keeps a fixed space, as wide as the widest it can read, so values changing never move the others.</p>
        <Slider label="Spacing" value={style.spacing} min={50} max={250} step={5} unit="%" onChange={(spacing) => setStyle({ spacing })} />
        <div className="flex flex-wrap gap-x-4 gap-y-1.5 text-sm">
          <Check label="Bold values" checked={style.bold} onChange={(bold) => setStyle({ bold })} />
          <Check label="Spaced capitals" checked={style.caps} onChange={(caps) => setStyle({ caps })} />
          <Check label="Labels" checked={style.showLabels} onChange={(showLabels) => setStyle({ showLabels })} />
          <Check label="Units" checked={style.showUnits} onChange={(showUnits) => setStyle({ showUnits })} />
          <Check label="Icons" checked={style.icons} onChange={(icons) => setStyle({ icons })} />
        </div>
      </Group>

      <Group title="Colors">
        <div className="grid grid-cols-2 gap-x-3 gap-y-2 sm:grid-cols-3">
          <Color label="Accent" value={style.accent} onChange={(accent) => setStyle({ accent })} />
          <Color label="Values" value={style.valueColor} onChange={(valueColor) => setStyle({ valueColor })} />
          <Color label="Labels" value={style.labelColor} onChange={(labelColor) => setStyle({ labelColor })} />
          <Color label="Units" value={style.unitColor} onChange={(unitColor) => setStyle({ unitColor })} />
          <Color label="Title" value={style.titleColor} onChange={(titleColor) => setStyle({ titleColor })} />
          <OptionalColor label="Icons" value={style.iconColor} fallback={style.accent} empty="Accent" onChange={(iconColor) => setStyle({ iconColor })} />
          <Color label="OK" value={style.okColor} onChange={(okColor) => setStyle({ okColor })} />
          <Color label="Warning" value={style.warningColor} onChange={(warningColor) => setStyle({ warningColor })} />
          <Color label="Critical" value={style.criticalColor} onChange={(criticalColor) => setStyle({ criticalColor })} />
        </div>
      </Group>

      <Group title="Background">
        <div role="group" aria-label="Quick opacity" className="flex flex-wrap gap-1.5">
          {(
            [
              ['Text only', { backgroundOpacity: 0, tileOpacity: 0, outline: true }],
              ['See-through', { backgroundOpacity: 35, tileOpacity: 6 }],
              ['Glass', { backgroundOpacity: 60, tileOpacity: 8 }],
              ['Solid', { backgroundOpacity: 95, tileOpacity: 10 }],
            ] as const
          ).map(([name, patch]) => (
            <button key={name} type="button" onClick={() => setStyle(patch)} className="rounded-full border border-border px-2.5 py-0.5 text-xs hover:border-accent/60">
              {name}
            </button>
          ))}
        </div>
        <div className="grid grid-cols-2 gap-x-3 gap-y-2">
          <Color label="Color" value={style.background} onChange={(background) => setStyle({ background })} />
          <OptionalColor label="Gradient to" value={style.background2} fallback="#0E7490" empty="Solid" onChange={(background2) => setStyle({ background2 })} />
        </div>
        {style.background2 && <Slider label="Gradient direction" value={style.gradientAngle} min={0} max={360} step={5} unit="°" onChange={(gradientAngle) => setStyle({ gradientAngle })} />}
        <Slider label="Opacity" value={style.backgroundOpacity} min={0} max={100} step={5} unit="%" onChange={(backgroundOpacity) => setStyle({ backgroundOpacity })} />
        <Check label="Glass sheen" checked={style.sheen} onChange={(sheen) => setStyle({ sheen })} />

        <Sub title="Picture" />
        <BackgroundField panel={panel} sourceMap={sourceMap} history={history} onChange={setStyle} />
        {style.image && !style.art && (
          <>
            <Field label="The picture" group>
              <Segmented label="Picture fit" value={style.imageFit ?? 'cover'} options={IMAGE_FITS.map((f) => [f.id, f.name] as const)} onChange={(imageFit) => setStyle({ imageFit })} />
            </Field>
            {(style.imageFit ?? 'cover') !== 'stretch' && (style.imageFit ?? 'cover') !== 'tile' && (
              <Field label="Position">
                <select className={input} value={style.imagePosition ?? 'center'} onChange={(e) => setStyle({ imagePosition: e.target.value as OverlayStyle['imagePosition'] })} aria-label="Picture position">
                  {(['center', 'top', 'bottom', 'left', 'right'] as const).map((p) => (
                    <option key={p} value={p}>
                      {p === 'center' ? 'Centre' : p[0]!.toUpperCase() + p.slice(1)}
                    </option>
                  ))}
                </select>
              </Field>
            )}
            <Slider label="Picture" value={style.imageOpacity ?? 100} min={0} max={100} step={5} unit="%" onChange={(imageOpacity) => setStyle({ imageOpacity })} />
            <Slider label="Blur" value={style.imageBlur ?? 0} min={0} max={30} step={1} unit=" px" onChange={(imageBlur) => setStyle({ imageBlur })} />
            <div className="grid grid-cols-2 gap-x-3 gap-y-2">
              <OptionalColor label="Tint" value={style.imageTint ?? null} fallback="#000000" empty="None" onChange={(imageTint) => setStyle({ imageTint })} />
              <Field label="Colors" group>
                <Check label="Grey" checked={style.imageGray ?? false} onChange={(imageGray) => setStyle({ imageGray })} />
              </Field>
            </div>
            {style.imageTint && <Slider label="Tint strength" value={style.imageTintOpacity ?? 35} min={0} max={100} step={5} unit="%" onChange={(imageTintOpacity) => setStyle({ imageTintOpacity })} />}
          </>
        )}

        <Sub title="Pattern" />
        <Field label="Pattern" group>
          <PatternPicker value={style.pattern ?? null} onChange={(pattern) => setStyle({ pattern })} />
        </Field>
        {style.pattern && (
          <>
            <OptionalColor label="Pattern color" value={style.patternColor ?? null} fallback="#FFFFFF" empty="Automatic" onChange={(patternColor) => setStyle({ patternColor })} />
            <Slider label="Pattern" value={style.patternOpacity ?? 14} min={0} max={100} step={1} unit="%" onChange={(patternOpacity) => setStyle({ patternOpacity })} />
            <Slider label="Pattern size" value={style.patternScale ?? 100} min={50} max={400} step={10} unit="%" onChange={(patternScale) => setStyle({ patternScale })} />
          </>
        )}

        <Sub title="Motion" />
        <div role="radiogroup" aria-label="Moving background" className="flex flex-wrap gap-1.5">
          {MOTIONS.map((m) => (
            <button
              key={m.id}
              type="button"
              role="radio"
              aria-checked={(style.motion ?? 'none') === m.id}
              title={m.description}
              onClick={() => setStyle({ motion: m.id })}
              className={cn('rounded-full border px-2.5 py-0.5 text-xs transition-colors', (style.motion ?? 'none') === m.id ? 'border-accent bg-accent/15' : 'border-border text-muted hover:text-foreground')}
            >
              {m.name}
            </button>
          ))}
        </div>
        {(style.motion ?? 'none') !== 'none' && <Slider label="Speed" value={style.motionSpeed ?? 100} min={25} max={300} step={5} unit="%" onChange={(motionSpeed) => setStyle({ motionSpeed })} />}
      </Group>

      <Group title="Frame and effects">
        <div className="grid grid-cols-2 gap-x-3 gap-y-2">
          <Field label="Corners">
            <input className={input} type="number" min={0} max={32} value={style.cornerRadius} onChange={(e) => setStyle({ cornerRadius: clamp(Number(e.target.value), 0, 32) })} />
          </Field>
          <OptionalColor label="Border" value={style.border} fallback={style.accent} empty="None" onChange={(border) => setStyle({ border })} />
          {style.border && (
            <Field label="Border width">
              <input className={input} type="number" min={0.5} max={6} step={0.5} value={style.borderWidth} onChange={(e) => setStyle({ borderWidth: clamp(Number(e.target.value), 0.5, 6) })} />
            </Field>
          )}
        </div>
        <Slider label="Padding" value={style.padding} min={0} max={40} step={1} unit="px" onChange={(padding) => setStyle({ padding })} />
        <Slider label="Shadow" value={style.shadow} min={0} max={100} step={5} unit="%" onChange={(shadow) => setStyle({ shadow })} />
        <Slider label="Glow" value={style.glow} min={0} max={100} step={5} unit="%" onChange={(glow) => setStyle({ glow })} />
        <Slider label="Whole panel" value={style.opacity} min={10} max={100} step={5} unit="%" onChange={(opacity) => setStyle({ opacity })} />
        <Field label="Accent edge" group>
          <Segmented
            label="Accent edge"
            value={style.accentEdge}
            options={[
              ['none', 'None'],
              ['left', 'Left'],
              ['top', 'Top'],
              ['bottom', 'Bottom'],
            ]}
            onChange={(accentEdge) => setStyle({ accentEdge })}
          />
        </Field>
        <div className="flex flex-col gap-1.5 text-sm">
          <Check label="Title on an accent band" checked={style.titleBar} onChange={(titleBar) => setStyle({ titleBar })} />
          <Check label="Dark outline around text (readable over games)" checked={style.outline} onChange={(outline) => setStyle({ outline })} />
        </div>
      </Group>

      {panel.layout === 'tiles' && (
        <Group title="Tiles">
          <Slider label="Tiles" value={style.tileOpacity} min={0} max={100} step={1} unit="%" onChange={(tileOpacity) => setStyle({ tileOpacity })} />
          <OptionalColor label="Tiles' color" value={style.tileColor} fallback="#FFFFFF" empty="Automatic" onChange={(tileColor) => setStyle({ tileColor })} />
          <Field label="Behind each tile" group>
            <div role="radiogroup" aria-label="Behind each tile" className="flex flex-wrap gap-1.5">
              {BACKDROPS.map((b) => (
                <button
                  key={b.id}
                  type="button"
                  role="radio"
                  aria-checked={(style.backdrop ?? 'none') === b.id}
                  title={b.description}
                  onClick={() => setStyle({ backdrop: b.id })}
                  className={cn('rounded-full border px-2.5 py-0.5 text-xs transition-colors', (style.backdrop ?? 'none') === b.id ? 'border-accent bg-accent/15' : 'border-border text-muted hover:text-foreground')}
                >
                  {b.name}
                </button>
              ))}
            </div>
          </Field>
        </Group>
      )}

      <Group title="Bars, rings and graphs">
        <Field label="Bars" group>
          <Segmented
            label="Bar style"
            value={style.barStyle}
            options={[
              ['rounded', 'Rounded'],
              ['solid', 'Square'],
              ['segments', 'Segments'],
            ]}
            onChange={(barStyle) => setStyle({ barStyle })}
          />
        </Field>
        <Slider label="Thickness" value={style.barHeight} min={50} max={400} step={10} unit="%" onChange={(barHeight) => setStyle({ barHeight })} />
        <Slider label="Graphs show the last" value={style.graphSeconds} min={15} max={300} step={15} unit=" s" onChange={(graphSeconds) => setStyle({ graphSeconds })} />
        <p className="text-xs text-muted">Choose a bar, ring or graph for each reading on the Readings tab.</p>
      </Group>
    </div>
  );
}

/** The panel in one theme, fitted into its gallery card (whatever the card's width). */
function ThemeSample({ panel, sources, history }: { panel: OverlayPanel; sources: ReadonlyMap<string, OverlaySource>; history?: History }) {
  const box = useRef<HTMLSpanElement>(null);
  const [width, setWidth] = useState(120);
  useLayoutEffect(() => {
    const element = box.current?.parentElement;
    if (!element) return undefined;
    const measure = () => setWidth(Math.max(48, (element.clientWidth || 96) - 8));
    measure();
    if (typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  return (
    <span ref={box} className="contents">
      <ShrunkPreview panel={panel} sources={sources} scale={1} history={history} fit={{ width, height: 48 }} className="pointer-events-none" />
    </span>
  );
}

const clamp = (v: number, min: number, max: number) => (Number.isFinite(v) ? Math.min(max, Math.max(min, v)) : min);

export function Section({ title, detail, children }: { title: string; detail?: string; children: ReactNode }) {
  return (
    <section aria-label={title} className="flex flex-col gap-2">
      <h3 className="flex items-baseline justify-between text-sm font-medium">
        {title}
        {detail && <span className="text-xs font-normal text-muted">{detail}</span>}
      </h3>
      {children}
    </section>
  );
}

/** A heading within a group (the picture and the pattern within Background). */
function Sub({ title }: { title: string }) {
  return <h4 className="mt-1 border-t border-border pt-2.5 text-xs font-medium uppercase tracking-wide text-muted">{title}</h4>;
}

function Group({ title, icon, open, children }: { title: string; icon?: ReactNode; open?: boolean; children: ReactNode }) {
  return (
    <details open={open} className="group rounded-md border border-border">
      <summary className="flex cursor-pointer list-none items-center justify-between gap-2 px-3 py-2 text-sm font-medium">
        <span className="flex items-center gap-1.5">
          {icon}
          {title}
        </span>
        <ChevronDown className="size-4 text-muted transition-transform group-open:rotate-180" aria-hidden />
      </summary>
      <div className="flex flex-col gap-2.5 border-t border-border px-3 py-3">{children}</div>
    </details>
  );
}

/**
 * A setting with its caption: a label for one input; a captioned group for buttons or checkboxes (a label would name
 * its first button after the caption, and cannot hold another label).
 */
function Field({ label, group = false, children }: { label: string; group?: boolean; children: ReactNode }) {
  if (group) {
    return (
      <div role="group" aria-label={label} className="flex flex-col gap-1 text-xs text-muted">
        <span>{label}</span>
        <span className="text-foreground">{children}</span>
      </div>
    );
  }
  return (
    <label className="flex flex-col gap-1 text-xs text-muted">
      {label}
      <span className="text-foreground">{children}</span>
    </label>
  );
}

export function Slider({ label, value, min, max, step, unit = '', format, onChange }: { label: string; value: number; min: number; max: number; step: number; unit?: string; format?: (v: number) => string; onChange: (v: number) => void }) {
  return (
    <label className="flex items-center gap-3 text-xs text-muted">
      <span className="w-32 shrink-0">{label}</span>
      <input type="range" min={min} max={max} step={step} value={value} onChange={(e) => onChange(clamp(Number(e.target.value), min, max))} className="h-1.5 flex-1 accent-[var(--accent)]" aria-label={label} />
      <span className="w-12 shrink-0 text-right tabular-nums text-foreground">{format ? format(value) : `${value}${unit}`}</span>
    </label>
  );
}

/** One of a few choices, side by side; wrapping onto more rows when they do not fit (each choice on one line). */
export function Segmented<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: readonly (readonly [T, string])[]; onChange: (v: T) => void }) {
  return (
    <span role="radiogroup" aria-label={label} className="flex flex-wrap gap-0.5 rounded-md bg-surface-muted p-0.5 text-xs">
      {options.map(([id, text]) => (
        <button key={id} type="button" role="radio" aria-checked={value === id} onClick={() => onChange(id)} className={cn('flex-1 whitespace-nowrap rounded px-2 py-1', value === id ? 'bg-surface font-medium text-foreground shadow-sm' : 'text-muted')}>
          {text}
        </button>
      ))}
    </span>
  );
}

/** The visuals as the dashboard's widget designs list shows its designs: a drawn sample and a name, grouped. */
function VisualPicker({ value, onChange }: { value: OverlayVisual; onChange: (v: OverlayVisual) => void }) {
  const groups = [...new Set(VISUALS.map((v) => v.group))];
  const chosen = VISUALS.find((v) => v.id === value);
  return (
    <span className="flex flex-col gap-1.5">
      <span role="radiogroup" aria-label="Shown with" className="flex flex-col gap-1.5">
        {groups.map((group) => (
          <span key={group} className="flex items-start gap-2">
            <span className="w-12 shrink-0 pt-2 text-[10px] uppercase tracking-wide text-muted">{group === 'History' ? 'Graphs' : group === 'Value' ? 'Plain' : `${group}s`}</span>
            <span className="grid flex-1 grid-cols-4 gap-1">
              {VISUALS.filter((v) => v.group === group).map((v) => (
                <button
                  key={v.id}
                  type="button"
                  role="radio"
                  aria-checked={value === v.id}
                  aria-label={v.name}
                  title={v.description}
                  onClick={() => onChange(v.id)}
                  className={cn(
                    'flex flex-col items-center gap-0.5 rounded-md border px-1 py-1.5 text-[11px] leading-tight transition-colors',
                    value === v.id ? 'border-accent bg-accent/10 text-foreground' : 'border-border text-muted hover:border-accent/50 hover:text-foreground',
                  )}
                >
                  <VisualSample visual={v.id} />
                  <span className="text-center">{v.name}</span>
                </button>
              ))}
            </span>
          </span>
        ))}
      </span>
      {chosen && <span className="text-xs text-muted">{chosen.description}</span>}
    </span>
  );
}

/** A small drawing of a visual, for the picker. */
function VisualSample({ visual }: { visual: OverlayVisual }) {
  const line = 'M2 15 L7 11 L11 12 L16 6 L21 8 L26 3';
  const accent = 'var(--accent)';
  const track = 'currentColor';
  const shape = (() => {
    switch (visual) {
      case 'none':
        return (
          <text x="14" y="14" textAnchor="middle" fontSize="11" fontWeight="700" fill="currentColor">
            42
            <tspan fontSize="7" fontWeight="400">%</tspan>
          </text>
        );
      case 'ring':
        return (
          <>
            <circle cx="14" cy="9" r="6.5" fill="none" stroke={track} strokeOpacity={0.25} strokeWidth="2.4" />
            <circle cx="14" cy="9" r="6.5" fill="none" stroke={accent} strokeWidth="2.4" strokeDasharray="28 41" strokeLinecap="round" transform="rotate(-90 14 9)" />
          </>
        );
      case 'arc':
        return (
          <>
            <path d="M8.5 14.5 A7 7 0 1 1 19.5 14.5" fill="none" stroke={track} strokeOpacity={0.25} strokeWidth="2.4" strokeLinecap="round" />
            <path d="M8.5 14.5 A7 7 0 0 1 17 3" fill="none" stroke={accent} strokeWidth="2.4" strokeLinecap="round" />
          </>
        );
      case 'speed':
        return (
          <>
            <path d="M6.5 13 A8 8 0 1 1 21.5 13" fill="none" stroke={track} strokeOpacity={0.3} strokeWidth="1.6" />
            <line x1="14" y1="11" x2="19" y2="5.5" stroke={accent} strokeWidth="1.8" strokeLinecap="round" />
            <circle cx="14" cy="11" r="1.8" fill={accent} />
          </>
        );
      case 'status':
        return (
          <>
            <circle cx="14" cy="9" r="6" fill="#34D399" fillOpacity={0.25} />
            <circle cx="14" cy="9" r="3.5" fill="#34D399" />
          </>
        );
      case 'graph':
        return (
          <>
            <path d={`${line} L26 17 L2 17 Z`} fill={accent} fillOpacity={0.3} />
            <path d={line} fill="none" stroke={accent} strokeWidth="1.6" strokeLinejoin="round" />
          </>
        );
      case 'line':
        return <path d={line} fill="none" stroke={accent} strokeWidth="1.6" strokeLinejoin="round" />;
      case 'columns':
        return (
          <>
            {[9, 6, 11, 4, 13, 8].map((h, i) => (
              <rect key={i} x={2 + i * 4.2} y={17 - h} width="3" height={h} fill={accent} />
            ))}
          </>
        );
      case 'bar':
        return (
          <>
            <rect x="2" y="7.5" width="24" height="3.5" rx="1.75" fill={track} fillOpacity={0.25} />
            <rect x="2" y="7.5" width="15" height="3.5" rx="1.75" fill={accent} />
          </>
        );
      case 'thermo':
        return (
          <>
            <rect x="6" y="7.5" width="20" height="3.5" rx="1.75" fill={track} fillOpacity={0.25} />
            <rect x="6" y="7.5" width="11" height="3.5" rx="1.75" fill={accent} />
            <circle cx="5" cy="9.25" r="3.4" fill={accent} />
          </>
        );
      case 'stats':
        return (
          <text x="14" y="12.5" textAnchor="middle" fontSize="6.5" fill="currentColor">
            ↓ avg ↑
          </text>
        );
      case 'trend':
        return (
          <>
            <text x="9" y="13.5" textAnchor="middle" fontSize="10" fontWeight="700" fill="currentColor">
              42
              <tspan fontSize="6" fontWeight="400">%</tspan>
            </text>
            <path d="M19 13 L22.5 6.5 L26 13 Z" fill="#34D399" />
          </>
        );
    }
  })();
  return (
    <svg width="28" height="18" viewBox="0 0 28 18" aria-hidden className="shrink-0">
      {shape}
    </svg>
  );
}

/**
 * The panel's picture: what it is now (a moving scene, glass over a picture, art drawn for its readings, a picture or
 * an animation), the chooser (scenes and the library, with uploads), and removing it.
 */
function BackgroundField({ panel, sourceMap, history, onChange }: { panel: OverlayPanel; sourceMap: ReadonlyMap<string, OverlaySource>; history?: History; onChange: (patch: Partial<OverlayStyle>) => void }) {
  const [open, setOpen] = useState(false);
  const s = panel.style;
  const picture = s.art === 'glass' ? (s.artSource ?? null) : s.art ? null : (s.image ?? null);
  const what = isGenerative(s.art)
    ? { name: GENERATIVE[s.art].name, detail: 'Moving scene, glass cards over the readings' }
    : s.art === 'glass'
      ? { name: 'Frosted glass', detail: 'Over a picture of the library' }
      : isDrawnArt(s.art)
        ? { name: ART_THEMES[s.art].name, detail: 'Drawn around its readings' }
        : s.image
          ? { name: isAnimationId(s.image) ? 'Animation' : 'Picture', detail: 'From the library' }
          : null;
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap items-center gap-2">
        {isGenerative(s.art) ? (
          <span aria-hidden className="h-10 w-16 rounded border border-border" style={{ background: `linear-gradient(135deg, ${GENERATIVE[s.art].palette.slice(1).join(', ')})` }} />
        ) : picture && !isAnimationId(picture) ? (
          <img src={imageUrl(picture)} alt="The panel's background picture" className="h-10 w-16 rounded border border-border object-cover" />
        ) : picture ? (
          <span className="flex h-10 w-16 items-center justify-center rounded border border-border bg-black text-[10px] text-white/80">Animation</span>
        ) : (
          <span className="flex h-10 w-16 items-center justify-center rounded border border-dashed border-border text-[10px] text-muted">{s.art ? 'Art' : 'No picture'}</span>
        )}
        {what && (
          <span className="min-w-0 text-xs">
            <span className="block font-medium">{what.name}</span>
            <span className="block text-muted">{what.detail}</span>
          </span>
        )}
        <Button size="sm" variant="outline" onClick={() => setOpen(true)}>
          {what ? 'Change…' : 'Choose…'}
        </Button>
        {what && (
          <Button size="sm" variant="ghost" onClick={() => onChange({ art: null, artSource: null, image: null })}>
            Remove
          </Button>
        )}
      </div>
      {!what && <p className="text-xs text-muted">A moving scene generated for this panel, or a photo, animation or short video of your library behind the readings.</p>}
      {open && <BackgroundChooser open panel={panel} sourceMap={sourceMap} history={history} onClose={() => setOpen(false)} onChange={onChange} />}
    </div>
  );
}

/** The background patterns, each drawn as it looks. */
function PatternPicker({ value, onChange }: { value: OverlayPattern | null; onChange: (pattern: OverlayPattern | null) => void }) {
  const options: { id: OverlayPattern | null; name: string }[] = [{ id: null, name: 'None' }, ...PATTERNS];
  return (
    <span role="radiogroup" aria-label="Pattern" className="grid grid-cols-4 gap-1 sm:grid-cols-7">
      {options.map((p) => (
        <button
          key={p.id ?? 'none'}
          type="button"
          role="radio"
          aria-checked={value === p.id}
          aria-label={p.name}
          title={p.name}
          onClick={() => onChange(p.id)}
          className={cn('flex flex-col items-center gap-0.5 rounded-md border px-1 py-1 text-[10px] transition-colors', value === p.id ? 'border-accent bg-accent/10 text-foreground' : 'border-border text-muted hover:border-accent/50 hover:text-foreground')}
        >
          <span
            aria-hidden
            className="block h-5 w-full rounded-sm bg-[#0f172a]"
            style={
              p.id
                ? (() => {
                    const t = patternTile(p.id, 12);
                    return { backgroundImage: patternSvg(p.id, 12, '#CBD5E1', 0.85), backgroundSize: `${t.width}px ${t.height}px` };
                  })()
                : undefined
            }
          />
          {p.name}
        </button>
      ))}
    </span>
  );
}

function Color({ label, value, onChange }: { label: string; value: string; onChange: (value: string) => void }) {
  return (
    <Field label={label}>
      <span className="flex items-center gap-1.5">
        <input type="color" aria-label={`${label} color`} value={value} onChange={(e) => onChange(e.target.value.toUpperCase())} className="h-8 w-10 shrink-0 rounded border border-border bg-background" />
        <span className="font-mono text-xs text-muted">{value}</span>
      </span>
    </Field>
  );
}

/** A color that can also be "none" (or follow another color). */
function OptionalColor({ label, value, fallback, empty, onChange }: { label: string; value: string | null; fallback: string; empty: string; onChange: (value: string | null) => void }) {
  return (
    <Field label={label}>
      <span className="flex items-center gap-1.5">
        <input type="checkbox" aria-label={`Own ${label.toLowerCase()} color`} checked={value != null} onChange={(e) => onChange(e.target.checked ? fallback : null)} className="size-4 accent-[var(--accent)]" />
        {value != null ? (
          <input type="color" aria-label={`${label} color`} value={value} onChange={(e) => onChange(e.target.value.toUpperCase())} className="h-8 w-10 rounded border border-border bg-background" />
        ) : (
          <span className="text-xs text-muted">{empty}</span>
        )}
      </span>
    </Field>
  );
}

export function Check({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <label className="flex items-center gap-1.5">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="size-4 accent-[var(--accent)]" />
      {label}
    </label>
  );
}

const num = (text: string): number | null => (text.trim() === '' || !Number.isFinite(Number(text)) ? null : Number(text));

function ReadingRow({
  reading,
  source,
  valueSource,
  iconsOn,
  focus,
  panelStyle,
  tiles,
  first,
  last,
  onChange,
  onMove,
  onRemove,
}: {
  reading: OverlayReading;
  source: OverlaySource | undefined;
  /** For a percentage of a value with its total (RAM, VRAM): that value, so it can read as either. */
  valueSource?: OverlaySource;
  iconsOn: boolean;
  /** Opened and brought into sight when this changes (the reading clicked on the screen). */
  focus?: number;
  panelStyle: OverlayStyle;
  tiles: boolean;
  first: boolean;
  last: boolean;
  onChange: (patch: Partial<OverlayReading>) => void;
  onMove: (by: number) => void;
  onRemove: () => void;
}) {
  const [open, setOpen] = useState(false);
  const row = useRef<HTMLLIElement>(null);
  useEffect(() => {
    if (focus === undefined) return;
    setOpen(true);
    window.setTimeout(() => row.current?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' }), 0);
  }, [focus]);
  const shownValue = shown(source, reading);
  const numeric = source?.text == null;
  const visual = visualOf(reading);
  const scaled = maxOf(reading, source) != null;
  // Without a full scale of its own, a ring or bar is drawn against an automatic one (as the recorder draws it).
  const auto = source && !scaled ? autoMax(source) : null;
  const autoShown = auto != null && source ? shown({ ...source, value: auto, max: null }, { ...reading, max: undefined }) : null;
  const setVisual = (v: OverlayVisual) => onChange({ visual: v, bar: false });
  return (
    <li ref={row} className={cn('rounded-md border p-2', focus !== undefined && open ? 'border-accent/60' : 'border-border')}>
      <div className="flex items-center gap-1.5">
        <button type="button" className="flex min-w-0 flex-1 items-center gap-1.5 text-left" aria-expanded={open} onClick={() => setOpen(!open)}>
          <ChevronDown className={cn('size-3.5 shrink-0 text-muted transition-transform', open && 'rotate-180')} aria-hidden />
          <span className="min-w-0 flex-1 truncate text-sm">
            {reading.label || source?.label || reading.source}
            <span className="text-xs text-muted"> · {source ? source.group : 'not available on this PC now'}</span>
          </span>
          {visual !== 'none' && <span className="shrink-0 rounded bg-surface-muted px-1.5 text-[10px] uppercase tracking-wide text-muted">{VISUALS.find((v) => v.id === visual)?.name ?? visual}</span>}
          <span className="shrink-0 text-xs tabular-nums text-muted">
            {shownValue.value} {shownValue.unit}
          </span>
        </button>
        <Button size="icon" variant="ghost" className="size-7" aria-label="Move up" disabled={first} onClick={() => onMove(-1)}>
          <ArrowUp aria-hidden />
        </Button>
        <Button size="icon" variant="ghost" className="size-7" aria-label="Move down" disabled={last} onClick={() => onMove(1)}>
          <ArrowDown aria-hidden />
        </Button>
        <Button size="icon" variant="ghost" className="size-7" aria-label={`Remove ${reading.label || source?.label || reading.source}`} onClick={onRemove}>
          <X aria-hidden />
        </Button>
      </div>
      {open && (
        <div className="mt-2 flex flex-col gap-2">
          {/* Its value or a percentage: for every number (a percentage only when it is one and nothing else, as CPU load). */}
          {numeric && source?.unit === '%' && !(valueSource?.max != null && valueSource.max > 0) && (
            <Field label="Reads as" group>
              <span className="text-xs text-muted">A percentage (there is no other value to show for it).</span>
            </Field>
          )}
          {numeric && (source?.unit !== '%' || (valueSource?.max != null && valueSource.max > 0)) && (
            <Field label="Reads as" group>
              <Segmented
                label="Reads as"
                value={readsAs(reading, source)}
                options={SHOWS.map((v) => [v.id, v.name] as const)}
                onChange={(show) => {
                  const next = readAs(reading, source, valueSource, show);
                  onChange({ source: next.source, show: next.show, warning: next.warning, critical: next.critical, max: next.max });
                }}
              />
            </Field>
          )}
          {numeric && source?.unit !== '%' && readsAs(reading, source) !== 'value' && !hasTotal(reading, source) && (
            <p className="text-xs text-warning">Reading it as a percentage or of a total needs its full scale: set “Full at” below.</p>
          )}
          {numeric && (
            <Field label="Shown with" group>
              <VisualPicker value={visual} onChange={setVisual} />
            </Field>
          )}
          {numeric && needsScale(visual) && !scaled && (
            <p className="text-xs text-muted">
              Drawn against an automatic scale{autoShown ? ` (up to ${autoShown.value} ${autoShown.unit})` : ''} that follows the value; set “Full at” below for a fixed one.
            </p>
          )}
          {numeric && visual === 'status' && reading.warning == null && reading.critical == null && <p className="text-xs text-warning">A status light turns amber or red at your limits: set “Warning at” or “Critical at” below.</p>}
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
            <Field label="Label">
              <input className={input} value={reading.label ?? ''} maxLength={MAX_TEXT} placeholder={source?.label ?? ''} onChange={(e) => onChange({ label: e.target.value || null })} />
            </Field>
            {numeric && (
              <>
                <Field label="Decimals">
                  <select className={input} value={reading.decimals ?? ''} onChange={(e) => onChange({ decimals: e.target.value === '' ? null : Number(e.target.value) })}>
                    <option value="">Usual</option>
                    {[0, 1, 2, 3].map((d) => (
                      <option key={d} value={d}>
                        {d}
                      </option>
                    ))}
                  </select>
                </Field>
                <OptionalColor label="Color" value={reading.color ?? null} fallback="#38BDF8" empty="Panel's" onChange={(color) => onChange({ color })} />
                <Field label={`Warning at${source?.unit ? ` (${source.unit === 'B' || source.unit === 'B/s' ? 'bytes' : source.unit})` : ''}`}>
                  <input className={input} type="number" value={reading.warning ?? ''} onChange={(e) => onChange({ warning: num(e.target.value) })} />
                </Field>
                <Field label="Critical at">
                  <input className={input} type="number" value={reading.critical ?? ''} onChange={(e) => onChange({ critical: num(e.target.value) })} />
                </Field>
                <Field label="Limits">
                  <select className={input} value={reading.below ? 'below' : 'above'} onChange={(e) => onChange({ below: e.target.value === 'below' })}>
                    <option value="above">Bad when higher</option>
                    <option value="below">Bad when lower</option>
                  </select>
                </Field>
                {source?.unit !== '%' && (
                  <Field label={source?.max ? 'Full at (its total when empty)' : 'Full at'}>
                    <input className={input} type="number" min={0} placeholder="For bars and rings" aria-label="Full at" value={reading.max ?? ''} onChange={(e) => onChange({ max: num(e.target.value) })} />
                  </Field>
                )}
              </>
            )}
          </div>
          {iconsOn && <IconPicker value={reading.icon ?? null} onChange={(icon) => onChange({ icon })} />}
          <ReadingLook reading={reading} visual={numeric ? visual : 'none'} panelStyle={panelStyle} tiles={tiles} onChange={(look) => onChange({ look })} />
        </div>
      )}
    </li>
  );
}

type Look = NonNullable<OverlayReading['look']>;

/** On, off, or as the panel. */
function Tri({ label, value, onChange }: { label: string; value: boolean | null | undefined; onChange: (v: boolean | null) => void }) {
  return (
    <Field label={label} group>
      <Segmented
        label={label}
        value={value == null ? 'panel' : value ? 'on' : 'off'}
        options={[
          ['panel', "Panel's"],
          ['on', 'On'],
          ['off', 'Off'],
        ]}
        onChange={(v) => onChange(v === 'panel' ? null : v === 'on')}
      />
    </Field>
  );
}

/**
 * A reading's own look, apart from the panel's Look: its text (font, sizes, colors, what shows) and its visual's
 * (a dial's size and thickness, a chart's size and line, a bar's style and thickness, their track), only what applies
 * to the way it is shown. Each follows the panel's until set.
 */
function ReadingLook({ reading, visual, panelStyle, tiles, onChange }: { reading: OverlayReading; visual: OverlayVisual; panelStyle: OverlayStyle; tiles: boolean; onChange: (look: Look | null) => void }) {
  const look: Look = reading.look ?? {};
  const set = (patch: Partial<Look>) => {
    const next = Object.fromEntries(Object.entries({ ...look, ...patch }).filter(([, v]) => v != null)) as Look;
    onChange(Object.keys(next).length ? next : null);
  };
  const own = Object.keys(look).length;
  const dial = visual === 'ring' || visual === 'arc' || visual === 'speed';
  const chart = visual === 'graph' || visual === 'line' || visual === 'columns';
  const sized = dial || chart || visual === 'status';
  const thick = dial || visual === 'bar' || visual === 'thermo' || visual === 'graph' || visual === 'line';
  const tracked = dial || visual === 'bar' || visual === 'thermo' || chart;
  return (
    <section aria-label="Its look">
      <Group title={own ? `Its look · ${own} own` : 'Its look'}>
        <p className="text-xs text-muted">This reading only; anything not set follows the panel's Look.</p>
        <Sub title="Value" />
        <div className="grid grid-cols-2 gap-x-3 gap-y-2">
          <Field label="Font">
            <select className={input} aria-label="Its font" value={look.font ?? ''} onChange={(e) => set({ font: e.target.value || null })}>
              <option value="">Panel's ({panelStyle.font})</option>
              {FONTS.map((f) => (
                <option key={f} value={f}>
                  {f}
                </option>
              ))}
            </select>
          </Field>
          <OptionalColor label="Value" value={look.valueColor ?? null} fallback={reading.color ?? panelStyle.valueColor} empty="Panel's" onChange={(valueColor) => set({ valueColor })} />
        </div>
        <Slider label="Value size" value={look.valueSize ?? 100} min={50} max={300} step={5} unit="%" onChange={(valueSize) => set({ valueSize: valueSize === 100 ? null : valueSize })} />
        <Tri label="Bold" value={look.bold} onChange={(bold) => set({ bold })} />
        <Field label="Sits" group>
          <Segmented
            label="Its value sits"
            value={look.align ?? 'panel'}
            options={[['panel', "Panel's"] as const, ...VALUE_ALIGNS.map((a) => [a.id, a.name] as const)]}
            onChange={(align) => set({ align: align === 'panel' ? null : align })}
          />
        </Field>
        {tiles && !dial && !chart && (
          <Field label="Up and down" group>
            <Segmented
              label="Its value up and down"
              value={look.vertical ?? 'panel'}
              options={[['panel', "Panel's"] as const, ...VALUE_VERTICALS.map((a) => [a.id, a.name] as const)]}
              onChange={(vertical) => set({ vertical: vertical === 'panel' ? null : vertical })}
            />
          </Field>
        )}
        <Sub title="Label and unit" />
        <Tri label="Label" value={look.showLabel} onChange={(showLabel) => set({ showLabel })} />
        <Slider label="Label size" value={look.labelSize ?? 100} min={50} max={250} step={5} unit="%" onChange={(labelSize) => set({ labelSize: labelSize === 100 ? null : labelSize })} />
        <div className="grid grid-cols-2 gap-x-3 gap-y-2">
          <OptionalColor label="Label" value={look.labelColor ?? null} fallback={panelStyle.labelColor} empty="Panel's" onChange={(labelColor) => set({ labelColor })} />
          <OptionalColor label="Unit" value={look.unitColor ?? null} fallback={panelStyle.unitColor} empty="Panel's" onChange={(unitColor) => set({ unitColor })} />
        </div>
        <Tri label="Unit" value={look.showUnit} onChange={(showUnit) => set({ showUnit })} />
        {(sized || thick || tracked) && <Sub title={VISUALS.find((v) => v.id === visual)?.name ?? 'Visual'} />}
        {sized && (
          <Slider
            label={dial ? 'Dial size' : chart ? 'Chart size' : 'Light size'}
            value={look.visualSize ?? 100}
            min={50}
            max={250}
            step={5}
            unit="%"
            onChange={(visualSize) => set({ visualSize: visualSize === 100 ? null : visualSize })}
          />
        )}
        {thick && (
          <Slider
            label={visual === 'graph' || visual === 'line' ? 'Line thickness' : 'Thickness'}
            value={look.thickness ?? 100}
            min={25}
            max={400}
            step={5}
            unit="%"
            onChange={(thickness) => set({ thickness: thickness === 100 ? null : thickness })}
          />
        )}
        {visual === 'bar' && (
          <Field label="Bar" group>
            <Segmented
              label="Its bar style"
              value={look.barStyle ?? 'panel'}
              options={[
                ['panel', "Panel's"],
                ['rounded', 'Rounded'],
                ['solid', 'Square'],
                ['segments', 'Segments'],
              ]}
              onChange={(barStyle) => set({ barStyle: barStyle === 'panel' ? null : barStyle })}
            />
          </Field>
        )}
        {visual === 'graph' && (
          <Slider label="Area under the line" value={look.fillOpacity ?? 43} min={0} max={100} step={1} unit="%" onChange={(fillOpacity) => set({ fillOpacity: fillOpacity === 43 ? null : fillOpacity })} />
        )}
        {tracked && (
          <>
            <OptionalColor label={chart ? 'Baseline' : 'Track'} value={look.trackColor ?? null} fallback={panelStyle.labelColor} empty="Panel's" onChange={(trackColor) => set({ trackColor })} />
            <Slider
              label={chart ? 'Baseline opacity' : 'Track opacity'}
              value={look.trackOpacity ?? 22}
              min={0}
              max={100}
              step={1}
              unit="%"
              onChange={(trackOpacity) => set({ trackOpacity: trackOpacity === 22 ? null : trackOpacity })}
            />
          </>
        )}
        {own > 0 && (
          <Button size="sm" variant="ghost" className="self-start" onClick={() => onChange(null)}>
            Same as the panel
          </Button>
        )}
      </Group>
    </section>
  );
}

/** Automatic, none, or one of the icons. */
function IconPicker({ value, onChange }: { value: string | null; onChange: (icon: string | null) => void }) {
  const choice = (id: string | null, label: string, glyph: ReactNode) => (
    <button
      key={id ?? 'auto'}
      type="button"
      role="radio"
      aria-checked={value === id}
      aria-label={label}
      title={label}
      onClick={() => onChange(id)}
      className={cn('flex size-8 items-center justify-center rounded-md border text-xs', value === id ? 'border-accent bg-accent/10 text-accent' : 'border-border text-muted hover:border-accent/60')}
    >
      {glyph}
    </button>
  );
  return (
    <div className="flex flex-col gap-1 text-xs text-muted">
      Icon
      <div role="radiogroup" aria-label="Icon" className="flex flex-wrap gap-1">
        {choice(null, 'Automatic', 'Auto')}
        {choice('none', 'No icon', '—')}
        {(Object.keys(ICON_PATHS) as IconId[]).map((id) =>
          choice(
            id,
            ICON_NAMES[id],
            <svg width={16} height={16} viewBox="0 0 24 24" fill="none" aria-hidden>
              <path d={ICON_PATHS[id]} stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" />
            </svg>,
          ),
        )}
      </div>
    </div>
  );
}

/** Every value the recorder offers, grouped by where it comes from, with a search. */
function SourcePicker({ sources, onPick }: { sources: OverlaySource[]; onPick: (id: string) => void }) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState('');
  const groups = useMemo(() => {
    const needle = text.trim().toLowerCase();
    const map = new Map<string, OverlaySource[]>();
    for (const s of sources) {
      if (needle && !`${s.label} ${s.group} ${s.id}`.toLowerCase().includes(needle)) continue;
      map.set(s.group, [...(map.get(s.group) ?? []), s]);
    }
    return [...map.entries()];
  }, [sources, text]);

  if (!open) {
    return (
      <Button size="sm" variant="outline" className="self-start" onClick={() => setOpen(true)}>
        <Plus aria-hidden /> Add a reading
      </Button>
    );
  }

  return (
    <div className="rounded-md border border-border p-2" role="region" aria-label="Add a reading">
      <div className="mb-2 flex items-center gap-2">
        <label className="relative flex-1">
          <span className="sr-only">Find a value</span>
          <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
          <input autoFocus type="search" value={text} onChange={(e) => setText(e.target.value)} placeholder="Find, e.g. hot spot, fan, voltage" className={cn(input, 'pl-7')} />
        </label>
        <Button size="sm" variant="ghost" onClick={() => setOpen(false)}>
          Done
        </Button>
      </div>
      <div className="max-h-72 overflow-y-auto pr-1">
        {groups.length === 0 && <p className="text-xs text-muted">{sources.length === 0 ? 'The recorder has not sent its values yet.' : 'Nothing matches.'}</p>}
        {groups.map(([group, items]) => (
          <details key={group} open={!!text.trim() || groups.length <= 3} className="mb-1">
            <summary className="cursor-pointer text-xs font-medium text-muted">
              {group} <span className="tabular-nums">({items.length})</span>
            </summary>
            <ul className="mt-1 flex flex-col">
              {items.map((s) => {
                const shown = formatSource(s);
                return (
                  <li key={s.id}>
                    <button type="button" onClick={() => onPick(s.id)} className="flex w-full items-center justify-between gap-2 rounded px-2 py-1 text-left text-sm hover:bg-surface-muted">
                      <span className="truncate">{s.label}</span>
                      <span className="shrink-0 text-xs tabular-nums text-muted">
                        {shown.value} {shown.unit}
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </details>
        ))}
      </div>
    </div>
  );
}
