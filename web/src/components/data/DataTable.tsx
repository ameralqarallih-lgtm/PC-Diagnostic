import { ArrowDown, ArrowUp, ChevronDown, ChevronRight, GripVertical, Layers, ListOrdered, RotateCcw, X } from 'lucide-react';
import { Fragment, useMemo, useRef, useState, type DragEvent, type KeyboardEvent, type ReactNode } from 'react';
import { useFillHeight } from '@/lib/useFillHeight';
import { cn } from '@/lib/utils';
import { cleanView, groupRows, moveItem, sameView, sortRows, type RowGroup, type SortKey, type TableView, type ViewColumn } from './view';

export type { SortKey, TableView } from './view';

/** A column: how to sort and group by it, and how to show it. */
export interface Column<T> extends ViewColumn<T> {
  /** The cell (default: the value as text). */
  cell?: (row: T) => ReactNode;
  align?: 'left' | 'right';
  className?: string;
  headerClassName?: string;
  /** Header tooltip. */
  title?: string;
  /** Offered for grouping and sorting but not shown as a column. */
  hidden?: boolean;
}

export interface DataTableProps<T> {
  /** Remembers how the user grouped and sorted this table, in this browser. */
  viewKey: string;
  /** Accessible name of the table or list. */
  label: string;
  rows: readonly T[];
  columns: readonly Column<T>[];
  rowKey: (row: T) => string | number;
  /** The grouping and sorting it opens with. */
  defaultView: TableView;
  /** List mode: each row rendered freely (cards, timelines) instead of table cells. */
  renderRow?: (row: T) => ReactNode;
  rowClassName?: (row: T) => string | undefined;
  rowTitle?: (row: T) => string | undefined;
  emptyText?: ReactNode;
  /** A one-line summary that opens the grouping and sorting bars (small widgets). */
  compact?: boolean;
  /** Placed at the start of the toolbar (e.g. a filter). */
  toolbarStart?: ReactNode;
  className?: string;
  /** The scrolling area's class. */
  scrollClassName?: string;
  /** The rows reach down to the bottom of the window and scroll there (a page's main list), instead of a fixed height. */
  fill?: boolean;
  /** List mode: the list's class. */
  listClassName?: string;
}

type Zone = 'group' | 'sort';
type Dragged = { from: 'column' | Zone; id: string } | null;

const STORAGE_PREFIX = 'pcdiag.view.';

/** The view remembered for a table, or its default. */
function useStoredView<T>(key: string, columns: readonly ViewColumn<T>[], fallback: TableView): [TableView, (view: TableView) => void] {
  const [view, setView] = useState<TableView>(() => {
    try {
      const raw = window.localStorage.getItem(STORAGE_PREFIX + key);
      return raw ? cleanView(JSON.parse(raw) as TableView, columns, fallback) : fallback;
    } catch {
      return fallback;
    }
  });
  const save = (next: TableView) => {
    setView(next);
    try {
      if (sameView(next, fallback)) window.localStorage.removeItem(STORAGE_PREFIX + key);
      else window.localStorage.setItem(STORAGE_PREFIX + key, JSON.stringify(next));
    } catch {
      // Not remembered (private window, storage blocked); the view still applies now.
    }
  };
  return [view, save];
}

/**
 * A table or list the user groups and sorts: drag a column header (or, in list mode, a field) onto "Group by" or
 * "Sort by"; drag the chips there to change their order; click a header to sort by it (Shift+click adds it as a
 * further sort key) and a sort chip to reverse it. The same is available without a mouse: the "+" menus add, the
 * arrow keys move a focused chip, × removes. Groups nest and fold. Each table opens with its own default grouping and
 * sorting and remembers the user's in this browser; Reset returns to the default.
 */
