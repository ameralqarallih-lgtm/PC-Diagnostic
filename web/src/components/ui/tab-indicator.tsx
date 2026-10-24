import { motion } from 'motion/react';
import { useEffect, useRef, type ReactNode } from 'react';
import { EASE, SPRING } from '@/lib/motion';

/**
 * The chosen tab's underline. It slides to the tab chosen next (pass one name per tab list, e.g. from useId); the
 * tab needs `relative`.
 */
export function TabUnderline({ group }: { group: string }) {
  return <motion.span layoutId={group} aria-hidden className="pointer-events-none absolute inset-x-0 -bottom-0.5 h-0.5 rounded-full bg-accent" transition={SPRING} />;
}

/** The chosen tab's raised background, in a segmented tab list. The tab needs `relative isolate`. */
export function TabPill({ group }: { group: string }) {
  return <motion.span layoutId={group} aria-hidden className="pointer-events-none absolute inset-0 -z-10 rounded bg-surface shadow-sm" transition={SPRING} />;
}

/**
 * A tab's content: it fades in, rising a little, when another tab is chosen (not when the page opens, which shows
 * it at once).
 */
export function TabFade({ tab, children, className }: { tab: string; children: ReactNode; className?: string }) {
  const opened = useRef(false);
  useEffect(() => {
    opened.current = true;
  }, []);
  return (
    <motion.div key={tab} className={className} initial={opened.current ? { opacity: 0, y: 6 } : false} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.26, ease: EASE.out }}>
      {children}
    </motion.div>
  );
}
