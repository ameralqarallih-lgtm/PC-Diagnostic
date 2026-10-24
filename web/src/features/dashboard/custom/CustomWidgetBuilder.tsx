import { ArrowDown, ArrowUp, ChevronDown, ChevronRight, Radio, Search, X } from 'lucide-react';
import { useEffect, useId, useMemo, useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { TabUnderline } from '@/components/ui/tab-indicator';
import { cn } from '@/lib/utils';
import { useDashboardData } from '../DashboardContext';
import { EVENT_SOURCES } from '../config/widgets';
import type { WidgetSettings } from '../config/types';
import { AccentScope, CustomWidgetView } from './CustomWidget';
import {
  ACCENTS,
  AGGREGATES,
  defaultCustom,
  DESIGN_INFO,
  DESIGNS,
  PALETTES,
  PROCESS_MEASURES,
  readCustom,
  sourceColor,
  WINDOWS,
  writeCustom,
  type CustomConfig,
  type CustomDesign,
} from './model';
import { missingSource, type Source } from './sources';
import { useSources } from './useCustomData';

export interface BuilderResult {
  title: string | null;
  settings: WidgetSettings;
  /** Keep a copy in "My widgets" to add again later. */
  saveToLibrary: boolean;
}

export interface CustomWidgetBuilderProps {
  open: boolean;
  /** What is being built: a new widget, a widget on the dashboard, or one in "My widgets". */
  mode: 'add' | 'edit' | 'library';
  initial: { title: string | null; settings: WidgetSettings } | null;
  onClose: () => void;
  onSave: (result: BuilderResult) => void;
}

const INPUT = 'h-9 w-full rounded-md border border-border bg-background px-2 text-sm';

/**
 * Builds a widget: pick a design, the values it shows (any recorded series, every hardware sensor, any live
 * value), how it looks and its limits, while a preview shows it with the live data.
 */
export function CustomWidgetBuilder({ open, mode, initial, onClose, onSave }: CustomWidgetBuilderProps) {
  const [config, setConfig] = useState<CustomConfig>(() => defaultCustom('value'));
  const [title, setTitle] = useState('');
  const [accent, setAccent] = useState('');
  const [saveToLibrary, setSaveToLibrary] = useState(true);
  const [tab, setTab] = useState<'values' | 'look' | 'limits'>('values');

  useEffect(() => {
    if (!open) return;
    const settings = initial?.settings ?? {};
    setConfig(initial ? { ...defaultCustom(readCustom(settings).design), ...readCustom(settings) } : defaultCustom('value'));
    setTitle(initial?.title ?? '');
    setAccent(typeof settings.accent === 'string' ? settings.accent : '');
    setSaveToLibrary(mode === 'add');
    setTab('values');
  }, [open, initial, mode]);

  const info = DESIGN_INFO[config.design];
  const update = (patch: Partial<CustomConfig>) => setConfig((c) => ({ ...c, ...patch }));
  const chooseDesign = (design: CustomDesign) =>
    setConfig((c) => {
      const fresh = defaultCustom(design);
      // Keep the values chosen so far when the new design takes values too.
      const keep = DESIGN_INFO[design].maxSources > 0 && c.sources.length > 0;
      return {
        ...fresh,
        sources: keep ? c.sources.slice(0, DESIGN_INFO[design].maxSources) : fresh.sources,
        labels: keep ? c.labels : [],
        colors: keep ? c.colors : [],
        warning: keep ? c.warning : fresh.warning,
        critical: keep ? c.critical : fresh.critical,
        direction: c.direction,
        window: c.window,
        palette: c.palette,
      };
    });

  const tabs = info.maxSources > 0 ? (['values', 'look', 'limits'] as const) : (['look'] as const);
  const activeTab = (tabs as readonly string[]).includes(tab) ? tab : tabs[0];

  return (
    <Dialog
      open={open}
      title={mode === 'add' ? 'Build a widget' : 'Edit widget'}
      description="Choose a design and what it shows; the preview uses the live data."
      onClose={onClose}
      className="max-w-6xl"
      footer={
        <>
          {mode !== 'library' && (
            <label className="mr-auto flex items-center gap-2 text-sm">
              <input type="checkbox" checked={saveToLibrary} onChange={(e) => setSaveToLibrary(e.target.checked)} className="size-4 accent-[var(--accent)]" />
              {mode === 'add' ? 'Also keep it in My widgets' : 'Also save a copy in My widgets'}
            </label>
          )}
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => {
              onSave({ title: title.trim() || null, settings: writeCustom(config, accent), saveToLibrary: mode === 'library' ? false : saveToLibrary });
              onClose();
            }}
          >
            {mode === 'add' ? 'Add to the dashboard' : 'Apply'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        <fieldset>
          <legend className="mb-2 text-sm font-medium">Design</legend>
          <div role="radiogroup" aria-label="Design" className="grid grid-cols-2 gap-1.5 sm:grid-cols-4 lg:grid-cols-6 xl:grid-cols-11">
            {DESIGNS.map((design) => {
              const d = DESIGN_INFO[design];
              const Icon = d.icon;
              return (
                <button
                  key={design}
                  type="button"
                  role="radio"
                  aria-checked={config.design === design}
                  title={d.description}
                  onClick={() => chooseDesign(design)}
                  className={cn(
                    'flex flex-col items-center gap-1 rounded-md border px-1 py-2 text-xs',
                    config.design === design ? 'border-accent bg-accent/10 text-foreground' : 'border-border text-muted hover:border-accent/50 hover:text-foreground',
                  )}
                >
                  <Icon className="size-5" aria-hidden />
                  <span className="text-center leading-tight">{d.name}</span>
                </button>
              );
            })}
          </div>
          <p className="mt-1.5 text-xs text-muted">{info.description}</p>
        </fieldset>

        <div className="grid grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
          <div className="flex min-w-0 flex-col gap-4">
            {tabs.length > 1 && (
              <div role="tablist" aria-label="Widget settings" className="flex gap-1 border-b border-border">
                {tabs.map((t) => (
                  <button
                    key={t}
                    type="button"
                    role="tab"
                    aria-selected={activeTab === t}
                    onClick={() => setTab(t)}
                    className={cn('relative -mb-px border-b-2 border-transparent px-3 py-1.5 text-sm transition-colors', activeTab === t ? 'font-medium' : 'text-muted hover:text-foreground')}
                  >
                    {activeTab === t && <TabUnderline group="widget-builder-tabs" />}
                    {t === 'values' ? `Values (${config.sources.length}/${info.maxSources})` : t === 'look' ? 'Look' : 'Scale and limits'}
                  </button>
                ))}
              </div>
            )}
            {activeTab === 'values' && <SourcesEditor config={config} onChange={update} />}
            {activeTab === 'look' && <LookEditor config={config} onChange={update} title={title} onTitle={setTitle} accent={accent} onAccent={setAccent} />}
            {activeTab === 'limits' && <LimitsEditor config={config} onChange={update} />}
          </div>

          <section aria-label="Preview" className="flex min-w-0 flex-col gap-2 lg:sticky lg:top-0 lg:self-start">
            <h3 className="text-sm font-medium">Preview</h3>
            <AccentScope accent={accent} className="@container" >
              <div style={{ height: `${Math.max(9, Math.min(24, info.size.h * 2.6))}rem` }}>
                <CustomWidgetView config={config} title={title || null} viewKey="custom.preview" />
              </div>
            </AccentScope>
            <p className="text-xs text-muted">
              It starts at {info.size.w} of 12 columns wide; resize it on the dashboard like any widget.
            </p>
          </section>
        </div>
      </div>
    </Dialog>
  );
}

function Field({ label, help, children }: { label: string; help?: string; children: (id: string) => ReactNode }) {
  const id = useId();
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      {children(id)}
      {help && <p className="text-xs text-muted">{help}</p>}
    </div>
  );
}

function Toggle({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="flex items-center gap-2 text-sm">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="size-4 accent-[var(--accent)]" />
      {label}
    </label>
  );
}

function SourcesEditor({ config, onChange }: { config: CustomConfig; onChange: (patch: Partial<CustomConfig>) => void }) {
  const { live } = useDashboardData();
  const { sources, byId, loading } = useSources(live);
  const [filter, setFilter] = useState('');
  const [showRemoved, setShowRemoved] = useState(false);
  const [openGroups, setOpenGroups] = useState<Set<string>>(() => new Set(['System']));
  const max = DESIGN_INFO[config.design].maxSources;
  const full = config.sources.length >= max;

  const groups = useMemo(() => {
    const text = filter.trim().toLowerCase();
    const map = new Map<string, Source[]>();
    for (const s of sources) {
      if (s.stale && !showRemoved && !config.sources.includes(s.id)) continue;
      if (text && !`${s.label} ${s.group} ${s.unit}`.toLowerCase().includes(text)) continue;
      map.set(s.group, [...(map.get(s.group) ?? []), s]);
    }
    return [...map.entries()];
  }, [sources, filter, showRemoved, config.sources]);

  const set = (ids: string[], labels: string[], colors: string[]) => onChange({ sources: ids, labels, colors });
  const toggle = (id: string) => {
    const index = config.sources.indexOf(id);
    if (index >= 0) {
      set(
        config.sources.filter((_, i) => i !== index),
        config.labels.filter((_, i) => i !== index),
        config.colors.filter((_, i) => i !== index),
      );
    } else if (!full) {
      set([...config.sources, id], [...pad(config.labels, config.sources.length), ''], [...pad(config.colors, config.sources.length), '']);
    }
  };
  const move = (index: number, by: -1 | 1) => {
    const target = index + by;
    if (target < 0 || target >= config.sources.length) return;
    const swap = <T,>(list: T[]) => {
      const next = [...pad(list as string[], config.sources.length)] as T[];
      [next[index], next[target]] = [next[target]!, next[index]!];
      return next;
    };
    set(swap(config.sources), swap(config.labels), swap(config.colors));
  };
  const setAt = (key: 'labels' | 'colors', index: number, value: string) => {
    const next = pad(config[key], config.sources.length);
    next[index] = value;
    onChange({ [key]: next });
  };

  return (
    <div className="flex flex-col gap-4">
      <section aria-label="Chosen values" className="flex flex-col gap-1.5">
        <h3 className="text-sm font-medium">
          Shown ({config.sources.length} of at most {max})
        </h3>
        {config.sources.length === 0 && <p className="text-sm text-muted">Nothing yet: tick values below.</p>}
        <ol className="flex flex-col gap-1.5">
          {config.sources.map((id, index) => {
            const source = byId.get(id) ?? missingSource(id);
            return (
              <li key={id} className="flex items-center gap-1.5 rounded-md border border-border px-2 py-1.5">
                <input
                  type="color"
                  value={toHex(sourceColor(config, index))}
                  onChange={(e) => setAt('colors', index, e.target.value)}
                  aria-label={`Color of ${source.label}`}
                  className="size-6 shrink-0 cursor-pointer rounded border-0 bg-transparent p-0"
                />
                <div className="min-w-0 flex-1">
                  <input
                    value={config.labels[index] ?? ''}
                    onChange={(e) => setAt('labels', index, e.target.value.slice(0, 60))}
                    placeholder={source.label}
                    aria-label={`Label of ${source.label}`}
                    className="h-7 w-full rounded border border-transparent bg-transparent px-1 text-sm placeholder:text-foreground hover:border-border focus:border-border"
                  />
                  <p className="truncate px-1 text-[11px] text-muted">
                    {source.group} · {source.unit || 'count'}
                    {source.stale && ' · not seen for a day'}
                    {source.group === 'Not found' && ' · not on this PC'}
                  </p>
                </div>
                <IconButton label={`Move ${source.label} up`} onClick={() => move(index, -1)} disabled={index === 0}>
                  <ArrowUp aria-hidden />
                </IconButton>
                <IconButton label={`Move ${source.label} down`} onClick={() => move(index, 1)} disabled={index === config.sources.length - 1}>
                  <ArrowDown aria-hidden />
                </IconButton>
                <IconButton label={`Remove ${source.label}`} onClick={() => toggle(id)}>
                  <X aria-hidden />
                </IconButton>
              </li>
            );
          })}
        </ol>
      </section>

      <section aria-label="Available values" className="flex flex-col gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <label className="relative min-w-48 flex-1">
            <span className="sr-only">Find a value</span>
            <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
            <input type="search" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Find a value, e.g. fan, hotspot, Samsung" className={`${INPUT} pl-7`} />
          </label>
          <Toggle label="Removed devices" checked={showRemoved} onChange={setShowRemoved} />
        </div>
        <p className="flex items-center gap-1.5 text-xs text-muted">
          <Radio className="size-3.5 text-success" aria-hidden /> live once a second; the others come from the recording (every few seconds while recording).
        </p>
        {loading && <p className="text-sm text-muted">Loading the values this PC has…</p>}
        <div className="flex max-h-80 flex-col overflow-auto rounded-md border border-border">
          {groups.map(([group, items]) => {
            const opened = filter.trim() !== '' || openGroups.has(group);
            const chosen = items.filter((s) => config.sources.includes(s.id)).length;
            return (
              <div key={group} className="border-b border-border last:border-0">
                <button
                  type="button"
                  aria-expanded={opened}
                  onClick={() => setOpenGroups((g) => new Set(g.has(group) ? [...g].filter((x) => x !== group) : [...g, group]))}
                  className="flex w-full items-center gap-1.5 bg-surface-muted/50 px-2 py-1.5 text-left text-xs font-semibold"
                >
                  {opened ? <ChevronDown className="size-3.5" aria-hidden /> : <ChevronRight className="size-3.5" aria-hidden />}
                  <span className="truncate">{group}</span>
                  <span className="ml-auto font-normal text-muted">
                    {chosen > 0 ? `${chosen} chosen · ` : ''}
                    {items.length}
                  </span>
                </button>
                {opened && (
                  <ul className="flex flex-col">
                    {items.map((s) => {
                      const checked = config.sources.includes(s.id);
                      return (
                        <li key={s.id}>
                          <label className={cn('flex items-center gap-2 px-3 py-1 text-sm hover:bg-surface-muted', !checked && full && 'opacity-50')}>
                            <input type="checkbox" checked={checked} disabled={!checked && full} onChange={() => toggle(s.id)} className="size-4 accent-[var(--accent)]" />
                            <span className="min-w-0 flex-1 truncate" title={s.label}>
                              {s.label}
                            </span>
                            {s.liveId && <Radio className="size-3.5 shrink-0 text-success" aria-label="Live" />}
                            <span className="w-12 shrink-0 text-right text-xs text-muted">{s.unit}</span>
                          </label>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </div>
            );
          })}
          {!loading && groups.length === 0 && <p className="p-3 text-sm text-muted">Nothing matches.</p>}
        </div>
      </section>
    </div>
  );
}

function pad(list: string[], length: number): string[] {
  return Array.from({ length }, (_, i) => list[i] ?? '');
}

function toHex(color: string): string {
  return /^#[0-9a-f]{6}$/i.test(color) ? color : '#38bdf8';
}

function IconButton({ label, onClick, disabled, children }: { label: string; onClick: () => void; disabled?: boolean; children: ReactNode }) {
  return (
    <button type="button" aria-label={label} title={label} onClick={onClick} disabled={disabled} className="rounded p-1 text-muted hover:bg-surface-muted hover:text-foreground disabled:opacity-30 [&_svg]:size-3.5">
      {children}
    </button>
  );
}

function LookEditor({
  config,
  onChange,
  title,
  onTitle,
  accent,
  onAccent,
}: {
  config: CustomConfig;
  onChange: (patch: Partial<CustomConfig>) => void;
  title: string;
  onTitle: (title: string) => void;
  accent: string;
  onAccent: (accent: string) => void;
}) {
  const info = DESIGN_INFO[config.design];
  const d = config.design;
  return (
    <div className="flex flex-col gap-4">
      <Field label="Title">{(id) => <input id={id} value={title} maxLength={60} placeholder={info.name} onChange={(e) => onTitle(e.target.value)} className={INPUT} />}</Field>

      {info.maxSources > 0 && (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label={info.overTime ? 'Time window' : 'Window for trends and low/high'}>
            {(id) => (
              <select id={id} value={String(config.window)} onChange={(e) => onChange({ window: Number(e.target.value) })} className={INPUT}>
                {WINDOWS.map((w) => (
                  <option key={w.value} value={w.value}>
                    {w.label}
                  </option>
                ))}
              </select>
            )}
          </Field>
          {!info.overTime && d !== 'table' && (
            <Field label="Shows">
              {(id) => (
                <select id={id} value={config.aggregate} onChange={(e) => onChange({ aggregate: e.target.value as CustomConfig['aggregate'] })} className={INPUT}>
                  {AGGREGATES.map((a) => (
                    <option key={a.value} value={a.value}>
                      {a.label}
                    </option>
                  ))}
                </select>
              )}
            </Field>
          )}
          <Field label="Decimals">
            {(id) => (
              <select id={id} value={config.decimals == null ? '' : String(config.decimals)} onChange={(e) => onChange({ decimals: e.target.value === '' ? null : Number(e.target.value) })} className={INPUT}>
                <option value="">Automatic</option>
                {[0, 1, 2, 3].map((n) => (
                  <option key={n} value={n}>
                    {n}
                  </option>
                ))}
              </select>
            )}
          </Field>
          {d === 'gauge' && (
            <Field label="Dial">
              {(id) => (
                <select id={id} value={config.dial} onChange={(e) => onChange({ dial: e.target.value as CustomConfig['dial'] })} className={INPUT}>
                  <option value="arc">Arc</option>
                  <option value="ring">Ring</option>
                  <option value="speed">Speedometer</option>
                  <option value="thermo">Thermometer</option>
                </select>
              )}
            </Field>
          )}
        </div>
      )}

      {(d === 'value' || d === 'bars' || d === 'tiles' || d === 'note') && (
        <fieldset className="flex flex-col gap-1.5">
          <legend className="text-sm font-medium">Text size</legend>
          <div className="flex gap-3 text-sm">
            {(['s', 'm', 'l'] as const).map((size) => (
              <label key={size} className="flex items-center gap-1.5">
                <input type="radio" name="text-size" checked={config.size === size} onChange={() => onChange({ size })} className="accent-[var(--accent)]" />
                {size === 's' ? 'Small' : size === 'm' ? 'Medium' : 'Large'}
              </label>
            ))}
          </div>
        </fieldset>
      )}

      {info.maxSources > 0 && (
        <fieldset className="flex flex-col gap-1.5">
          <legend className="text-sm font-medium">Colors</legend>
          <div className="flex flex-wrap gap-2">
            {Object.entries(PALETTES).map(([id, palette]) => (
              <button
                key={id}
                type="button"
                aria-pressed={config.palette === id}
                onClick={() => onChange({ palette: id, colors: [] })}
                className={cn('flex items-center gap-1.5 rounded-md border px-2 py-1 text-xs', config.palette === id ? 'border-accent' : 'border-border')}
              >
                <span className="flex">
                  {palette.colors.slice(0, 5).map((c) => (
                    <span key={c} className="-ml-0.5 size-3 rounded-full border border-surface first:ml-0" style={{ background: c }} aria-hidden />
                  ))}
                </span>
                {palette.name}
              </button>
            ))}
          </div>
          <p className="text-xs text-muted">Each value can also have its own color (Values tab).</p>
        </fieldset>
      )}

      <div className="flex flex-col gap-2">
        {d === 'value' && <Toggle label="Trend line" checked={config.spark} onChange={(spark) => onChange({ spark })} />}
        {d === 'value' && <Toggle label="Lowest, average and highest" checked={config.legend} onChange={(legend) => onChange({ legend })} />}
        {d === 'table' && <Toggle label="Trend column" checked={config.spark} onChange={(spark) => onChange({ spark })} />}
        {(d === 'line' || d === 'area') && (
          <>
            <Toggle label="Legend" checked={config.legend} onChange={(legend) => onChange({ legend })} />
            <Toggle label="Smooth lines" checked={config.smooth} onChange={(smooth) => onChange({ smooth })} />
          </>
        )}
        {d === 'area' && <Toggle label="Stack the areas (a total made of parts)" checked={config.stacked} onChange={(stacked) => onChange({ stacked })} />}
      </div>

      {d === 'processes' && (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label="Busiest by">
            {(id) => (
              <select id={id} value={config.measure} onChange={(e) => onChange({ measure: e.target.value })} className={INPUT}>
                {PROCESS_MEASURES.map((m) => (
                  <option key={m.value} value={m.value}>
                    {m.label}
                  </option>
                ))}
              </select>
            )}
          </Field>
          <Field label="Processes listed">
            {(id) => (
              <select id={id} value={String(config.count)} onChange={(e) => onChange({ count: Number(e.target.value) })} className={INPUT}>
                {[3, 5, 8, 10, 15, 20].map((n) => (
                  <option key={n} value={n}>
                    {n}
                  </option>
                ))}
              </select>
            )}
          </Field>
          <Field label="Only processes named" help="Part of a name; several separated by commas (e.g. chrome, steam). Empty lists all.">
            {(id) => <input id={id} value={config.match} maxLength={200} onChange={(e) => onChange({ match: e.target.value })} className={INPUT} />}
          </Field>
          <div className="self-end pb-1">
            <Toggle label="Usage bars" checked={config.showBars} onChange={(showBars) => onChange({ showBars })} />
          </div>
        </div>
      )}

      {d === 'events' && (
        <div className="flex flex-col gap-3">
          <Field label="Shows at least">
            {(id) => (
              <select id={id} value={config.minimum} onChange={(e) => onChange({ minimum: e.target.value })} className={INPUT}>
                <option value="0">All events</option>
                <option value="2">Warnings</option>
                <option value="3">Serious problems</option>
              </select>
            )}
          </Field>
          <fieldset className="flex flex-col gap-1.5">
            <legend className="text-sm font-medium">Sources</legend>
            <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
              {EVENT_SOURCES.map((s) => (
                <Toggle
                  key={s.value}
                  label={s.label}
                  checked={config.eventSources.includes(s.value)}
                  onChange={(on) => onChange({ eventSources: on ? EVENT_SOURCES.map((x) => x.value).filter((v) => v === s.value || config.eventSources.includes(v)) : config.eventSources.filter((v) => v !== s.value) })}
                />
              ))}
            </div>
          </fieldset>
          <Field label="Only events mentioning" help="Words in the title, separated by commas (e.g. nvlddmkm, disk). Empty shows all.">
            {(id) => <input id={id} value={config.match} maxLength={200} onChange={(e) => onChange({ match: e.target.value })} className={INPUT} />}
          </Field>
        </div>
      )}

      {d === 'note' && (
        <div className="flex flex-col gap-3">
          <Field label="Text" help={`${config.text.length}/500`}>
            {(id) => <textarea id={id} value={config.text} maxLength={500} rows={4} onChange={(e) => onChange({ text: e.target.value })} className="w-full rounded-md border border-border bg-background px-2 py-1.5 text-sm" />}
          </Field>
          <Toggle label="Centered" checked={config.align === 'center'} onChange={(on) => onChange({ align: on ? 'center' : 'left' })} />
        </div>
      )}

      <AccentPicker accent={accent} onChange={onAccent} />
    </div>
  );
}

/** The widget's own accent color (shared with the preconfigured widgets' settings). */
export function AccentPicker({ accent, onChange }: { accent: string; onChange: (accent: string) => void }) {
  return (
    <fieldset className="flex flex-col gap-1.5">
      <legend className="text-sm font-medium">Accent color</legend>
      <div className="flex flex-wrap items-center gap-1.5">
        <button
          type="button"
          aria-pressed={!accent}
          onClick={() => onChange('')}
          className={cn('rounded-md border px-2 py-1 text-xs', !accent ? 'border-accent' : 'border-border text-muted')}
        >
          Theme
        </button>
        {ACCENTS.map((color) => (
          <button
            key={color}
            type="button"
            aria-pressed={accent === color}
            aria-label={`Accent ${color}`}
            title={color}
            onClick={() => onChange(color)}
            className={cn('size-6 rounded-full border-2', accent === color ? 'border-foreground' : 'border-transparent')}
            style={{ background: color }}
          />
        ))}
      </div>
      <p className="text-xs text-muted">Buttons, bars and highlights inside the widget take this color.</p>
    </fieldset>
  );
}

function NumberInput({ label, value, onValue, help }: { label: string; value: number | null; onValue: (v: number | null) => void; help?: string }) {
  // The text as typed ("-", "1e"), so a value being typed is not reset to the last valid number.
  const [draft, setDraft] = useState(value == null ? '' : String(value));
  useEffect(() => {
    if ((draft.trim() === '' ? null : Number(draft)) !== value) setDraft(value == null ? '' : String(value));
    // Only an outside change (another design, Reset) replaces what is typed.
  }, [value]);
  return (
    <Field label={label} help={help}>
      {(id) => (
        <input
          id={id}
          type="number"
          step="any"
          value={draft}
          placeholder="Automatic"
          onChange={(e) => {
            setDraft(e.target.value);
            const text = e.target.value.trim();
            onValue(text === '' || !Number.isFinite(Number(text)) ? null : Number(text));
          }}
          className={INPUT}
        />
      )}
    </Field>
  );
}

function LimitsEditor({ config, onChange }: { config: CustomConfig; onChange: (patch: Partial<CustomConfig>) => void }) {
  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <NumberInput label="Scale from" value={config.min} onValue={(min) => onChange({ min })} help="Empty: 0." />
        <NumberInput label="Scale to" value={config.max} onValue={(max) => onChange({ max })} help="Empty: 100 for percentages, else from the values." />
      </div>
      <p className="-mt-2 text-xs text-muted">Values are in their own unit: bytes for sizes and rates (1 GB = 1073741824), °C, W, MHz, RPM.</p>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <NumberInput label="Warning at" value={config.warning} onValue={(warning) => onChange({ warning })} help="Amber from here. Empty: none." />
        <NumberInput label="Critical at" value={config.critical} onValue={(critical) => onChange({ critical })} help="Red from here. Empty: none." />
      </div>
      <fieldset className="flex flex-col gap-1.5">
        <legend className="text-sm font-medium">Which way is worse</legend>
        <label className="flex items-center gap-2 text-sm">
          <input type="radio" name="direction" checked={config.direction === 'up'} onChange={() => onChange({ direction: 'up' })} className="accent-[var(--accent)]" />
          Higher is worse (load, temperature, latency)
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input type="radio" name="direction" checked={config.direction === 'down'} onChange={() => onChange({ direction: 'down' })} className="accent-[var(--accent)]" />
          Lower is worse (free memory, fan speed, Wi-Fi signal)
        </label>
      </fieldset>
    </div>
  );
}
