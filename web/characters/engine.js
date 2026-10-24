// The shipped characters' drawings and how they move (not part of the app): each character is layered SVG parts
// (fills, shading clipped to the part, line art, gradients) hanging from parents, posed by keyframes of rotation about
// a pivot, offset, scale and opacity, eased with a cosine and looping. bake.html renders every clip's frames from these
// and scripts/bake-characters.mjs packs them into the ".char" files the app ships (src/PcDiag.Core/Overlay/Characters).
// The app itself only plays those frames.

/** The characters, by id (each file adds its own). */
const CHARACTERS = {};

function mul(m, n) {
  return [m[0] * n[0] + m[2] * n[1], m[1] * n[0] + m[3] * n[1], m[0] * n[2] + m[2] * n[3], m[1] * n[2] + m[3] * n[3], m[0] * n[4] + m[2] * n[5] + m[4], m[1] * n[4] + m[3] * n[5] + m[5]];
}

/** Offset, then about a pivot: rotation (degrees) and scale. */
function local(p, pivot) {
  const a = (p.r * Math.PI) / 180, c = Math.cos(a), s = Math.sin(a), [px, py] = pivot;
  const ma = c * p.sx, mb = s * p.sx, mc = -s * p.sy, md = c * p.sy;
  return [ma, mb, mc, md, p.x + px - (ma * px + mc * py), p.y + py - (mb * px + md * py)];
}

/** A track's values at a phase (0-1), eased between keyframes, looping. */
function sample(track, phase) {
  const D = { r: 0, x: 0, y: 0, sx: 1, sy: 1, o: 1 };
  if (!track || !track.length) return { ...D };
  const v = (k) => ({ ...D, ...k });
  if (track.length === 1) return v(track[0][1]);
  const last = track.length - 1;
  let t0, k0, t1, k1;
  if (phase < track[0][0]) { [t0, k0] = [track[last][0] - 1, track[last][1]]; [t1, k1] = track[0]; }
  else {
    let i = last; while (i > 0 && track[i][0] > phase) i--;
    [t0, k0] = track[i]; [t1, k1] = i < last ? track[i + 1] : [track[0][0] + 1, track[0][1]];
  }
  const u = t1 - t0 <= 0 ? 0 : Math.min(1, Math.max(0, (phase - t0) / (t1 - t0)));
  const e = 0.5 - 0.5 * Math.cos(Math.PI * u);
  const a = v(k0), b = v(k1), out = {};
  for (const k of Object.keys(D)) out[k] = a[k] + (b[k] - a[k]) * e;
  return out;
}

/** The eyes' blink: a quick squash once a loop (a clip's frames hold one blink at most; see blinkAt). */
const BLINK = [[0, {}], [0.86, {}], [0.92, { sy: 0.08 }], [0.98, {}]];
const n = (v) => (Math.round(v * 1000) / 1000).toString();

/**
 * A character drawn at a phase (0-1) of a clip, as SVG markup in its own units. `blink` is the blink's phase (null:
 * none), `shadow` whether its shadow is on the ground under it.
 */
function renderCharacter(ch, action, phase, { blink = null, shadow = true } = {}) {
  const anim = ch.anims[action];
  const rootP = sample(anim.tracks.root, phase);
  const root = local(rootP, ch.anchor);
  const squash = blink == null ? 1 : sample(BLINK, blink).sy;
  const world = {};
  const out = [`<defs>${ch.defs || ''}</defs>`];
  if (shadow) {
    // Soft, under the feet; smaller as the character leaves the ground.
    const lift = Math.max(0, -rootP.y);
    out.push(`<ellipse cx="${ch.anchor[0]}" cy="${ch.anchor[1] + 1}" rx="${n(ch.shadow * (1 - lift / 90))}" ry="${n(ch.shadow * 0.16 * (1 - lift / 90))}" fill="url(#${ch.id}-shadow)"/>`);
  }
  for (const part of ch.parts) {
    const parent = part.parent ? world[part.parent] : { m: root, o: rootP.o };
    if (!parent) continue;
    const p = sample(anim.tracks[part.id], phase);
    if (part.offset) { p.x += part.offset[0]; p.y += part.offset[1]; }
    if (part.hidden && !anim.tracks[part.id]) p.o = 0;
    if (part.blink) p.sy *= squash;
    const m = mul(parent.m, local(p, part.pivot || [0, 0]));
    const o = parent.o * p.o;
    world[part.id] = { m, o };
    if (part.svg && o > 0.001) out.push(`<g transform="matrix(${m.map(n).join(' ')})"${o < 0.999 ? ` opacity="${n(o)}"` : ''}>${part.svg}</g>`);
  }
  return out.join('');
}

/** A part that is a base shape, shading and highlights clipped to it, then its line art (all or part of its outline). */
function layered(id, base, { fill, line, width = 2.2, shade, light, extra = '', outline }) {
  const clip = `${id}-clip`;
  return `<clipPath id="${clip}"><path d="${base}"/></clipPath>` +
    `<path d="${base}" fill="${fill}"/>` +
    `<g clip-path="url(#${clip})">${shade ? `<path d="${shade.d}" fill="${shade.fill}" opacity="${shade.o ?? 1}"/>` : ''}${light ? `<path d="${light.d}" fill="${light.fill}" opacity="${light.o ?? 1}"/>` : ''}${extra}</g>` +
    `<path d="${outline || base}" fill="none" stroke="${line}" stroke-width="${width}" stroke-linejoin="round" stroke-linecap="round"/>`;
}