export function DataTable<T>(props: DataTableProps<T>) {
  const { viewKey, label, rows, columns, rowKey, defaultView, renderRow, rowClassName, rowTitle, emptyText, compact, toolbarStart, className, scrollClassName, listClassName, fill } = props;
  const [fillRef, fillHeight] = useFillHeight<HTMLDivElement>();
  const [view, setView] = useStoredView(viewKey, columns, defaultView);
  const [open, setOpen] = useState(!compact);
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());
  const [dragging, setDragging] = useState(false);
  const dragged = useRef<Dragged>(null);

  const visible = columns.filter((c) => !c.hidden);
  const byId = useMemo(() => new Map(columns.map((c) => [c.id, c])), [columns]);
  const groups = useMemo(() => groupRows(rows, columns, view), [rows, columns, view]);
  const sorted = useMemo(() => (groups ? null : sortRows(rows, columns, view.sort)), [groups, rows, columns, view.sort]);

  const headerOf = (id: string) => byId.get(id)?.header ?? id;
  const groupable = columns.filter((c) => c.groupable !== false && !view.groupBy.includes(c.id));
  const sortable = columns.filter((c) => c.sortable !== false && !view.sort.some((k) => k.id === c.id));

  const addGroup = (id: string, at = view.groupBy.length) => {
    if (byId.get(id)?.groupable === false) return;
    const without = view.groupBy.filter((g) => g !== id);
    const index = Math.min(at, without.length);
    setView({ ...view, groupBy: [...without.slice(0, index), id, ...without.slice(index)] });
  };
  const addSort = (id: string, at = view.sort.length) => {
    const column = byId.get(id);
    if (!column || column.sortable === false) return;
    const existing = view.sort.find((k) => k.id === id);
    const without = view.sort.filter((k) => k.id !== id);
    const index = Math.min(at, without.length);
    const key: SortKey = existing ?? { id, desc: column.firstDesc ?? false };
    setView({ ...view, sort: [...without.slice(0, index), key, ...without.slice(index)] });
  };

  /** Header click: sort by this column alone (again: reverse); with Shift, add it to the sort keys (again: reverse it). */
  const sortBy = (id: string, add: boolean) => {
    const column = byId.get(id);
    if (!column || column.sortable === false) return;
    const existing = view.sort.find((k) => k.id === id);
    if (add) {
      setView({ ...view, sort: existing ? view.sort.map((k) => (k.id === id ? { ...k, desc: !k.desc } : k)) : [...view.sort, { id, desc: column.firstDesc ?? false }] });
    } else {
      const primary = view.sort[0]?.id === id;
      setView({ ...view, sort: [{ id, desc: primary && existing ? !existing.desc : (column.firstDesc ?? false) }] });
    }
  };

  const startDrag = (event: DragEvent, item: NonNullable<Dragged>) => {
    dragged.current = item;
    setDragging(true);
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('text/plain', item.id);
  };
  const endDrag = () => {
    dragged.current = null;
    setDragging(false);
  };
  const dropOn = (event: DragEvent, zone: Zone, index?: number) => {
    event.preventDefault();
    event.stopPropagation();
    const item = dragged.current;
    endDrag();
    if (!item) return;
    if (zone === 'group') {
      if (item.from === 'group') {
        const from = view.groupBy.indexOf(item.id);
        setView({ ...view, groupBy: moveItem(view.groupBy, from, index ?? view.groupBy.length) });
      } else addGroup(item.id, index);
    } else if (item.from === 'sort') {
      const from = view.sort.findIndex((k) => k.id === item.id);
      setView({ ...view, sort: moveItem(view.sort, from, index ?? view.sort.length) });
    } else addSort(item.id, index);
  };
  const allowDrop = (event: DragEvent) => {
    if (dragged.current) {
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
    }
  };

  const chipKeys = (event: KeyboardEvent, zone: Zone, index: number) => {
    const length = zone === 'group' ? view.groupBy.length : view.sort.length;
    const to = event.key === 'ArrowLeft' ? index - 1 : event.key === 'ArrowRight' ? index + 2 : null;
    if (to === null || to < 0 || to > length) return;
    event.preventDefault();
    if (zone === 'group') setView({ ...view, groupBy: moveItem(view.groupBy, index, to) });
    else setView({ ...view, sort: moveItem(view.sort, index, to) });
  };

  const toggle = (key: string) =>
    setCollapsed((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const summary = [
    view.groupBy.length > 0 ? `Grouped by ${view.groupBy.map(headerOf).join(' › ')}` : null,
    view.sort.length > 0 ? `sorted by ${view.sort.map((k) => `${headerOf(k.id)} ${k.desc ? '↓' : '↑'}`).join(', ')}` : null,
  ]
    .filter(Boolean)
    .join(', ');

  const zoneClass = cn(
    'flex min-h-8 flex-1 flex-wrap items-center gap-1.5 rounded-md border border-dashed px-2 py-1 text-xs transition-colors',
    dragging ? 'border-accent bg-accent/5' : 'border-border',
  );

  const bars = (
    <div className="flex flex-col gap-1.5" data-testid="table-view-bars">
      <div className="flex flex-wrap items-stretch gap-1.5">
        <div className={zoneClass} onDragOver={allowDrop} onDrop={(e) => dropOn(e, 'group')} role="group" aria-label="Group by">
          <Layers className="size-3.5 text-muted" aria-hidden />
          <span className="text-muted">Group by</span>
          {view.groupBy.length === 0 && <span className="text-muted/80 italic">drag a {renderRow ? 'field' : 'column'} here</span>}
          {view.groupBy.map((id, i) => (
            <Chip
              key={id}
              label={headerOf(id)}
              onRemove={() => setView({ ...view, groupBy: view.groupBy.filter((g) => g !== id) })}
              onDragStart={(e) => startDrag(e, { from: 'group', id })}
              onDragEnd={endDrag}
              onDrop={(e) => dropOn(e, 'group', i)}
              onDragOver={allowDrop}
              onKeyDown={(e) => chipKeys(e, 'group', i)}
              separator={i > 0 ? '›' : undefined}
            />
          ))}
          {groupable.length > 0 && <AddMenu label="Add a grouping" options={groupable} onPick={(id) => addGroup(id)} />}
        </div>
        <div className={zoneClass} onDragOver={allowDrop} onDrop={(e) => dropOn(e, 'sort')} role="group" aria-label="Sort by">
          <ListOrdered className="size-3.5 text-muted" aria-hidden />
          <span className="text-muted">Sort by</span>
          {view.sort.length === 0 && <span className="text-muted/80 italic">drag a {renderRow ? 'field' : 'column'} here</span>}
          {view.sort.map((key, i) => (
            <Chip
              key={key.id}
              label={headerOf(key.id)}
              direction={key.desc ? 'desc' : 'asc'}
              onClick={() => setView({ ...view, sort: view.sort.map((k) => (k.id === key.id ? { ...k, desc: !k.desc } : k)) })}
              onRemove={() => setView({ ...view, sort: view.sort.filter((k) => k.id !== key.id) })}
              onDragStart={(e) => startDrag(e, { from: 'sort', id: key.id })}
              onDragEnd={endDrag}
              onDrop={(e) => dropOn(e, 'sort', i)}
              onDragOver={allowDrop}
              onKeyDown={(e) => chipKeys(e, 'sort', i)}
              separator={i > 0 ? ',' : undefined}
            />
          ))}
          {sortable.length > 0 && <AddMenu label="Add a sort key" options={sortable} onPick={(id) => addSort(id)} />}
        </div>
        {!sameView(view, defaultView) && (
          <button
            type="button"
            onClick={() => setView(defaultView)}
            className="inline-flex items-center gap-1 rounded-md px-2 text-xs text-muted hover:bg-surface-muted hover:text-foreground"
            title="Back to this table's default grouping and sorting"
          >
            <RotateCcw className="size-3.5" aria-hidden /> Reset
          </button>
        )}
      </div>
      {renderRow && (
        <div className="flex flex-wrap items-center gap-1.5 text-xs" aria-label="Fields to group or sort by">
          <span className="text-muted">Fields:</span>
          {columns
            .filter((c) => c.groupable !== false || c.sortable !== false)
            .map((c) => (
              <span
                key={c.id}
                draggable
                onDragStart={(e) => startDrag(e, { from: 'column', id: c.id })}
                onDragEnd={endDrag}
                className="inline-flex cursor-grab items-center gap-0.5 rounded-full border border-border px-2 py-0.5 text-muted active:cursor-grabbing"
              >
                <GripVertical className="size-3" aria-hidden />
                {c.header}
              </span>
            ))}
        </div>
      )}
    </div>
  );

  const count = rows.length;
  const empty = count === 0 ? <div className="py-4 text-center text-sm text-muted">{emptyText ?? 'Nothing to show.'}</div> : null;

  const renderGroups = (list: RowGroup<T>[]): ReactNode =>
    list.map((g) => {
      const folded = collapsed.has(g.key);
      const heading = (
        <button
          type="button"
          onClick={() => toggle(g.key)}
          aria-expanded={!folded}
          className="flex w-full items-center gap-1.5 py-1 text-left text-xs font-medium hover:text-foreground"
          style={{ paddingLeft: `${g.depth * 1}rem` }}
        >
          {folded ? <ChevronRight className="size-3.5" aria-hidden /> : <ChevronDown className="size-3.5" aria-hidden />}
          <span className="text-muted">{headerOf(g.column)}:</span>
          <span className="truncate">{g.label}</span>
          <span className="rounded-full bg-surface-muted px-1.5 text-[11px] text-muted">{g.count}</span>
        </button>
      );
      const inner = folded ? null : g.groups ? renderGroups(g.groups) : renderRows(g.rows!, g.depth + 1);
      return renderRow ? (
        <li key={g.key} className="list-none">
          {heading}
          {inner && <ul className="flex flex-col">{inner}</ul>}
        </li>
      ) : (
        <Fragment key={g.key}>
          <tr className="border-t border-border bg-surface-muted/40">
            <td colSpan={visible.length}>{heading}</td>
          </tr>
          {inner}
        </Fragment>
      );
    });

  const renderRows = (list: readonly T[], depth = 0): ReactNode =>
    list.map((row) =>
      renderRow ? (
        <li key={rowKey(row)} className={cn('list-none', rowClassName?.(row))} title={rowTitle?.(row)} style={depth ? { paddingLeft: `${depth * 1}rem` } : undefined}>
          {renderRow(row)}
        </li>
      ) : (
        <tr key={rowKey(row)} className={cn('border-t border-border', rowClassName?.(row))} title={rowTitle?.(row)}>
          {visible.map((c, i) => (
            <td
              key={c.id}
              className={cn('py-1 pr-3 align-top last:pr-0', c.align === 'right' && 'text-right tabular-nums', c.className)}
              style={i === 0 && depth ? { paddingLeft: `${depth * 1}rem` } : undefined}
            >
              {c.cell ? c.cell(row) : String(c.value(row) ?? '—')}
            </td>
          ))}
        </tr>
      ),
    );

  return (
    <div className={cn('flex min-h-0 flex-col gap-2', className)}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        {toolbarStart ?? <span />}
        {compact && (
          <button
            type="button"
            onClick={() => setOpen((o) => !o)}
            aria-expanded={open}
            className="inline-flex max-w-full items-center gap-1 truncate rounded-md px-1.5 py-0.5 text-xs text-muted hover:bg-surface-muted hover:text-foreground"
            title="Group and sort"
          >
            <Layers className="size-3.5 shrink-0" aria-hidden />
            <span className="truncate">{summary || 'Group and sort'}</span>
          </button>
        )}
      </div>
      {open && bars}
      <div ref={fill ? fillRef : undefined} style={fill && fillHeight ? { maxHeight: fillHeight } : undefined} className={cn('min-h-0 overflow-auto', scrollClassName)}>
        {renderRow ? (
          empty ?? (
            <ul aria-label={label} className={cn('flex flex-col', listClassName)}>
              {groups ? renderGroups(groups) : renderRows(sorted!)}
            </ul>
          )
        ) : (
          <>
            <table aria-label={label} className="w-full text-left text-sm">
              <thead className="sticky top-0 z-[1] bg-background text-xs text-muted">
                <tr>
                  {visible.map((c) => {
                    const index = view.sort.findIndex((k) => k.id === c.id);
                    const key = index >= 0 ? view.sort[index] : null;
                    return (
                      <th
                        key={c.id}
                        scope="col"
                        draggable={c.groupable !== false || c.sortable !== false}
                        onDragStart={(e) => startDrag(e, { from: 'column', id: c.id })}
                        onDragEnd={endDrag}
                        aria-sort={index === 0 && key ? (key.desc ? 'descending' : 'ascending') : undefined}
                        className={cn('py-1 pr-3 font-medium last:pr-0', c.align === 'right' && 'text-right', c.headerClassName)}
                        title={c.title}
                      >
                        {c.sortable === false ? (
                          c.header
                        ) : (
                          <button
                            type="button"
                            onClick={(e) => sortBy(c.id, e.shiftKey)}
                            className={cn('inline-flex cursor-grab items-center gap-0.5 hover:text-foreground', key && 'text-foreground')}
                            title={`${c.title ? c.title + '\n' : ''}Click to sort, Shift+click to add a sort key; drag to group or sort`}
                          >
                            {c.header}
                            {key && (key.desc ? <ArrowDown className="size-3" aria-hidden /> : <ArrowUp className="size-3" aria-hidden />)}
                            {key && view.sort.length > 1 && <sup className="text-[9px]">{index + 1}</sup>}
                          </button>
                        )}
                      </th>
                    );
                  })}
                </tr>
              </thead>
              <tbody>{groups ? renderGroups(groups) : renderRows(sorted!)}</tbody>
            </table>
            {empty}
          </>
        )}
      </div>
    </div>
  );
}

interface ChipProps {
  label: string;
  direction?: 'asc' | 'desc';
  separator?: string;
  onClick?: () => void;
  onRemove: () => void;
  onDragStart: (event: DragEvent) => void;
  onDragEnd: () => void;
  onDrop: (event: DragEvent) => void;
  onDragOver: (event: DragEvent) => void;
  onKeyDown: (event: KeyboardEvent) => void;
}

function Chip({ label, direction, separator, onClick, onRemove, onDragStart, onDragEnd, onDrop, onDragOver, onKeyDown }: ChipProps) {
  return (
    <>
      {separator && <span className="text-muted">{separator}</span>}
      <span
        draggable
        onDragStart={onDragStart}
        onDragEnd={onDragEnd}
        onDrop={onDrop}
        onDragOver={onDragOver}
        className="inline-flex cursor-grab items-center gap-0.5 rounded-full border border-accent/40 bg-accent/10 py-0.5 pl-1.5 pr-0.5 text-foreground active:cursor-grabbing"
      >
        <GripVertical className="size-3 text-muted" aria-hidden />
        <button
          type="button"
          onClick={onClick}
          onKeyDown={onKeyDown}
          className="inline-flex items-center gap-0.5"
          title={direction ? 'Click to reverse; drag or use ← → to change the order' : 'Drag or use ← → to change the order'}
          aria-label={direction ? `${label}, ${direction === 'desc' ? 'descending' : 'ascending'}` : label}
        >
          {label}
          {direction === 'desc' && <ArrowDown className="size-3" aria-hidden />}
          {direction === 'asc' && <ArrowUp className="size-3" aria-hidden />}
        </button>
        <button type="button" onClick={onRemove} aria-label={`Remove ${label}`} className="rounded-full p-0.5 text-muted hover:bg-surface-muted hover:text-foreground">
          <X className="size-3" aria-hidden />
        </button>
      </span>
    </>
  );
}

function AddMenu<T>({ label, options, onPick }: { label: string; options: readonly ViewColumn<T>[]; onPick: (id: string) => void }) {
  return (
    <select
      aria-label={label}
      value=""
      onChange={(e) => {
        if (e.target.value) onPick(e.target.value);
      }}
      className="h-6 rounded-md border border-border bg-background px-1 text-xs text-muted"
    >
      <option value="">+</option>
      {options.map((c) => (
        <option key={c.id} value={c.id}>
          {c.header}
        </option>
      ))}
    </select>
  );
}
