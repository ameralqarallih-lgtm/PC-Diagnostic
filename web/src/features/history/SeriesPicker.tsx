import { ChevronDown, ChevronRight, Search } from 'lucide-react';
import { useMemo, useState } from 'react';
import type { SeriesInfo } from '@/lib/api/types';
import { cn } from '@/lib/utils';
import { MAX_SERIES } from './ranges';

export { MAX_SERIES };

export interface SeriesPreset {
  name: string;
  /** Series ids, or patterns with "*" for any device id. */
  ids: string[];
}

export const SERIES_PRESETS: SeriesPreset[] = [
  { name: 'Load', ids: ['cpu', 'memory', 'gpu', 'vram', 'disk'] },
  { name: 'Thermals', ids: ['cpu.temp', 'gpu.*.temp', 'gpu.*.hotspot', 'cpu.power', 'gpu.*.power'] },
  { name: 'GPU', ids: ['gpu.*.util', 'gpu.*.vram', 'gpu.*.core', 'gpu.*.hotspot', 'gpu.*.power'] },
  { name: 'Memory', ids: ['memory', 'memory.commit', 'memory.hardfaults', 'memory.compressed'] },
  { name: 'Disk', ids: ['disk', 'disk.*.read', 'disk.*.write', 'disk.*.readlat', 'disk.*.writelat'] },
  { name: 'Network', ids: ['net', 'net.*.rx', 'net.*.tx', 'net.*.wifi'] },
];

/** The catalog ids a preset stands for. */
export function expandPreset(preset: SeriesPreset, catalog: SeriesInfo[]): string[] {
  const ids = preset.ids.flatMap((pattern) => {
    if (!pattern.includes('*')) return catalog.some((s) => s.id === pattern) ? [pattern] : [];
    const [prefix, suffix] = pattern.split('*') as [string, string];
    return catalog.filter((s) => s.id.startsWith(prefix) && s.id.endsWith(suffix)).map((s) => s.id);
  });
  return [...new Set(ids)].slice(0, MAX_SERIES);
}

export interface SeriesPickerProps {
  catalog: SeriesInfo[];
  selected: string[];
  onChange: (ids: string[]) => void;
}

/**
 * Choose what to overlay: quick sets, or any series by group, with a filter. Groups start folded (with how many of
 * theirs are chosen) and open while filtering; everything shown, or a whole group, can be chosen or cleared at once.
 */
