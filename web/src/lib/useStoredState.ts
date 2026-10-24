import { useCallback, useState } from 'react';

/** Reads a stored value; null when storage is unavailable (private windows, blocked site data) or the value is bad. */
export function readStored<T>(key: string, validate: (value: unknown) => value is T): T | null {
  try {
    const raw = window.localStorage.getItem(key);
    if (raw === null) return null;
    const value: unknown = JSON.parse(raw);
    return validate(value) ? value : null;
  } catch {
    return null;
  }
}

/** Stores a value; failures are ignored (the page works the same without storage). */
export function writeStored(key: string, value: unknown): void {
  try {
    window.localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage full or blocked: this viewer just loses the convenience.
  }
}

/**
 * State that survives a page reload in this browser (a per-viewer convenience such as a chart's window or zoom;
 * never shared or relied on). Falls back to <paramref name="initial"/> when nothing valid is stored.
 */
export function useStoredState<T>(key: string, initial: T, validate: (value: unknown) => value is T): [T, (next: T) => void] {
  const [value, setValue] = useState<T>(() => readStored(key, validate) ?? initial);
  const update = useCallback(
    (next: T) => {
      setValue(next);
      writeStored(key, next);
    },
    [key],
  );
  return [value, update];
}
