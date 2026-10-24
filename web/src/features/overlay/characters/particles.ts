/**
 * Particles drawn in code with a character (as the recorder's CharacterParticles.cs, the same numbers, checked by
 * particles.test.ts against particle-parity.json which the recorder's tests read): confetti bursting from its head on a
 * happy moment and dust puffing behind its feet while it runs; and its breathing while it stands, sits or sleeps.
 */

export const CONFETTI_SECONDS = 1.6;
export const DUST_EVERY = 0.17;
export const DUST_SECONDS = 0.5;

/** A particle: where (from its feet, in pixels), how big, its color (#AARRGGBB as a number) and opacity, and its turn (radians). */
export interface Particle {
  x: number;
  y: number;
  r: number;
  color: number;
  alpha: number;
  angle: number;
}

const CONFETTI = [0xfff43f5e, 0xfff59e0b, 0xff10b981, 0xff3b82f6, 0xffa855f7, 0xfffde047];

/** A number from 0 to 1 for a particle and a draw (the same in the recorder). */
export function random(particle: number, draw: number) {
  const x = Math.sin(particle * 12.9898 + draw * 78.233) * 43758.5453;
  return x - Math.floor(x);
}

/** A confetti burst `age` seconds in, from the head of a character of `size` pixels. */
export function confettiAt(age: number, size: number): Particle[] {
  if (age < 0 || age >= CONFETTI_SECONDS) return [];
  const fade = 1 - (age / CONFETTI_SECONDS) ** 3;
  const pieces: Particle[] = [];
  for (let k = 0; k < 24; k++) {
    const angle = -Math.PI / 2 + (random(k, 1) - 0.5) * Math.PI * 0.9;
    const speed = size * (1.2 + random(k, 2) * 1.4);
    const gravity = size * 2.2;
    pieces.push({
      x: Math.cos(angle) * speed * age,
      y: -size * 0.9 + Math.sin(angle) * speed * age + 0.5 * gravity * age * age,
      r: size * 0.035,
      color: CONFETTI[k % CONFETTI.length]!,
      alpha: fade,
      angle: age * (4 + random(k, 3) * 8) * (random(k, 4) > 0.5 ? 1 : -1),
    });
  }
  return pieces;
}

/** The puffs of dust behind the feet of a character running (facing `facing`) at `now` seconds. */
export function dustAt(now: number, facing: number, size: number): Particle[] {
  const puffs: Particle[] = [];
  const last = Math.floor(now / DUST_EVERY) * DUST_EVERY;
  for (let j = 0; j < 3; j++) {
    const age = now - (last - j * DUST_EVERY);
    if (age < 0 || age >= DUST_SECONDS) continue;
    puffs.push({ x: -facing * (size * 0.12 + age * size * 0.5), y: -age * size * 0.15, r: size * (0.04 + age * 0.12), color: 0xffc8beaa, alpha: 0.45 * (1 - age / DUST_SECONDS), angle: 0 });
  }
  return puffs;
}

/** How tall it is drawn against its size at a moment: breathing (1.8 % up and down every 2.6 s) while it stands, sits or sleeps. */
export const breathAt = (action: string, now: number) => (action === 'idle' || action === 'sit' || action === 'sleep' ? 1 + 0.018 * Math.sin((2 * Math.PI * now) / 2.6) : 1);

/** The particles of a character at a moment: its burst (confetti) and, running, its dust. */
export function particlesOf(burst: string | null, burstAge: number, action: string, facing: number, size: number, now: number): Particle[] {
  return [...(burst === 'confetti' ? confettiAt(burstAge, size) : []), ...(action === 'run' ? dustAt(now, facing, size) : [])];
}

/** Draws particles at (`x`, `y`: its feet on the canvas), `k` canvas pixels to a pixel: dust as soft discs, confetti as turning bits. */
export function drawParticles(ctx: CanvasRenderingContext2D, particles: readonly Particle[], x: number, y: number, k: number) {
  for (const p of particles) {
    const a = ((p.color >>> 24) & 0xff) / 255;
    const rgb = `${(p.color >>> 16) & 0xff},${(p.color >>> 8) & 0xff},${p.color & 0xff}`;
    ctx.save();
    ctx.globalAlpha *= Math.max(0, Math.min(1, p.alpha * a));
    ctx.translate(x + p.x * k, y + p.y * k);
    ctx.fillStyle = `rgb(${rgb})`;
    if (p.angle === 0) {
      ctx.beginPath();
      ctx.arc(0, 0, p.r * k, 0, Math.PI * 2);
      ctx.fill();
    } else {
      ctx.rotate(p.angle);
      ctx.fillRect(-p.r * k, -p.r * 0.5 * k, p.r * 2 * k, p.r * k);
    }
    ctx.restore();
  }
}
