import { useQuery } from '@tanstack/react-query';
import { ChevronDown, ChevronRight, Search } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import { Dialog } from '@/components/ui/dialog';
import { apiGet } from '@/lib/api/client';
import type { RecordingCatalog, RecordingSource } from '@/lib/api/types';

/** Sources grouped for reading, in this order. */
const GROUPS: { title: string; kinds: string[] }[] = [
  { title: 'CPU and memory', kinds: ['cpu', 'memory'] },
  { title: 'Graphics', kinds: ['gpu'] },
  { title: 'Storage', kinds: ['disk', 'volumes'] },
  { title: 'Network', kinds: ['network'] },
  { title: 'Hardware sensors', kinds: ['sensors'] },
  { title: 'Processes, events and system', kinds: ['processes', 'events', 'display', 'services', 'inventory'] },
];

export interface RecordingPickerProps {
  open: boolean;
  title: string;
  /** Items selected when it opens; null selects everything. */
  initial: string[] | null;
  onClose: () => void;
  /** The chosen items, or null for "record everything". */
  onConfirm: (selection: string[] | null) => void;
  /** e.g. "Start custom recording" or "Use this selection". */
  confirmLabel: string;
  busy?: boolean;
}

/** The recorder's catalog: the sources and items of this PC (its devices and sensors). */
export function useRecordingCatalog(enabled: boolean) {
  return useQuery({
    queryKey: ['recorder', 'catalog'],
    queryFn: ({ signal }) => apiGet<RecordingCatalog>('/recorder/catalog', signal),
    enabled,
    staleTime: 60_000,
  });
}

/**
 * Picks what a custom recording stores: sources (CPU, each GPU, disk and network adapter, hardware-sensor devices,
 * processes, Windows events...) and, within each, its data items; hardware sensors one by one. What is left out is
 * still shown live on the dashboard but is not stored, not kept for incidents and not seen by the automatic rules.
 */
