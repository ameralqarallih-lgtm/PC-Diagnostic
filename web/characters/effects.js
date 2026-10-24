// The effects shown around a reacting character (reactions.json names them), drawn per moment of a loop as SVG in units
// where the character stands 100 tall, the origin at what the effect is attached to: the top of its head ("head") or
// its feet ("feet"), x towards where it faces, y down. bake.html renders them into effects.char (one clip each).
const EFFECTS = {};
(() => {
  const TAU = Math.PI * 2;
  const r = (v) => Math.round(v * 100) / 100;
  /** 0 → 1 → 0 over a phase, smooth. */
  const bump = (u) => Math.sin(Math.PI * Math.min(1, Math.max(0, u)));
  const ease = (u) => u * u * (3 - 2 * u);
  /** A loop's moment for one of n staggered items. */
  const stagger = (phase, i, n) => (phase + i / n) % 1;

  const zee = (x, y, s, o, fill, line) => `<g transform="translate(${r(x)} ${r(y)}) scale(${r(s)})" opacity="${r(o)}">
    <path d="M-6 -6.5 H6.5 L-3.5 4 H6.5 V7 H-7 L3 -3.5 H-6 Z" fill="${fill}" stroke="${line}" stroke-width="1.8" stroke-linejoin="round"/></g>`;
  EFFECTS.zzz = { at: 'head', mirror: false, duration: 2.4, draw: (p) => [0, 1, 2].map((i) => {
    const u = stagger(p, i, 3);
    return zee(12 + 30 * u + 3 * Math.sin(u * TAU), -4 - 46 * u, 0.55 + 0.75 * u, bump(u * 1.05), '#FFFFFF', '#3E4C86');
  }).join('') };

  const drop = (x, y, s, o) => `<g transform="translate(${r(x)} ${r(y)}) scale(${r(s)})" opacity="${r(o)}">
    <path d="M0 -7 C 3 -2, 5 1, 5 3.6 C 5 6.6, 2.6 8.6, 0 8.6 C -2.6 8.6, -5 6.6, -5 3.6 C -5 1, -3 -2, 0 -7 Z" fill="#8FDBFF" stroke="#2C7DB8" stroke-width="1.3"/>
    <ellipse cx="-1.6" cy="3" rx="1.2" ry="2" fill="#FFFFFF" opacity="0.85"/></g>`;
  EFFECTS.sweat = { at: 'head', mirror: true, duration: 1.2, draw: (p) => {
    const a = stagger(p, 0, 2), b = stagger(p, 1, 2);
    // One sliding down the back of the head, one flung off the front.
    return drop(-20, 10 + 14 * ease(a), 0.9, bump(a * 1.1)) + drop(20 + 16 * b, 2 - 18 * b + 30 * b * b, 0.75, bump(b)) + drop(-26 - 10 * b, -2 - 10 * b + 26 * b * b, 0.6, bump(b * 1.2));
  } };

  const star = (x, y, s, o, fill = '#FFE45C', line = '#E08A00') => `<g transform="translate(${r(x)} ${r(y)}) scale(${r(s)})" opacity="${r(o)}">
    <path d="M0 -7 L2.1 -2.2 L7.2 -2.2 L3.1 1 L4.6 6.2 L0 3.2 L-4.6 6.2 L-3.1 1 L-7.2 -2.2 L-2.1 -2.2 Z" fill="${fill}" stroke="${line}" stroke-width="1.4" stroke-linejoin="round"/></g>`;
  EFFECTS.stars = { at: 'head', mirror: false, duration: 1.2, draw: (p) => {
    const items = [0, 1, 2].map((i) => {
      const a = (p + i / 3) * TAU;
      const depth = Math.sin(a);
      return { z: depth, svg: star(26 * Math.cos(a), -4 + 7 * depth, 0.75 + 0.25 * depth, 0.65 + 0.35 * (depth + 1) / 2) };
    });
    const ring = `<ellipse cx="0" cy="-4" rx="26" ry="7" fill="none" stroke="#FFD34D" stroke-width="1.2" stroke-dasharray="3 4" opacity="0.6"/>`;
    return ring + items.sort((x, y) => x.z - y.z).map((x) => x.svg).join('');
  } };

  EFFECTS.exclaim = { at: 'head', mirror: false, duration: 1.0, draw: (p) => {
    const pop = 1 + 0.12 * Math.max(0, Math.sin(p * TAU * 2));
    const shake = 2.5 * Math.sin(p * TAU * 4) * (p < 0.5 ? 1 : 0);
    return `<g transform="translate(${r(18 + shake)} -30) scale(${r(pop)})">
      <path d="M-13 -14 C -13 -22, 13 -22, 13 -14 L 13 6 C 13 12, 8 14, 2 14 L -4 21 L -3 14 C -10 14, -13 11, -13 6 Z" fill="#FFFFFF" stroke="#C81E3A" stroke-width="2.2" stroke-linejoin="round"/>
      <path d="M-2.6 -14 H2.6 L1.6 3 H-1.6 Z" fill="#E8263F"/><circle cx="0" cy="8" r="2.6" fill="#E8263F"/></g>
      <path d="M${r(36 + shake)} -44 L 42 -50 M${r(39 + shake)} -36 L 47 -38 M${r(34 + shake)} -52 L 36 -60" stroke="#E8263F" stroke-width="2.2" stroke-linecap="round" opacity="${r(0.5 + 0.5 * Math.abs(Math.sin(p * TAU * 2)))}"/>`;
  } };

  const flake = (x, y, s, a, o) => `<g transform="translate(${r(x)} ${r(y)}) rotate(${r(a)}) scale(${r(s)})" opacity="${r(o)}">
    <path d="M0 -6 V6 M-5.2 -3 L5.2 3 M-5.2 3 L5.2 -3 M-1.6 -4.6 L0 -3.2 L1.6 -4.6 M-1.6 4.6 L0 3.2 L1.6 4.6" stroke="#5FA8E0" stroke-width="2.6" stroke-linecap="round" fill="none"/>
    <path d="M0 -6 V6 M-5.2 -3 L5.2 3 M-5.2 3 L5.2 -3" stroke="#FFFFFF" stroke-width="1.2" stroke-linecap="round"/></g>`;
  EFFECTS.snow = { at: 'feet', mirror: false, duration: 3.0, draw: (p) => Array.from({ length: 11 }, (_, i) => {
    const u = stagger(p, i, 11);
    const x = -70 + ((i * 37) % 140) + 6 * Math.sin((u + i) * TAU);
    return flake(x, -135 + 140 * u, 0.6 + ((i * 7) % 5) / 10, u * 360 * (i % 2 ? 1 : -1), bump(u * 1.02) * 0.95);
  }).join('') };

  EFFECTS.steam = { at: 'head', mirror: false, duration: 2.0, draw: (p) => [0, 1, 2].map((i) => {
    const u = stagger(p, i, 3);
    const x = -12 + 12 * i;
    const y = -4 - 40 * u;
    const w = 4 + 3 * Math.sin((u + i / 3) * TAU);
    const d = `M${r(x)} ${r(y + 14)} C ${r(x - w)} ${r(y + 9)}, ${r(x + w)} ${r(y + 5)}, ${r(x)} ${r(y)} C ${r(x - w)} ${r(y - 5)}, ${r(x + w)} ${r(y - 9)}, ${r(x)} ${r(y - 14)}`;
    return `<g opacity="${r(bump(u) * 0.85)}"><path d="${d}" stroke="#9AA7B8" stroke-width="5.5" stroke-linecap="round" fill="none" opacity="0.55"/><path d="${d}" stroke="#FFFFFF" stroke-width="3" stroke-linecap="round" fill="none"/></g>`;
  }).join('') };

  EFFECTS.flame = { at: 'head', mirror: false, duration: 0.8, draw: (p) => {
    const f = Math.sin(p * TAU), g = Math.sin(p * TAU * 2 + 1);
    const outer = `M0 ${r(-34 - 3 * f)} C ${r(7 + 2 * g)} -24, 15 -16, 13 -7 C 12 0, 6 3, 0 3 C -6 3, -12 0, -13 -7 C ${r(-15 + g)} -15, ${r(-8 + 2 * f)} -20, ${r(-5 + 2 * f)} -27 C -2 -22, -1 -20, 0 ${r(-34 - 3 * f)} Z`;
    const inner = `M0 ${r(-20 - 2 * g)} C 5 -14, 8 -9, 7 -4 C 6 0, 3 1, 0 1 C -3 1, -6 0, -7 -4 C -8 -9, -4 -13, 0 ${r(-20 - 2 * g)} Z`;
    const embers = [0, 1, 2].map((i) => {
      const u = stagger(p, i, 3);
      return `<circle cx="${r(-8 + 8 * i + 4 * Math.sin(u * TAU))}" cy="${r(-26 - 22 * u)}" r="${r(1.6 - u)}" fill="#FFB02E" opacity="${r(1 - u)}"/>`;
    }).join('');
    return `<defs><linearGradient id="fx-flame" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="#FF4B1F"/><stop offset="1" stop-color="#FFB627"/></linearGradient></defs>
      <path d="${outer}" fill="url(#fx-flame)" stroke="#B8260E" stroke-width="1.8" stroke-linejoin="round"/><path d="${inner}" fill="#FFE97A"/>${embers}`;
  } };

  EFFECTS.dots = { at: 'head', mirror: false, duration: 1.5, draw: (p) => {
    const dot = (i) => {
      const u = (p * 3 - i + 3) % 3;
      const up = u < 1 ? bump(u) : 0;
      return `<circle cx="${-8 + 8 * i}" cy="${r(-2 - 3 * up)}" r="2.6" fill="#5B6478" opacity="${r(0.45 + 0.55 * up)}"/>`;
    };
    return `<circle cx="6" cy="-6" r="2.4" fill="#FFFFFF" stroke="#5B6478" stroke-width="1.2"/><circle cx="11" cy="-13" r="3.6" fill="#FFFFFF" stroke="#5B6478" stroke-width="1.3"/>
      <g transform="translate(26 -32)"><rect x="-17" y="-11" width="34" height="20" rx="10" fill="#FFFFFF" stroke="#5B6478" stroke-width="1.8"/>${[0, 1, 2].map(dot).join('')}</g>`;
  } };

  EFFECTS.data = { at: 'feet', mirror: true, duration: 1.0, draw: (p) => Array.from({ length: 9 }, (_, i) => {
    const u = stagger(p, i, 9);
    const y = -24 - ((i * 23) % 50);
    const x = 10 - 120 * u;
    const bit = i % 3 === 0 ? `<path d="M-3 -3 L1 0 L-3 3" stroke="#0E3B57" stroke-width="1.5" fill="none" stroke-linecap="round"/>` : `<text x="0" y="2.6" font-family="Consolas, monospace" font-size="7.5" font-weight="700" text-anchor="middle" fill="#0E3B57">${i % 2}</text>`;
    return `<g transform="translate(${r(x)} ${y})" opacity="${r(bump(u) * 0.95)}"><rect x="-6" y="-5.5" width="12" height="11" rx="3" fill="#7FF0FF" stroke="#1A8FB8" stroke-width="1.2"/>${bit}</g>` +
      `<path d="M${r(x + 8)} ${y} H ${r(x + 26)}" stroke="#7FF0FF" stroke-width="1.6" stroke-linecap="round" opacity="${r(bump(u) * 0.6)}"/>`;
  }).join('') };

  const bolt = (x, y, s, o) => `<g transform="translate(${r(x)} ${r(y)}) scale(${r(s)})" opacity="${r(o)}">
    <path d="M2 -10 L-5 1 H0 L-2 10 L6 -2 H1 Z" fill="#FFE94D" stroke="#D97B00" stroke-width="1.5" stroke-linejoin="round"/></g>`;
  EFFECTS.sparks = { at: 'feet', mirror: false, duration: 0.9, draw: (p) => Array.from({ length: 6 }, (_, i) => {
    const u = stagger(p, i, 6);
    const x = [-48, 44, -30, 56, -58, 26][i];
    const y = [-92, -78, -40, -30, -62, -104][i];
    const flash = u < 0.35 ? bump(u / 0.35) : 0;
    return bolt(x, y, 0.8 + 0.4 * flash, flash) + star(x + 9, y - 7, 0.35, flash * 0.9, '#FFFFFF', '#FFD34D');
  }).join('') };

  const note = (x, y, s, o, double, fill) => `<g transform="translate(${r(x)} ${r(y)}) scale(${r(s)})" opacity="${r(o)}">${double
    ? `<path d="M-4 4 V-8 L8 -11 V1" stroke="#3B2A5A" stroke-width="2" fill="none" stroke-linejoin="round"/><path d="M-4 -8 L8 -11 V-7 L-4 -4 Z" fill="#3B2A5A"/><ellipse cx="-6.5" cy="4.5" rx="3.6" ry="2.8" fill="${fill}" stroke="#3B2A5A" stroke-width="1.3" transform="rotate(-20 -6.5 4.5)"/><ellipse cx="5.5" cy="1.5" rx="3.6" ry="2.8" fill="${fill}" stroke="#3B2A5A" stroke-width="1.3" transform="rotate(-20 5.5 1.5)"/>`
    : `<path d="M2 4 V-10 C 6 -8, 9 -6, 8 -1" stroke="#3B2A5A" stroke-width="2" fill="none" stroke-linecap="round"/><ellipse cx="-0.5" cy="4.5" rx="3.8" ry="2.9" fill="${fill}" stroke="#3B2A5A" stroke-width="1.3" transform="rotate(-20 -0.5 4.5)"/>`}</g>`;
  EFFECTS.notes = { at: 'head', mirror: false, duration: 2.0, draw: (p) => [0, 1, 2, 3].map((i) => {
    const u = stagger(p, i, 4);
    const side = i % 2 ? -1 : 1;
    return note(side * (14 + 14 * u) + 4 * Math.sin(u * TAU * 1.5), -6 - 42 * u, 0.8 + 0.3 * u, bump(u * 1.05), i % 3 === 1, ['#FF7EB6', '#8E7CFF', '#5CC8FF', '#FFC44D'][i]);
  }).join('') };

  EFFECTS.rain = { at: 'head', mirror: false, duration: 1.0, draw: (p) => {
    const bob = 1.5 * Math.sin(p * TAU);
    const cloud = `<g transform="translate(0 ${r(-36 + bob)})"><path d="M-22 6 C -30 6, -31 -5, -22 -6 C -21 -14, -9 -16, -5 -9 C -1 -17, 14 -16, 15 -7 C 24 -8, 26 6, 17 6 Z" fill="#AEB8C8" stroke="#56627A" stroke-width="1.8" stroke-linejoin="round"/>
      <path d="M-18 1 C -12 3, 8 3, 14 1" stroke="#FFFFFF" stroke-width="1.6" opacity="0.5" fill="none" stroke-linecap="round"/></g>`;
    const drops = Array.from({ length: 6 }, (_, i) => {
      const u = stagger(p, i, 6);
      const x = -16 + 6.5 * i;
      const y = -28 + 50 * u;
      return `<path d="M${r(x)} ${r(y)} l -1.2 5" stroke="#5DA9E8" stroke-width="2" stroke-linecap="round" opacity="${r(u < 0.85 ? 0.9 : (1 - u) * 6)}"/>`;
    }).join('');
    return drops + cloud;
  } };

  const heart = (x, y, s, o, fill) => `<g transform="translate(${r(x)} ${r(y)}) scale(${r(s)})" opacity="${r(o)}">
    <path d="M0 6 C -9 -1, -8 -8, -3.6 -8 C -1.6 -8, -0.4 -6.6, 0 -5.4 C 0.4 -6.6, 1.6 -8, 3.6 -8 C 8 -8, 9 -1, 0 6 Z" fill="${fill}" stroke="#B5174E" stroke-width="1.4" stroke-linejoin="round"/>
    <ellipse cx="-3.4" cy="-4.6" rx="1.4" ry="1" fill="#FFFFFF" opacity="0.8"/></g>`;
  EFFECTS.hearts = { at: 'head', mirror: false, duration: 2.0, draw: (p) => [0, 1, 2].map((i) => {
    const u = stagger(p, i, 3);
    const side = [-1, 1, 0.3][i];
    return heart(side * (12 + 10 * u) + 3 * Math.sin(u * TAU * 2), -2 - 40 * u, 0.8 + 0.35 * bump(u), bump(u * 1.05), ['#FF6F9F', '#FF8FB8', '#FF5C8A'][i]);
  }).join('') };

  EFFECTS.wind = { at: 'feet', mirror: true, duration: 1.2, draw: (p) => Array.from({ length: 5 }, (_, i) => {
    const u = stagger(p, i, 5);
    const y = -28 - ((i * 19) % 64);
    const x = 50 - 150 * u;
    const curl = i % 2 === 0;
    const d = curl ? `M${r(x + 30)} ${y} C ${r(x + 14)} ${y}, ${r(x)} ${y - 2}, ${r(x - 4)} ${y - 8} C ${r(x - 7)} ${y - 13}, ${r(x + 2)} ${y - 15}, ${r(x + 3)} ${y - 9}` : `M${r(x + 34)} ${y} C ${r(x + 20)} ${y + 2}, ${r(x + 8)} ${y - 2}, ${r(x - 6)} ${y}`;
    return `<g opacity="${r(bump(u) * 0.9)}"><path d="${d}" stroke="#7CA9C9" stroke-width="4" stroke-linecap="round" fill="none" opacity="0.5"/><path d="${d}" stroke="#FFFFFF" stroke-width="2" stroke-linecap="round" fill="none"/></g>`;
  }).join('') };
})();
