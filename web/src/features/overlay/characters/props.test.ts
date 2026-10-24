import { describe, expect, it } from 'vitest';
import parity from './prop-parity.json';
import { ARRIVE_SECONDS, LEAVE_SECONDS, PROPS, PropTrack, emitAt, poseAt, propInfo } from './props';
import { EFFECTS, KINDS, STATUSES } from './reactions';
import { readPack } from './pack';

/** What the recorder must draw alike (CharacterMindTests.cs reads the same file): poses, what props give off, and a track's changes. */
function samples() {
  const poses = [];
  for (const prop of PROPS) {
    for (const [age, gone, now, facing, moving] of [
      [0, -1, 0, 1, false],
      [0.1, -1, 0.7, 1, false],
      [0.3, -1, 1.3, -1, false],
      [2, -1, 2.45, 1, false],
      [2, 0.12, 3.1, -1, false],
      [2, -1, 4, 1, true],
    ] as const) {
      poses.push({ prop: prop.id, age, gone, now, facing, moving, size: 96, headX: 6, headY: -88, reach: 31, pose: poseAt(prop, age, gone, now, facing, 96, 6, -88, 31, moving) });
    }
  }
  const emits = [];
  for (const prop of PROPS.filter((p) => p.emit)) for (const now of [0, 0.37, 1.21, 2.9]) for (const facing of [1, -1]) emits.push({ prop: prop.id, now, facing, size: 80, particles: emitAt(prop, now, facing, 80) });
  const track = new PropTrack();
  const steps = [
    [0, 'cone'],
    [0.5, 'cone'],
    [1, 'siren'],
    [1.1, 'siren'],
    [1.2, null],
    [1.4, null],
    [1.6, 'trophy'],
    [1.7, 'trophy'],
  ] as const;
  const changes = steps.map(([now, prop]) => ({ now, prop, shown: track.at(now, prop) }));
  return { poses, emits, track: changes };
}

describe('Props', () => {
  it('each has its frame in the props file, and the reactions show only props there are', async () => {
    const node = (globalThis as unknown as { process?: { cwd: () => string } }).process;
    const fs = (await import(/* @vite-ignore */ ['node', 'fs'].join(':'))) as { readFileSync: (file: string) => Uint8Array };
    const pack = readPack(new Uint8Array(fs.readFileSync(`${node!.cwd()}/../src/PcDiag.Core/Overlay/Characters/props.char`)), { effects: true });
    expect(pack?.clips.map((c) => c.action).sort()).toEqual(PROPS.map((p) => p.id).sort());
    for (const kind of Object.values(KINDS)) for (const prop of Object.values(kind.props ?? {})) expect(propInfo(prop!)).toBeDefined();
    expect(Object.keys(KINDS.temperature.props ?? {})).toEqual(['low', 'high', 'critical']);
    expect(STATUSES).toContain('critical');
    // Props are not effects: their names stand apart.
    expect(PROPS.some((p) => EFFECTS.some(([e]) => e === p.id))).toBe(false);
  });

  it('arrive (popping up on it, dropping in beside it), move, and go, fading', () => {
    const ice = propInfo('ice-pack')!;
    const cone = propInfo('cone')!;
    // On its head: at the top of its head, popping up past its size, then settled.
    expect(poseAt(ice, 0, -1, 0, 1, 100, 4, -90, 30, false)).toMatchObject({ x: 4, alpha: 0 });
    expect(poseAt(ice, ARRIVE_SECONDS * 0.6, -1, 0, 1, 100, 4, -90, 30, false).scale).toBeGreaterThan(1);
    expect(poseAt(ice, 3, -1, 0, 1, 100, 4, -90, 30, false)).toMatchObject({ scale: 1, alpha: 1 });
    // Beside it the way it faces, dropped from above and landed.
    expect(poseAt(cone, 0.01, -1, 0, -1, 100, 0, -90, 30, false).y).toBeLessThan(-40);
    // Clear of its body (30 px) by 4, its middle half its width further.
    expect(poseAt(cone, 3, -1, 0, -1, 100, 0, -90, 30, false).x).toBeCloseTo(-(30 + 4 + 14.5));
    expect(poseAt(cone, 3, -1, 0, -1, 100, 0, -90, 30, false)).toMatchObject({ y: 0, alpha: 1 });
    // Left behind while it walks; gone after it went.
    expect(poseAt(cone, 3, -1, 0, 1, 100, 0, -90, 30, true).alpha).toBe(0);
    expect(poseAt(cone, 3, LEAVE_SECONDS, 0, 1, 100, 0, -90, 30, false).alpha).toBe(0);
    // A fan blows back at the character, whichever way it faces.
    const fan = propInfo('desk-fan')!;
    expect(emitAt(fan, 0.3, 1, 100).every((p) => p.x < 0)).toBe(true);
    expect(emitAt(fan, 0.3, -1, 100).every((p) => p.x > 0)).toBe(true);
  });

  it('change on a track: the new one arrives as the old one goes', () => {
    const track = new PropTrack();
    expect(track.at(0, 'cone')).toMatchObject({ id: 'cone', age: 0, going: null });
    expect(track.at(1, 'siren')).toMatchObject({ id: 'siren', age: 0, going: 'cone', gone: 0 });
    expect(track.at(1.1, 'siren').going).toBe('cone');
    expect(track.at(1 + LEAVE_SECONDS, 'siren').going).toBeNull();
  });

  it('are the recorder’s (prop-parity.json, which the recorder’s tests read)', async () => {
    const cases = samples();
    const node = (globalThis as unknown as { process?: { env: Record<string, string | undefined>; cwd: () => string } }).process;
    if (node?.env.UPDATE_CHARACTER_PARITY) {
      const fs = (await import(/* @vite-ignore */ ['node', 'fs'].join(':'))) as { writeFileSync: (file: string, text: string) => void };
      fs.writeFileSync(`${node.cwd()}/src/features/overlay/characters/prop-parity.json`, JSON.stringify(cases));
      return;
    }
    expect(parity).toEqual(JSON.parse(JSON.stringify(cases)));
  });
});
