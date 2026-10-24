import { useEffect, useState } from 'react';

/** The theme's colors (CSS variables), for canvas charts that cannot use CSS directly. */
export interface ThemeColors {
  foreground: string;
  muted: string;
  border: string;
  surface: string;
  accent: string;
  danger: string;
  warning: string;
  success: string;
  unavailable: string;
  dark: boolean;
}

/** Series colors, chosen to stay distinct in both themes. */
export const SERIES_COLORS = {
  cpu: '#38bdf8',
  memory: '#a78bfa',
  gpu: '#34d399',
  vram: '#2dd4bf',
  disk: '#fbbf24',
  netReceive: '#60a5fa',
  netSend: '#f472b6',
  cpuTemperature: '#fb923c',
  gpuTemperature: '#f87171',
  gpuHotspot: '#ef4444',
} as const;

function read(): ThemeColors {
  const style = getComputedStyle(document.documentElement);
  const v = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
  return {
    foreground: v('--foreground', '#e6edf3'),
    muted: v('--muted', '#9aa7b4'),
    border: v('--border', '#253041'),
    surface: v('--surface', '#121821'),
    accent: v('--accent', '#38bdf8'),
    danger: v('--danger', '#ef4444'),
    warning: v('--warning', '#f59e0b'),
    success: v('--success', '#22c55e'),
    unavailable: v('--unavailable', '#7d8a98'),
    dark: document.documentElement.classList.contains('dark'),
  };
}

/** Current theme colors; updates when the theme class on &lt;html&gt; changes. */
export function useThemeColors(): ThemeColors {
  const [colors, setColors] = useState<ThemeColors>(read);

  useEffect(() => {
    const observer = new MutationObserver(() => setColors(read()));
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => observer.disconnect();
  }, []);

  return colors;
}
