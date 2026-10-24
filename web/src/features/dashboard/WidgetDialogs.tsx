import { Pencil, Plus, Search, Trash2, Wand2 } from 'lucide-react';
import { useEffect, useId, useState } from 'react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { TabUnderline } from '@/components/ui/tab-indicator';
import { cn } from '@/lib/utils';
import { WIDGET_TYPES, type LayoutEntry, type WidgetInstance, type WidgetSettings, type WidgetType } from './config/types';
import { WIDGETS, type SettingField } from './config/widgets';
import { AccentPicker } from './custom/CustomWidgetBuilder';
import { DESIGN_INFO, DESIGNS, readCustom, type CustomDesign } from './custom/model';

export interface WidgetSettingsDialogProps {
  widget: WidgetInstance | null;
  /** The widget's share of the 12-column grid and height. */
  entry: LayoutEntry | null;
  onClose: () => void;
  onApply: (widget: WidgetInstance, size: { w: number; h: number } | null) => void;
}

/** Title, the data the widget includes, and its size on this screen size. */
export function WidgetSettingsDialog({ widget, entry, onClose, onApply }: WidgetSettingsDialogProps) {
  const [title, setTitle] = useState('');
  const [settings, setSettings] = useState<WidgetSettings>({});
  const [size, setSize] = useState<{ w: number; h: number } | null>(null);
  const titleId = useId();

  useEffect(() => {
    if (!widget) return;
    setTitle(widget.title ?? '');
    setSettings(structuredClone(widget.settings));
    setSize(entry ? { w: entry.w, h: entry.h } : null);
  }, [widget, entry]);

  if (!widget) return <Dialog open={false} title="" onClose={onClose}>{null}</Dialog>;
  const definition = WIDGETS[widget.type];

  return (
    <Dialog
      open
      title={`${definition.name} settings`}
      description={definition.description}
      onClose={onClose}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            onClick={() => {
              onApply({ ...widget, title: title.trim() || null, settings }, size);
              onClose();
            }}
          >
            Apply
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        <div className="flex flex-col gap-1">
          <label htmlFor={titleId} className="text-sm font-medium">
            Title
          </label>
          <input
            id={titleId}
            type="text"
            maxLength={60}
            value={title}
            placeholder={definition.name}
            onChange={(e) => setTitle(e.target.value)}
            className="h-9 rounded-md border border-border bg-background px-3 text-sm placeholder:text-muted"
          />
        </div>

        {definition.fields.map((field) => (
          <FieldEditor key={field.key} field={field} value={settings[field.key]} onChange={(value) => setSettings((s) => ({ ...s, [field.key]: value }))} />
        ))}

        <AccentPicker
          accent={typeof settings.accent === 'string' ? settings.accent : ''}
          onChange={(accent) =>
            setSettings((s) => {
              const next = { ...s };
              if (accent) next.accent = accent;
              else delete next.accent;
              return next;
            })
          }
        />

        {size && (
          <fieldset className="flex flex-col gap-2">
            <legend className="text-sm font-medium">Size</legend>
            <div className="flex flex-wrap gap-4">
              <NumberSelect label="Width (of 12 columns)" value={size.w} min={definition.size.minW} max={12} onChange={(w) => setSize({ ...size, w })} />
              <NumberSelect label="Height (rows)" value={size.h} min={definition.size.minH} max={30} onChange={(h) => setSize({ ...size, h })} />
            </div>
            <p className="text-xs text-muted">
              The width is a share of the screen. On narrower screens the widget keeps its share and moves to its own row when it would be too narrow to read.
            </p>
          </fieldset>
        )}
      </div>
    </Dialog>
  );
}

