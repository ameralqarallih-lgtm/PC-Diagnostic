/**
 * Undo and redo of an editor's value: the values before each change (past) and after each undo (future). Changes
 * made in quick succession (a drag, a slider, typing) are one step: a change within `merge` ms of the last one joins
 * it. At most `limit` steps are kept.
 */
export interface UndoStack<T> {
  past: readonly T[];
  future: readonly T[];
  /** When the last change was recorded (ms), for joining quick ones. */
  lastAt: number;
}

export const emptyUndo = <T>(): UndoStack<T> => ({ past: [], future: [], lastAt: Number.NEGATIVE_INFINITY });

/** A change made: `before` is the value it replaced. */
export function recordChange<T>(stack: UndoStack<T>, before: T, now: number, merge = 600, limit = 100): UndoStack<T> {
  const joins = stack.past.length > 0 && stack.future.length === 0 && now - stack.lastAt < merge;
  return { past: joins ? stack.past : [...stack.past, before].slice(-limit), future: [], lastAt: now };
}

/** The value before the last change, or null when there is nothing to undo. */
export function undoStep<T>(stack: UndoStack<T>, current: T): { stack: UndoStack<T>; value: T } | null {
  const value = stack.past.at(-1);
  if (value === undefined) return null;
  return { stack: { past: stack.past.slice(0, -1), future: [current, ...stack.future], lastAt: Number.NEGATIVE_INFINITY }, value };
}

/** The value the last undo took back, or null when there is nothing to redo. */
export function redoStep<T>(stack: UndoStack<T>, current: T): { stack: UndoStack<T>; value: T } | null {
  const value = stack.future[0];
  if (value === undefined) return null;
  return { stack: { past: [...stack.past, current], future: stack.future.slice(1), lastAt: Number.NEGATIVE_INFINITY }, value };
}

/** Whether a key press is the editor's undo (Ctrl+Z) or redo (Ctrl+Y, Ctrl+Shift+Z), outside text fields (they undo their own typing). */
export function undoKey(e: Pick<KeyboardEvent, 'ctrlKey' | 'metaKey' | 'shiftKey' | 'key' | 'target'>): 'undo' | 'redo' | null {
  if (!(e.ctrlKey || e.metaKey)) return null;
  const t = e.target as HTMLElement | null;
  if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName ?? '')) && (t as HTMLInputElement).type !== 'range' && (t as HTMLInputElement).type !== 'checkbox') return null;
  const key = e.key.toLowerCase();
  if (key === 'z') return e.shiftKey ? 'redo' : 'undo';
  if (key === 'y') return 'redo';
  return null;
}
