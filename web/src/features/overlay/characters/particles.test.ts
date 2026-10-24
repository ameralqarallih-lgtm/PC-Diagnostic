import { describe, expect, it } from 'vitest';
import parity from './particle-parity.json';
import { breathAt, confettiAt, dustAt, particlesOf } from './particles';

/** Samples of each, the recorder's tests check them number for number. */
function samples() {
  const out: unknown[] = [];
  for (const t of [0, 0.3, 0.9, 1.55]) out.push({ kind: 'confetti', t, size: 96, particles: confettiAt(t, 96) });
  for (const t of [0.05, 1.234, 7.9]) for (const facing of [1, -1]) out.push({ kind: 'dust', t, size: 64, facing, particles: dustAt(t, facing, 64) });
  for (const [action, t] of [['idle', 0.4], ['sit', 1.9], ['run', 0.4]] as const) out.push({ kind: 'breath', t, size: 0, action, value: breathAt(action, t) });
  return out;
}

describe('Characters’ particles', () => {
  it('bursts confetti from its head, falling and fading, and puffs dust behind its feet while it runs', () => {
    const start = confettiAt(0, 100);
    expect(start).toHaveLength(24);
    expect(start.every((p) => p.x === 0 && p.y === -90 && p.alpha === 1)).toBe(true);
    const later = confettiAt(1.2, 100);
    expect(later.some((p) => p.y > -90)).toBe(true);
    expect(later[0]!.alpha).toBeLessThan(1);
    expect(confettiAt(1.6, 100)).toEqual([]);
    expect(dustAt(1, 1, 64).every((p) => p.x < 0)).toBe(true);
    expect(dustAt(1, -1, 64).every((p) => p.x > 0)).toBe(true);
    expect(particlesOf(null, 0, 'walk', 1, 64, 1)).toEqual([]);
    expect(breathAt('run', 1)).toBe(1);
    expect(breathAt('idle', 0.65)).toBeCloseTo(1.018);
  });

  it('are the recorder’s (particle-parity.json, which the recorder’s tests read)', async () => {
    const cases = samples();
    const node = (globalThis as unknown as { process?: { env: Record<string, string | undefined>; cwd: () => string } }).process;
    if (node?.env.UPDATE_CHARACTER_PARITY) {
      const fs = (await import(/* @vite-ignore */ ['node', 'fs'].join(':'))) as { writeFileSync: (file: string, text: string) => void };
      fs.writeFileSync(`${node.cwd()}/src/features/overlay/characters/particle-parity.json`, JSON.stringify(cases));
      return;
    }
    expect(parity).toEqual(JSON.parse(JSON.stringify(cases)));
  });
});