function FieldEditor({ field, value, onChange }: { field: SettingField; value: unknown; onChange: (value: string | boolean | string[]) => void }) {
  const id = useId();
  if (field.kind === 'toggle') {
    return (
      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={value === true} onChange={(e) => onChange(e.target.checked)} className="size-4 accent-[var(--accent)]" />
        {field.label}
      </label>
    );
  }

  if (field.kind === 'select') {
    return (
      <div className="flex flex-col gap-1">
        <label htmlFor={id} className="text-sm font-medium">
          {field.label}
        </label>
        <select
          id={id}
          value={typeof value === 'string' || typeof value === 'number' ? String(value) : ''}
          onChange={(e) => onChange(e.target.value)}
          className="h-9 rounded-md border border-border bg-background px-2 text-sm"
        >
          {field.options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
        {field.help && <p className="text-xs text-muted">{field.help}</p>}
      </div>
    );
  }

  const selected = Array.isArray(value) ? value : [];
  const toggle = (option: string, on: boolean) =>
    onChange(on ? field.options.map((o) => o.value).filter((v) => v === option || selected.includes(v)) : selected.filter((v) => v !== option));
  return (
    <fieldset className="flex flex-col gap-2">
      <legend className="flex w-full items-center justify-between text-sm font-medium">
        {field.label}
        <span className="flex gap-2 text-xs font-normal">
          <button type="button" className="text-accent hover:underline" onClick={() => onChange(field.options.map((o) => o.value))}>
            All
          </button>
          <button type="button" className="text-accent hover:underline" onClick={() => onChange([])}>
            None
          </button>
        </span>
      </legend>
      <div className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
        {field.options.map((o) => (
          <label key={o.value} className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={selected.includes(o.value)} onChange={(e) => toggle(o.value, e.target.checked)} className="size-4 accent-[var(--accent)]" />
            {o.label}
          </label>
        ))}
      </div>
      {field.help && <p className="text-xs text-muted">{field.help}</p>}
    </fieldset>
  );
}

function NumberSelect({ label, value, min, max, onChange }: { label: string; value: number; min: number; max: number; onChange: (value: number) => void }) {
  const id = useId();
  const options = Array.from({ length: Math.max(1, max - min + 1) }, (_, i) => min + i);
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="text-xs text-muted">
        {label}
      </label>
      <select id={id} value={value} onChange={(e) => onChange(Number(e.target.value))} className="h-9 rounded-md border border-border bg-background px-2 text-sm">
        {options.map((n) => (
          <option key={n} value={n}>
            {n}
          </option>
        ))}
      </select>
    </div>
  );
}

export interface AddWidgetDialogProps {
  open: boolean;
  onClose: () => void;
  /** Adds a preconfigured widget. */
  onAdd: (type: WidgetType) => void;
  /** Opens the builder for a new widget of this design. */
  onBuild: (design: CustomDesign) => void;
  /** Widgets the user built and kept. */
  library: WidgetInstance[];
  onAddFromLibrary: (id: string) => void;
  onEditLibrary: (id: string) => void;
  onRemoveFromLibrary: (id: string) => void;
}

/** What each preconfigured widget is about (the catalog groups by it). */
const WIDGET_CATEGORY: Partial<Record<WidgetType, string>> = {
  capture: 'Recorder',
  recorder: 'Recorder',
  gauges: 'Live values',
  chart: 'Live values',
  metric: 'Live values',
  processes: 'Lists',
  incidents: 'Lists',
  events: 'Lists',
  anomalies: 'Lists',
  sensors: 'Hardware',
  gpu: 'Hardware',
  displays: 'Hardware',
  network: 'Hardware',
};

const CATALOG_COLUMNS: Column<WidgetType>[] = [
  { id: 'category', header: 'Area', value: (t) => WIDGET_CATEGORY[t] ?? 'Other' },
  { id: 'name', header: 'Name', value: (t) => WIDGETS[t].name, groupable: false },
];

const LIBRARY_COLUMNS: Column<WidgetInstance>[] = [
  { id: 'name', header: 'Name', value: (w) => w.title || DESIGN_INFO[readCustom(w.settings).design].name, groupable: false },
  { id: 'design', header: 'Design', value: (w) => DESIGN_INFO[readCustom(w.settings).design].name },
  { id: 'values', header: 'Values', value: (w) => readCustom(w.settings).sources.length, firstDesc: true },
];

