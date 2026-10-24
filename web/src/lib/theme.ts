export type Theme = 'dark' | 'light';

const STORAGE_KEY = 'pcdiag.theme';

/** Reads the saved theme. Storage can be unavailable (private mode, blocked site data); fall back to dark. */
export function loadTheme(): Theme {
  try {
    return window.localStorage.getItem(STORAGE_KEY) === 'light' ? 'light' : 'dark';
  } catch {
    return 'dark';
  }
}

export function applyTheme(theme: Theme): void {
  document.documentElement.classList.toggle('dark', theme === 'dark');
  try {
    window.localStorage.setItem(STORAGE_KEY, theme);
  } catch {
    // The theme still applies for this page view.
  }
}

interface ViewTransitions {
  startViewTransition?: (options: { update: () => void; types: string[] }) => { ready: Promise<void> };
}

/**
 * Switches the theme with the new one growing in a circle from `from` (the toggle), where the browser has view
 * transitions and the user has not asked for less motion; otherwise at once.
 */
export function switchTheme(theme: Theme, from?: { x: number; y: number }): void {
  const start = (document as unknown as ViewTransitions).startViewTransition?.bind(document);
  const reduced = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  if (!start || !from || reduced) {
    applyTheme(theme);
    return;
  }
  let transition: { ready: Promise<void> };
  try {
    transition = start({ update: () => applyTheme(theme), types: ['theme'] });
  } catch {
    // An engine that takes only a callback.
    applyTheme(theme);
    return;
  }
  const radius = Math.hypot(Math.max(from.x, window.innerWidth - from.x), Math.max(from.y, window.innerHeight - from.y));
  transition.ready
    .then(() =>
      document.documentElement.animate(
        { clipPath: [`circle(0px at ${from.x}px ${from.y}px)`, `circle(${radius}px at ${from.x}px ${from.y}px)`] },
        { duration: 560, easing: 'cubic-bezier(0.65, 0, 0.35, 1)', pseudoElement: '::view-transition-new(root)' },
      ),
    )
    .catch(() => undefined);
}
