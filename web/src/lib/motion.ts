import { useRef } from 'react';

/**
 * Tells rows that arrive while a list is shown from the ones it opened with: only arrivals get an entrance (a list
 * whose every row slides in when the page opens looks like it is still loading). The first rows the list has are
 * its opening rows, even when they come a moment after it is shown.
 */
export function useArrivals<T>(rows: readonly T[], key: (row: T) => string): (row: T) => boolean {
  const known = useRef<Set<string> | null>(null);
  if (known.current === null && rows.length > 0) known.current = new Set(rows.map(key));
  const opening = known.current;
  // A row's entrance plays when it mounts, so telling arrivals once is enough (re-renders do not replay it).
  return (row: T) => opening !== null && !opening.has(key(row));
}

/** Easing curves of the app's motion (expressive, but quick to settle). */
export const EASE = {
  /** Things arriving: fast start, soft landing. */
  out: [0.16, 1, 0.3, 1] as [number, number, number, number],
  /** Things moving from one place to another. */
  inOut: [0.65, 0, 0.35, 1] as [number, number, number, number],
};

/** A spring for things that follow the pointer or change place (tabs, the navigation highlight). */
export const SPRING = { type: 'spring' as const, stiffness: 520, damping: 40, mass: 0.9 };