/** The widget library: preconfigured widgets, and building your own (from a design, or one kept in My widgets). */
export function AddWidgetDialog({ open, onClose, onAdd, onBuild, library, onAddFromLibrary, onEditLibrary, onRemoveFromLibrary }: AddWidgetDialogProps) {
  const [tab, setTab] = useState<'preconfigured' | 'custom'>('preconfigured');
  const [filter, setFilter] = useState('');
  const text = filter.trim().toLowerCase();
  const preconfigured = WIDGET_TYPES.filter((t) => t !== 'custom').filter((t) => !text || `${WIDGETS[t].name} ${WIDGETS[t].description}`.toLowerCase().includes(text));

  return (
    <Dialog open={open} title="Add a widget" description="Widgets are added at the bottom; drag them where you want them." onClose={onClose} className="max-w-3xl">
      <div role="tablist" aria-label="Widgets" className="mb-4 flex gap-1 border-b border-border">
        {(
          [
            ['preconfigured', 'Preconfigured widgets'],
            ['custom', `Build your own${library.length ? ` · My widgets (${library.length})` : ''}`],
          ] as const
        ).map(([id, label]) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={tab === id}
            onClick={() => setTab(id)}
            className={cn('relative -mb-px border-b-2 border-transparent px-3 py-1.5 text-sm transition-colors', tab === id ? 'font-medium' : 'text-muted hover:text-foreground')}
          >
            {tab === id && <TabUnderline group="add-widget-tabs" />}
            {label}
          </button>
        ))}
      </div>

      {tab === 'preconfigured' ? (
        <div className="flex flex-col gap-3">
          <label className="relative block max-w-sm">
            <span className="sr-only">Find a widget</span>
            <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
            <input
              type="search"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              placeholder="Find a widget"
              className="h-8 w-full rounded-md border border-border bg-background pl-7 pr-2 text-sm placeholder:text-muted"
            />
          </label>
          <DataTable
            viewKey="widgets.catalog"
            label="Preconfigured widgets"
            rows={preconfigured}
            columns={CATALOG_COLUMNS}
            rowKey={(type) => type}
            defaultView={{ groupBy: [], sort: [] }}
            compact
            emptyText="Nothing matches."
            listClassName="gap-2"
            scrollClassName="max-h-[28rem]"
            renderRow={(type) => {
              const definition = WIDGETS[type];
              const Icon = definition.icon;
              return (
                <div className="flex items-start gap-3 rounded-md border border-border p-3">
                  <Icon className="mt-0.5 size-5 shrink-0 text-accent" aria-hidden />
                  <div className="min-w-0 flex-1">
                    <p className="text-sm font-medium">{definition.name}</p>
                    <p className="text-xs text-muted">{definition.description}</p>
                  </div>
                  <Button size="sm" variant="outline" aria-label={`Add ${definition.name}`} onClick={() => onAdd(type)}>
                    <Plus aria-hidden /> Add
                  </Button>
                </div>
              );
            }}
          />
        </div>
      ) : (
        <div className="flex flex-col gap-5">
          <section aria-label="Start from a design" className="flex flex-col gap-2">
            <h3 className="text-sm font-medium">Start from a design</h3>
            <p className="text-xs text-muted">Then choose the values it shows (any reading, every hardware sensor), its colors, scale and limits.</p>
            <ul className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-4">
              {DESIGNS.map((design) => {
                const info = DESIGN_INFO[design];
                const Icon = info.icon;
                return (
                  <li key={design}>
                    <button
                      type="button"
                      onClick={() => onBuild(design)}
                      aria-label={`Build a ${info.name} widget`}
                      title={info.description}
                      className="flex h-full w-full flex-col items-start gap-1 rounded-md border border-border p-3 text-left hover:border-accent/60"
                    >
                      <Icon className="size-5 text-accent" aria-hidden />
                      <span className="text-sm font-medium">{info.name}</span>
                      <span className="line-clamp-2 text-xs text-muted">{info.description}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>

          <section aria-label="My widgets" className="flex flex-col gap-2">
            <h3 className="text-sm font-medium">My widgets</h3>
            {library.length === 0 ? (
              <p className="rounded-md border border-dashed border-border p-4 text-sm text-muted">
                Widgets you build are kept here (unless you untick it in the builder), so you can add them again, also after removing them from the dashboard.
              </p>
            ) : (
              <DataTable
                viewKey="widgets.library"
                label="My widgets"
                rows={library}
                columns={LIBRARY_COLUMNS}
                rowKey={(widget) => widget.id}
                defaultView={{ groupBy: [], sort: [{ id: 'name', desc: false }] }}
                compact
                listClassName="gap-2"
                renderRow={(widget) => {
                  const config = readCustom(widget.settings);
                  const info = DESIGN_INFO[config.design];
                  const Icon = info.icon;
                  const name = widget.title || info.name;
                  return (
                    <div className="flex items-start gap-3 rounded-md border border-border p-3">
                      <Icon className="mt-0.5 size-5 shrink-0 text-accent" aria-hidden />
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-sm font-medium">{name}</p>
                        <p className="text-xs text-muted">
                          {info.name}
                          {info.maxSources > 0 ? ` · ${config.sources.length} value${config.sources.length === 1 ? '' : 's'}` : ''}
                        </p>
                      </div>
                      <div className="flex shrink-0 gap-1">
                        <Button size="sm" variant="outline" aria-label={`Add ${name}`} onClick={() => onAddFromLibrary(widget.id)}>
                          <Plus aria-hidden /> Add
                        </Button>
                        <Button size="icon" variant="ghost" aria-label={`Edit ${name}`} title="Edit" onClick={() => onEditLibrary(widget.id)}>
                          <Pencil aria-hidden />
                        </Button>
                        <Button size="icon" variant="ghost" aria-label={`Delete ${name} from My widgets`} title="Delete from My widgets" onClick={() => onRemoveFromLibrary(widget.id)}>
                          <Trash2 aria-hidden />
                        </Button>
                      </div>
                    </div>
                  );
                }}
              />
            )}
          </section>
          <p className="flex items-center gap-1.5 text-xs text-muted">
            <Wand2 className="size-3.5" aria-hidden /> A widget you built is changed from its ⚙ on the dashboard, like any other.
          </p>
        </div>
      )}
    </Dialog>
  );
}