export function RecordingPicker({ open, title, initial, onClose, onConfirm, confirmLabel, busy = false }: RecordingPickerProps) {
  const catalog = useRecordingCatalog(open);
  const sources = catalog.data?.sources ?? [];
  const allItems = useMemo(() => sources.flatMap((s) => s.items.map((i) => i.id)), [sources]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [filter, setFilter] = useState('');
  const seeded = useRef(false);

  // Start from the given selection (everything when none) once the catalog is known; again on each opening.
  useEffect(() => {
    if (!open) {
      seeded.current = false;
      return;
    }
    if (seeded.current || allItems.length === 0) return;
    seeded.current = true;
    setSelected(new Set(initial ?? allItems));
    setExpanded(new Set());
    setFilter('');
  }, [open, allItems, initial]);

  const known = useMemo(() => new Set(allItems), [allItems]);
  const count = [...selected].filter((id) => known.has(id)).length;
  const needle = filter.trim().toLowerCase();
  const visible = (source: RecordingSource) =>
    !needle ? source.items : source.label.toLowerCase().includes(needle) ? source.items : source.items.filter((i) => i.label.toLowerCase().includes(needle));

  const toggle = (ids: string[], on: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      for (const id of ids) {
        if (on) next.add(id);
        else next.delete(id);
      }
      return next;
    });

  const footer = (
    <div className="flex w-full flex-wrap items-center justify-between gap-2">
      <span className="text-sm text-muted">{allItems.length > 0 && (count === allItems.length ? 'Everything selected' : `${count} of ${allItems.length} items selected`)}</span>
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="outline" disabled={busy} onClick={() => onConfirm(null)}>
          Record everything
        </Button>
        <Button type="button" disabled={busy || count === 0} onClick={() => onConfirm([...selected].filter((id) => known.has(id)))}>
          {confirmLabel}
        </Button>
      </div>
    </div>
  );

  return (
    <Dialog
      open={open}
      title={title}
      description="Choose what is stored. Everything is still shown live on the dashboard; what you leave out is not saved, not kept for incidents and not checked by the automatic incident rules."
      onClose={onClose}
      footer={footer}
      className="max-w-3xl"
    >
      {catalog.isLoading && <p className="text-sm text-muted">Reading this PC's devices and sensors…</p>}
      {catalog.isError && <p className="text-sm text-danger">The recorder could not list its sources. Is it running?</p>}
      {sources.length > 0 && (
        <div className="flex flex-col gap-3">
          <div className="flex flex-wrap items-center gap-2">
            <label className="flex min-w-48 flex-1 items-center gap-2 rounded-md border border-border px-2 py-1 text-sm">
              <Search className="size-3.5 text-muted" aria-hidden />
              <input
                type="search"
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder="Find a source or sensor"
                aria-label="Find a source or sensor"
                className="w-full bg-transparent outline-none"
              />
            </label>
            <Button type="button" size="sm" variant="ghost" onClick={() => setSelected(new Set(allItems))}>
              Select all
            </Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setSelected(new Set())}>
              Select none
            </Button>
          </div>
          <div className="flex max-h-[55vh] flex-col gap-4 overflow-y-auto pr-1">
            {GROUPS.map((group) => {
              const members = sources.filter((s) => group.kinds.includes(s.kind) && visible(s).length > 0);
              if (members.length === 0) return null;
              return (
                <section key={group.title} aria-label={group.title} className="flex flex-col gap-1">
                  <h3 className="text-xs font-medium uppercase tracking-wide text-muted">{group.title}</h3>
                  {members.map((source) => {
                    const items = visible(source);
                    const on = items.filter((i) => selected.has(i.id)).length;
                    const unfolded = expanded.has(source.id) || (needle !== '' && !source.label.toLowerCase().includes(needle));
                    return (
                      <div key={source.id} className="rounded-md border border-border">
                        <div className="flex items-center gap-2 px-2 py-1.5">
                          <TriCheckbox
                            label={source.label}
                            checked={on === items.length}
                            indeterminate={on > 0 && on < items.length}
                            onChange={(value) => toggle(items.map((i) => i.id), value)}
                          />
                          <span className="ml-auto text-xs tabular-nums text-muted">
                            {on}/{items.length}
                          </span>
                          {items.length > 1 && (
                            <button
                              type="button"
                              aria-expanded={unfolded}
                              aria-label={`${unfolded ? 'Hide' : 'Show'} the items of ${source.label}`}
                              className="rounded p-0.5 text-muted hover:text-foreground"
                              onClick={() =>
                                setExpanded((current) => {
                                  const next = new Set(current);
                                  if (next.has(source.id)) next.delete(source.id);
                                  else next.add(source.id);
                                  return next;
                                })
                              }
                            >
                              {unfolded ? <ChevronDown className="size-4" aria-hidden /> : <ChevronRight className="size-4" aria-hidden />}
                            </button>
                          )}
                        </div>
                        {unfolded && items.length > 1 && (
                          <ul className="grid gap-x-4 gap-y-1 border-t border-border px-2 py-1.5 sm:grid-cols-2">
                            {items.map((item) => (
                              <li key={item.id}>
                                <label className="flex items-start gap-2 text-sm">
                                  <input
                                    type="checkbox"
                                    checked={selected.has(item.id)}
                                    onChange={(e) => toggle([item.id], e.target.checked)}
                                    className="mt-0.5 size-3.5 accent-[var(--accent)]"
                                  />
                                  <span>
                                    {item.label}
                                    {item.detail && <span className="block text-xs text-muted">{item.detail}</span>}
                                  </span>
                                </label>
                              </li>
                            ))}
                          </ul>
                        )}
                      </div>
                    );
                  })}
                </section>
              );
            })}
          </div>
        </div>
      )}
    </Dialog>
  );
}

function TriCheckbox({ label, checked, indeterminate, onChange }: { label: string; checked: boolean; indeterminate: boolean; onChange: (value: boolean) => void }) {
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = indeterminate;
  }, [indeterminate]);
  return (
    <label className="flex min-w-0 items-center gap-2 text-sm font-medium">
      <input ref={ref} type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="size-4 accent-[var(--accent)]" />
      <span className="truncate">{label}</span>
    </label>
  );
}
