import { describe, expect, it } from 'vitest';

// Every source file, as text.
const sources = import.meta.glob(['../**/*.ts', '../**/*.tsx', '!../**/*.test.ts', '!../**/*.test.tsx'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

describe('effects', () => {
  it('have block bodies, so nothing they return is taken for a clean-up', () => {
    // Browsers now return Promises from scrollIntoView and scrollTo: `useEffect(() => el.scrollIntoView())` crashed
    // React when the page closed ("destroy is not a function", 15 Oct 2026).
    const offenders = Object.entries(sources).flatMap(([file, text]) =>
      [...text.matchAll(/use(?:Layout)?Effect\(\s*\(\)\s*=>\s*(?![\s{(])/g)].map((m) => `${file}:${text.slice(0, m.index).split('\n').length}`),
    );
    expect(offenders).toEqual([]);
  });
});
