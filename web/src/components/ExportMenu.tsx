import { ChevronDown, Download } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';

export interface ExportItem {
  label: string;
  hint: string;
  /** Query string added to the export address, e.g. "format=pdf". */
  query: string;
}

export const INCIDENT_EXPORTS: readonly ExportItem[] = [
  { label: 'Quick report (HTML)', hint: 'What happened and the diagnostic summary', query: 'format=html' },
  { label: 'Quick report (PDF)', hint: 'Printed by Microsoft Edge', query: 'format=pdf' },
  { label: 'Technical report (HTML)', hint: 'Every chart, process, event, driver and setting', query: 'format=html&report=technical' },
  { label: 'Technical report (PDF)', hint: 'Printed by Microsoft Edge', query: 'format=pdf&report=technical' },
  { label: 'Everything (ZIP)', hint: 'Report, CSV and JSON files', query: 'format=zip' },
  { label: 'Metrics (CSV)', hint: 'Every series, one row per second', query: 'format=csv&file=metrics' },
  { label: 'Processes (CSV)', hint: 'Every process sample', query: 'format=csv&file=processes' },
  { label: 'Events (CSV)', hint: 'The timeline with Windows event details', query: 'format=csv&file=events' },
  { label: 'Services (CSV)', hint: 'Services at the capture and changes', query: 'format=csv&file=services' },
  { label: 'Incident (JSON)', hint: 'Summary, triggers, findings, anomalies and timeline', query: 'format=json' },
];

/** CSV files are streamed as they are written: only the others can be kept. */
const keepable = (item: ExportItem) => !item.query.includes('format=csv');

/**
 * Downloads of a report and its data from an export address (the browser saves them). With `keep`, a copy can also be
 * kept in the storage locations that keep exports (Storage › Where files are kept).
 */
export function ExportMenu({ href, items, disabled, keep = false }: { href: string; items: readonly ExportItem[]; disabled?: boolean; keep?: boolean }) {
  const [open, setOpen] = useState(false);
  const [keepCopy, setKeepCopy] = useState(false);
  const menuId = useId();
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent | KeyboardEvent) => {
      if (e instanceof KeyboardEvent ? e.key === 'Escape' : !ref.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', close);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', close);
    };
  }, [open]);

  return (
    <div ref={ref} className="relative">
      <Button size="sm" variant="outline" aria-haspopup="menu" aria-expanded={open} aria-controls={menuId} disabled={disabled} onClick={() => setOpen((o) => !o)}>
        <Download aria-hidden /> Export <ChevronDown aria-hidden />
      </Button>
      {open && (
        <div id={menuId} role="menu" aria-label="Export" className="absolute right-0 z-30 mt-1 w-72 rounded-md border border-border bg-surface p-1 shadow-lg">
          {items.map((item) => (
            <a
              key={item.query}
              role="menuitem"
              href={`${href}${href.includes('?') ? '&' : '?'}${item.query}${keep && keepCopy && keepable(item) ? '&keep=true' : ''}`}
              download
              onClick={() => setOpen(false)}
              className="flex flex-col rounded px-2.5 py-1.5 hover:bg-surface-muted focus:bg-surface-muted focus:outline-none"
            >
              <span className="text-sm">{item.label}</span>
              <span className="text-xs text-muted">{keep && keepCopy && !keepable(item) ? `${item.hint} (downloaded only)` : item.hint}</span>
            </a>
          ))}
          {keep && (
            <label className="mt-1 flex items-start gap-2 border-t border-border px-2.5 pb-1 pt-2 text-xs">
              <input type="checkbox" role="menuitemcheckbox" aria-checked={keepCopy} checked={keepCopy} onChange={(e) => setKeepCopy(e.target.checked)} className="mt-0.5 size-4 accent-[var(--accent)]" />
              <span>
                Keep a copy too
                <span className="block text-muted">In the storage locations that keep exports (Storage page)</span>
              </span>
            </label>
          )}
        </div>
      )}
    </div>
  );
}
