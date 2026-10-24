import { useEffect, useRef, useState } from 'react';
import { useReducedMotion } from 'motion/react';

/** How long a value takes to glide to a new one (as the recorder does on the screens). */
export const GLIDE_MS = 450;

/** Eases out (fast first, settling into the value): 1 - (1 - t)³. */
export const easeOut = (t: number) => 1 - (1 - Math.min(1, Math.max(0, t))) ** 3;

/** Whether a number glides from what is shown to a new one: both finite and different. */
const glides = (from: number | null | undefined, to: number | null | undefined): from is number => typeof from === 'number' && typeof to === 'number' && Number.isFinite(from) && Number.isFinite(to) && from !== to;

/**
 * A set of values that glides to each new set: every number that changed moves to its new value over {@link GLIDE_MS}
 * (easing out), the rest changes at once. `read` and `write` say where an item's number is. Off (the new values at
 * once) when the user prefers reduced motion or `enabled` is false.
 */
export function useGlidingValues<K, V>(values: ReadonlyMap<K, V>, read: (v: V) => number | null | undefined, write: (v: V, n: number) => V, enabled = true): ReadonlyMap<K, V> {
  const reduced = useReducedMotion() ?? false;
  const on = enabled && !reduced;
  const [shown, setShown] = useState(values);
  // What is drawn now (also part way), the values gliding from and to, and when the glide started.
  const drawn = useRef(values);
  const glide = useRef<{ from: ReadonlyMap<K, V>; to: ReadonlyMap<K, V>; start: number } | null>(null);
  const io = useRef({ read, write });
  io.current = { read, write };

  useEffect(() => {
    const { read: get, write: put } = io.current;
    const from = drawn.current;
    const moving = on && [...values].some(([k, v]) => glides(from.get(k) && get(from.get(k)!), get(v)));
    if (!moving) {
      drawn.current = values;
      glide.current = null;
      setShown(values);
      return undefined;
    }
    glide.current = { from, to: values, start: performance.now() };
    let frame = 0;
    const step = (now: number) => {
      const g = glide.current;
      if (!g) return;
      const t = (now - g.start) / GLIDE_MS;
      const e = easeOut(t);
      const next = new Map<K, V>();
      for (const [k, v] of g.to) {
        const was = g.from.get(k);
        const a = was === undefined ? undefined : get(was);
        const b = get(v);
        next.set(k, glides(a, b) ? put(v, a + (b! - a) * e) : v);
      }
      drawn.current = t >= 1 ? g.to : next;
      setShown(drawn.current);
      if (t < 1) frame = requestAnimationFrame(step);
    };
    frame = requestAnimationFrame(step);
    return () => {
      cancelAnimationFrame(frame);
    };
  }, [values, on]);

  return on ? shown : values;
}