export function SeriesPicker({ catalog, selected, onChange }: SeriesPickerProps) {
  const [filter, setFilter] = useState('');
  const [open, setOpen] = useState<Set<string>>(() => new Set());
  const text = filter.trim().toLowerCase();
  const groups = useMemo(() => {
    const visible = text ? catalog.filter((s) => s.label.toLowerCase().includes(text) || s.id.includes(text) || s.group.toLowerCase().includes(text)) : catalog;
    const map = new Map<string, SeriesInfo[]>();
    for (const s of visible) map.set(s.group, [...(map.get(s.group) ?? []), s]);
    return [...map.entries()];
  }, [catalog, text]);
  const full = selected.length >= MAX_SERIES;

  const toggle = (id: string, on: boolean) => onChange(on ? [...selected, id].slice(0, MAX_SERIES) : selected.filter((s) => s !== id));
  const add = (ids: string[]) => onChange([...new Set([...selected, ...ids])].slice(0, MAX_SERIES));
  const remove = (ids: string[]) => onChange(selected.filter((s) => !ids.includes(s)));
  const shownIds = groups.flatMap(([, items]) => items.map((s) => s.id));
  const isOpen = (group: string) => text !== '' || open.has(group);

  return (
    <div className="flex min-h-0 flex-col gap-3">
      <div className="flex flex-wrap gap-1.5" role="group" aria-label="Quick sets">
        {SERIES_PRESETS.map((preset) => (
          <button
            key={preset.name}
            type="button"
            onClick={() => onChange(expandPreset(preset, catalog))}
            className="rounded-full border border-border px-2.5 py-1 text-xs hover:border-accent hover:text-accent"
          >
            {preset.name}
          </button>
        ))}
      </div>
      <label className="relative">
        <span className="sr-only">Filter series</span>
        <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted" aria-hidden />
        <input
          type="search"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder="Filter, e.g. temperature, fan, RTX, C:"
          className="h-8 w-full rounded-md border border-border bg-background pl-7 pr-2 text-xs placeholder:text-muted"
        />
      </label>
      <div className="flex flex-wrap items-center justify-between gap-2 text-xs">
        <p className="text-muted" aria-live="polite">
          {selected.length} of {MAX_SERIES} selected{full ? ' (the most)' : ''}
        </p>
        <span className="flex gap-2">
          <button type="button" className="text-accent hover:underline disabled:opacity-40" disabled={full || shownIds.every((id) => selected.includes(id))} onClick={() => add(shownIds)}>
            {text ? 'Select shown' : 'Select all'}
          </button>
          <button type="button" className="text-accent hover:underline disabled:opacity-40" disabled={selected.length === 0} onClick={() => (text ? remove(shownIds) : onChange([]))}>
            Select none
          </button>
          <button type="button" className="text-muted hover:text-foreground" onClick={() => setOpen(open.size > 0 ? new Set() : new Set(groups.map(([g]) => g)))}>
            {open.size > 0 ? 'Fold all' : 'Unfold all'}
          </button>
        </span>
      </div>
      <div className="flex min-h-0 flex-1 flex-col gap-1 overflow-y-auto pr-1">
        {groups.map(([group, items]) => {
          const chosen = items.filter((s) => selected.includes(s.id)).length;
          const unfolded = isOpen(group);
          return (
            <section key={group} aria-label={group} className="rounded-md border border-border/60">
              <div className="flex items-center gap-1 px-1.5 py-1">
                <button
                  type="button"
                  aria-expanded={unfolded}
                  onClick={() => setOpen((o) => new Set(o.has(group) ? [...o].filter((g) => g !== group) : [...o, group]))}
                  className="flex min-w-0 flex-1 items-center gap-1 text-left text-xs font-semibold uppercase tracking-wide text-muted hover:text-foreground"
                >
                  {unfolded ? <ChevronDown className="size-3.5 shrink-0" aria-hidden /> : <ChevronRight className="size-3.5 shrink-0" aria-hidden />}
                  <span className="truncate">{group}</span>
                  <span className={cn('ml-1 shrink-0 font-normal normal-case tracking-normal', chosen > 0 ? 'text-accent' : '')}>
                    {chosen > 0 ? `${chosen}/${items.length}` : items.length}
                  </span>
                </button>
                <button
                  type="button"
                  aria-label={`Select all in ${group}`}
                  title="Select all in this group"
                  disabled={full || chosen === items.length}
                  onClick={() => add(items.map((s) => s.id))}
                  className="rounded px-1 text-[11px] text-accent hover:underline disabled:opacity-30"
                >
                  All
                </button>
                <button
                  type="button"
                  aria-label={`Select none in ${group}`}
                  title="Clear this group"
                  disabled={chosen === 0}
                  onClick={() => remove(items.map((s) => s.id))}
                  className="rounded px-1 text-[11px] text-accent hover:underline disabled:opacity-30"
                >
                  None
                </button>
              </div>
              {unfolded && (
                <ul className="flex flex-col gap-0.5 px-1 pb-1">
                  {items.map((s) => {
                    const checked = selected.includes(s.id);
                    return (
                      <li key={s.id}>
                        <label className={cn('flex items-center gap-2 rounded px-1 py-0.5 text-sm hover:bg-surface-muted', !checked && full && 'opacity-50')}>
                          <input
                            type="checkbox"
                            checked={checked}
                            disabled={!checked && full}
                            onChange={(e) => toggle(s.id, e.target.checked)}
                            className="size-3.5 accent-[var(--accent)]"
                          />
                          <span className="min-w-0 flex-1 truncate" title={s.label}>
                            {s.label}
                          </span>
                          <span className="text-xs text-muted">{s.unit}</span>
                        </label>
                      </li>
                    );
                  })}
                </ul>
              )}
            </section>
          );
        })}
        {groups.length === 0 && <p className="text-sm text-muted">Nothing matches.</p>}
      </div>
    </div>
  );
}
