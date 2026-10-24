/** A value a table can sort and group by. */
export type CellValue = string | number | boolean | null | undefined;

/** One sort key: a column and its direction. */
export interface SortKey {
  id: string;
  desc: boolean;
}

/** How a table is grouped and sorted. */
export interface TableView {
  /** Columns to group by, outermost first. */
  groupBy: string[];
  /** Sort keys, most significant first. */
  sort: SortKey[];
}

/** What grouping and sorting need to know about a column. */
export interface ViewColumn<T> {
  id: string;
  header: string;
  /** The value sorted and grouped by. */
  value: (row: T) => CellValue;
  /** A group's heading for a row (default: the value as text). Rows with the same heading form one group. */
  group?: (row: T) => string;
  groupable?: boolean;
  sortable?: boolean;
  /** Sorted descending when first chosen (figures and times), ascending otherwise (names). */
  firstDesc?: boolean;
}

/** A group of rows, possibly holding subgroups. */
export interface RowGroup<T> {
  /** Path of headings from the outermost group, joined with " › " (stable key for collapsing). */
  key: string;
  column: string;
  label: string;
  depth: number;
  /** Rows in the group (all levels below). */
  count: number;
  groups: RowGroup<T>[] | null;
  rows: T[] | null;
}

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

/** Compares two values: numbers numerically, text naturally ("item 2" before "item 10"), missing values always last. */
export function compareValues(a: CellValue, b: CellValue, desc = false): number {
  const missingA = a === null || a === undefined || a === '' || (typeof a === 'number' && Number.isNaN(a));
  const missingB = b === null || b === undefined || b === '' || (typeof b === 'number' && Number.isNaN(b));
  if (missingA || missingB) return missingA === missingB ? 0 : missingA ? 1 : -1;
  let result: number;
  if (typeof a === 'number' && typeof b === 'number') result = a - b;
  else if (typeof a === 'boolean' && typeof b === 'boolean') result = Number(a) - Number(b);
  else result = collator.compare(String(a), String(b));
  return desc ? -result : result;
}

/** Rows sorted by the keys in order (stable: equal rows keep their order). */
export function sortRows<T>(rows: readonly T[], columns: readonly ViewColumn<T>[], sort: readonly SortKey[]): T[] {
  const keys = sort.map((k) => ({ column: columns.find((c) => c.id === k.id), desc: k.desc })).filter((k) => k.column);
  if (keys.length === 0) return [...rows];
  return rows
    .map((row, index) => ({ row, index }))
    .sort((x, y) => {
      for (const { column, desc } of keys) {
        const result = compareValues(column!.value(x.row), column!.value(y.row), desc);
        if (result !== 0) return result;
      }
      return x.index - y.index;
    })
    .map((x) => x.row);
}

/** The heading of a row's group for a column. */
export function groupLabel<T>(column: ViewColumn<T>, row: T): string {
  if (column.group) return column.group(row);
  const value = column.value(row);
  if (value === null || value === undefined || value === '') return '(none)';
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  return String(value);
}

/**
 * Rows grouped by the columns in order, each level ordered by its column (the direction of that column's sort key when
 * it has one, ascending otherwise), with groups of no value last; rows within a group keep the sorted order.
 */
export function groupRows<T>(rows: readonly T[], columns: readonly ViewColumn<T>[], view: TableView): RowGroup<T>[] | null {
  const levels = view.groupBy.map((id) => columns.find((c) => c.id === id)).filter((c): c is ViewColumn<T> => c !== undefined);
  if (levels.length === 0) return null;
  const sorted = sortRows(rows, columns, view.sort);

  const build = (items: T[], depth: number, parent: string): RowGroup<T>[] => {
    const column = levels[depth]!;
    const byLabel = new Map<string, { label: string; first: T; rows: T[] }>();
    for (const row of items) {
      const label = groupLabel(column, row);
      const group = byLabel.get(label);
      if (group) group.rows.push(row);
      else byLabel.set(label, { label, first: row, rows: [row] });
    }

    const desc = view.sort.find((k) => k.id === column.id)?.desc ?? false;
    const groups = [...byLabel.values()].sort((a, b) => {
      const result = compareValues(column.value(a.first), column.value(b.first), desc);
      return result !== 0 ? result : collator.compare(a.label, b.label);
    });
    return groups.map((g) => {
      const key = parent ? `${parent} › ${g.label}` : g.label;
      const nested = depth + 1 < levels.length;
      return {
        key: `${column.id}:${key}`,
        column: column.id,
        label: g.label,
        depth,
        count: g.rows.length,
        groups: nested ? build(g.rows, depth + 1, key) : null,
        rows: nested ? null : g.rows,
      };
    });
  };

  return build(sorted, 0, '');
}

/** A view with unknown columns and duplicates removed (e.g. one remembered from an older version of the table). */
export function cleanView<T>(view: TableView | null | undefined, columns: readonly ViewColumn<T>[], fallback: TableView): TableView {
  if (!view || !Array.isArray(view.groupBy) || !Array.isArray(view.sort)) return fallback;
  const groupable = new Set(columns.filter((c) => c.groupable !== false).map((c) => c.id));
  const sortable = new Set(columns.filter((c) => c.sortable !== false).map((c) => c.id));
  const groupBy = [...new Set(view.groupBy.filter((id) => typeof id === 'string' && groupable.has(id)))];
  const seen = new Set<string>();
  const sort = view.sort.filter((k) => k && typeof k.id === 'string' && sortable.has(k.id) && !seen.has(k.id) && (seen.add(k.id), true)).map((k) => ({ id: k.id, desc: !!k.desc }));
  return { groupBy, sort };
}

/** Moves an item to a position in a list (the position counted before the move). */
export function moveItem<T>(list: readonly T[], from: number, to: number): T[] {
  const next = [...list];
  const [item] = next.splice(from, 1);
  next.splice(to > from ? to - 1 : to, 0, item!);
  return next;
}

/** Whether two views are the same. */
export function sameView(a: TableView, b: TableView): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

const UNITS: Record<string, number> = { b: 1, kb: 1024, mb: 1024 ** 2, gb: 1024 ** 3, tb: 1024 ** 4, ms: 0.001, s: 1, min: 60, h: 3600, d: 86_400 };
const FIGURE = /([+\-−]?\d[\d,]*(?:\.\d+)?)\s*([a-zA-Z%°]+)?(?:\/s)?/g;

/**
 * The number in a figure written as text, for sorting: "38.9 GB" and "141 MB/s" in bytes, "2 h 5 min" in seconds,
 * "8.8 %", "1,234", "×5". Text that is not only figures (names, dates) is returned as it is; "—" and empty as missing.
 */
export function measureValue(text: string | null | undefined): CellValue {
  if (text == null) return null;
  const trimmed = text.trim();
  if (trimmed === '' || trimmed === '—') return null;
  const parts = [...trimmed.matchAll(FIGURE)];
  const rest = trimmed.replace(FIGURE, '').replace(/[\s×]/g, '');
  if (parts.length === 0 || rest.length > 0) return trimmed;
  return parts.reduce((total, [, number, unit]) => total + Number(number!.replace(/,/g, '').replace('−', '-')) * (unit ? (UNITS[unit.toLowerCase()] ?? 1) : 1), 0);
}
