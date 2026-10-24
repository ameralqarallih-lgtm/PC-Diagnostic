import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * The most height a scrolling list can take to reach the bottom of the window, from where it starts on the page
 * (with the page scrolled to the top), so a long list uses the whole window instead of a fixed height. Never less
 * than `min` (a list far down a long page still shows enough rows); `gap` keeps room below it (the card's padding).
 * Follows the window's size and the content above it.
 */
export function useFillHeight<T extends HTMLElement>(min = 320, gap = 24): [(el: T | null) => void, number | undefined] {
  const [height, setHeight] = useState<number | undefined>(undefined);
  const element = useRef<T | null>(null);
  const measure = useCallback(() => {
    const el = element.current;
    if (!el || typeof window === 'undefined') return;
    const top = el.getBoundingClientRect().top + window.scrollY;
    const next = Math.max(min, Math.floor(window.innerHeight - top - gap));
    setHeight((old) => (old === next ? old : next));
  }, [min, gap]);
  const ref = useCallback(
    (el: T | null) => {
      element.current = el;
      measure();
    },
    [measure],
  );
  useEffect(() => {
    if (typeof window === 'undefined') return undefined;
    window.addEventListener('resize', measure);
    // Content above it growing or shrinking (notices, filters, a chart loading) moves it.
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => measure());
    observer?.observe(document.body);
    return () => {
      window.removeEventListener('resize', measure);
      observer?.disconnect();
    };
  }, [measure]);
  return [ref, height];
}
