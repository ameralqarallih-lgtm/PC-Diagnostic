import { useSyncExternalStore } from 'react';

const listeners = new Set<() => void>();

/** How tight the app is: Comfortable (the default) or Compact (everything about 12 % smaller), per browser. */
export type Density = 'comfortable' | 'compact';

const DENSITY_KEY = 'pcdiag.density';

export function loadDensity(): Density {
  try {
    return window.localStorage.getItem(DENSITY_KEY) === 'compact' ? 'compact' : 'comfortable';
  } catch {
    return 'comfortable';
  }
}

export function applyDensity(density: Density): void {
  document.documentElement.classList.toggle('compact', density === 'compact');
  try {
    window.localStorage.setItem(DENSITY_KEY, density);
  } catch {
    // It still applies for this page view.
  }
  listeners.forEach((l) => l());
}

/** The density in use, following changes. */
export function useDensity(): Density {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => (document.documentElement.classList.contains('compact') ? 'compact' : 'comfortable'),
    () => 'comfortable',
  );
}
