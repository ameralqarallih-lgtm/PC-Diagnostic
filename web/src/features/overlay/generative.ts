/**
 * Generated, animated backgrounds for panels, drawn on the GPU (WebGL2): a scene that loops seamlessly (every motion is
 * a whole number of turns over the loop) and, over it, frosted-glass cards fitted to the panel's tiles — the scene
 * blurred and lifted inside each card, a fine luminous edge with a light travelling round it, a soft shadow, and under
 * each dial an etched track with fine ticks along its sweep. The editor draws it live, and bakes its frames for the
 * recorder (see bake.ts).
 */

/** The generated scenes. */
export const GENERATIVE_KINDS = ['silk', 'mesh', 'aurora', 'nebula', 'caustics', 'bokeh', 'topo', 'prism', 'ocean', 'lava', 'ink', 'marble', 'foil', 'fireflies', 'warp', 'galaxy', 'clouds', 'snow', 'ribbons', 'circuit'] as const;
export type GenerativeKind = (typeof GENERATIVE_KINDS)[number];

export const GENERATIVE: Record<GenerativeKind, { name: string; description: string; palette: [string, string, string, string, string]; accent: string }> = {
  silk: { name: 'Liquid Silk', description: 'Satin folds flowing in plum, violet and rose, catching the light', palette: ['#0A0614', '#2A0F4A', '#7A1F6E', '#E0637A', '#F7C59F'], accent: '#F7C59F' },
  mesh: { name: 'Mesh Gradient', description: 'Soft pools of colour drifting into each other, clean and calm', palette: ['#070B1E', '#2B2F9E', '#7C3AED', '#E9568F', '#22B8C9'], accent: '#C4B5FD' },
  aurora: { name: 'Aurora Veil', description: 'Curtains of green and violet light rippling over a starry night', palette: ['#01030A', '#062433', '#13C59A', '#5EEAD4', '#A855F7'], accent: '#5EEAD4' },
  nebula: { name: 'Nebula Drift', description: 'Slow clouds of gas and dust turning in deep space, with stars', palette: ['#02030C', '#1B1450', '#5B2A86', '#E0645C', '#FFC58A'], accent: '#FDBA74' },
  caustics: { name: 'Deep Caustics', description: 'Sunlight dancing on the floor of a clear, deep lagoon', palette: ['#011422', '#033A55', '#0B7A8F', '#3CD3D8', '#D9FFFA'], accent: '#7FF5EE' },
  bokeh: { name: 'City Bokeh', description: 'Out-of-focus lights drifting at different depths, like a night street', palette: ['#06070D', '#1A1530', '#F59E0B', '#F43F5E', '#38BDF8'], accent: '#FBBF24' },
  topo: { name: 'Topography', description: 'Contour lines of a living landscape, slowly reshaping', palette: ['#03070F', '#0B2037', '#14506B', '#2DD4BF', '#E0F2FE'], accent: '#5EEAD4' },
  prism: { name: 'Prism Light', description: 'Beams of light split into colour sweeping across dark glass', palette: ['#05050A', '#14122A', '#6D28D9', '#22D3EE', '#FDE68A'], accent: '#E0E7FF' },
  ocean: { name: 'Ocean Swell', description: 'Swells rolling under a low sun at dusk, its path glittering on the sea', palette: ['#030714', '#0B2447', '#19376D', '#F28C5A', '#FFD8A8'], accent: '#FFB27A' },
  lava: { name: 'Lava Lamp', description: 'Warm wax rising, merging and sinking in a glowing lamp', palette: ['#12030A', '#3A0B1E', '#E8411F', '#FF8A2B', '#FFE08A'], accent: '#FFA94D' },
  ink: { name: 'Ink in Water', description: 'Clouds of ink spreading and curling in still water', palette: ['#05070D', '#0E1626', '#1E3A8A', '#7C3AED', '#E0E7FF'], accent: '#A5B4FC' },
  marble: { name: 'Liquid Marble', description: 'Black marble with veins of gold slowly flowing', palette: ['#050505', '#141414', '#2A2A2E', '#8C7A55', '#E7C77B'], accent: '#E7C77B' },
  foil: { name: 'Holo Foil', description: 'Holographic foil shifting through its colours, a shine sweeping across', palette: ['#07060D', '#1B1630', '#5B4B8A', '#9AD0EC', '#FFFFFF'], accent: '#C4F1F9' },
  fireflies: { name: 'Fireflies', description: 'A summer night in the woods, fireflies wandering and blinking', palette: ['#020604', '#0A1A12', '#1E4D2B', '#D9F99D', '#FDE68A'], accent: '#D9F99D' },
  warp: { name: 'Hyperspace', description: 'Stars streaking past at light speed', palette: ['#01020A', '#0B1238', '#7DD3FC', '#A78BFA', '#FFFFFF'], accent: '#7DD3FC' },
  galaxy: { name: 'Spiral Galaxy', description: 'A spiral galaxy turning slowly, its arms full of stars', palette: ['#02020A', '#1E1B4B', '#6366F1', '#F0ABFC', '#FFF7ED'], accent: '#C7D2FE' },
  clouds: { name: 'Cloud Drift', description: 'Soft clouds forming and drifting in the blue hour', palette: ['#0B1023', '#1F3A68', '#8FA3C8', '#F2A07B', '#FDF2E9'], accent: '#FBCFE8' },
  snow: { name: 'Snowfall', description: 'Snow falling quietly at night, near and far', palette: ['#050A16', '#13233F', '#3B5B8C', '#9CC3E6', '#F8FAFC'], accent: '#BFDBFE' },
  ribbons: { name: 'Fluid Ribbons', description: 'Ribbons of colour flowing across, a sheen running along them', palette: ['#070712', '#15102E', '#F43F5E', '#8B5CF6', '#06B6D4'], accent: '#F9A8D4' },
  circuit: { name: 'Data Stream', description: 'Pulses of data racing along a grid of glowing traces', palette: ['#02060A', '#071A26', '#0E7490', '#22D3EE', '#ECFEFF'], accent: '#67E8F9' },
};

/** How the glass is drawn: its corners, the size of a panel pixel, the scene's colours and the edges' accent. */
export interface GlassOptions {
  radius?: number;
  unit?: number;
  palette?: readonly string[];
  accent?: string;
}

/** A card (a tile) and a dial on it, in the canvas's pixels. */
export interface GlassCard {
  x: number;
  y: number;
  w: number;
  h: number;
}
export interface GlassDial {
  cx: number;
  cy: number;
  r: number;
  track: number;
  start: number;
  sweep: number;
}

const MAX_CARDS = 24;
const MAX_DIALS = 12;

const COMMON = `#version 300 es
precision highp float;
uniform vec2 uRes;
uniform float uPhase;
uniform vec3 uPal[5];
out vec4 outColor;
const float TAU = 6.28318530718;

vec4 mod289(vec4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float mod289(float x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 permute(vec4 x) { return mod289(((x * 34.0) + 10.0) * x); }
float permute(float x) { return mod289(((x * 34.0) + 10.0) * x); }
vec4 taylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }
float taylorInvSqrt(float r) { return 1.79284291400159 - 0.85373472095314 * r; }
vec4 grad4(float j, vec4 ip) {
  const vec4 ones = vec4(1.0, 1.0, 1.0, -1.0);
  vec4 p, s;
  p.xyz = floor(fract(vec3(j) * ip.xyz) * 7.0) * ip.z - 1.0;
  p.w = 1.5 - dot(abs(p.xyz), ones.xyz);
  s = vec4(lessThan(p, vec4(0.0)));
  p.xyz = p.xyz + (s.xyz * 2.0 - 1.0) * s.www;
  return p;
}
// Simplex noise in 4D (Ashima Arts, MIT): the 4th dimension turns the loop into change, not drift.
float snoise(vec4 v) {
  const vec4 C = vec4(0.138196601125011, 0.276393202250021, 0.414589803375032, -0.447213595499958);
  vec4 i = floor(v + dot(v, vec4(0.309016994374947451)));
  vec4 x0 = v - i + dot(i, C.xxxx);
  vec4 i0;
  vec3 isX = step(x0.yzw, x0.xxx);
  vec3 isYZ = step(x0.zww, x0.yyz);
  i0.x = isX.x + isX.y + isX.z;
  i0.yzw = 1.0 - isX;
  i0.y += isYZ.x + isYZ.y;
  i0.zw += 1.0 - isYZ.xy;
  i0.z += isYZ.z;
  i0.w += 1.0 - isYZ.z;
  vec4 i3 = clamp(i0, 0.0, 1.0);
  vec4 i2 = clamp(i0 - 1.0, 0.0, 1.0);
  vec4 i1 = clamp(i0 - 2.0, 0.0, 1.0);
  vec4 x1 = x0 - i1 + C.xxxx;
  vec4 x2 = x0 - i2 + C.yyyy;
  vec4 x3 = x0 - i3 + C.zzzz;
  vec4 x4 = x0 + C.wwww;
  i = mod289(i);
  float j0 = permute(permute(permute(permute(i.w) + i.z) + i.y) + i.x);
  vec4 j1 = permute(permute(permute(permute(i.w + vec4(i1.w, i2.w, i3.w, 1.0)) + i.z + vec4(i1.z, i2.z, i3.z, 1.0)) + i.y + vec4(i1.y, i2.y, i3.y, 1.0)) + i.x + vec4(i1.x, i2.x, i3.x, 1.0));
  vec4 ip = vec4(1.0 / 294.0, 1.0 / 49.0, 1.0 / 7.0, 0.0);
  vec4 p0 = grad4(j0, ip);
  vec4 p1 = grad4(j1.x, ip);
  vec4 p2 = grad4(j1.y, ip);
  vec4 p3 = grad4(j1.z, ip);
  vec4 p4 = grad4(j1.w, ip);
  vec4 norm = taylorInvSqrt(vec4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
  p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
  p4 *= taylorInvSqrt(dot(p4, p4));
  vec3 m0 = max(0.6 - vec3(dot(x0, x0), dot(x1, x1), dot(x2, x2)), 0.0);
  vec2 m1 = max(0.6 - vec2(dot(x3, x3), dot(x4, x4)), 0.0);
  m0 = m0 * m0; m1 = m1 * m1;
  return 49.0 * (dot(m0 * m0, vec3(dot(p0, x0), dot(p1, x1), dot(p2, x2))) + dot(m1 * m1, vec2(dot(p3, x3), dot(p4, x4))));
}
// The loop: a circle in the noise's last two dimensions, so the last frame runs into the first.
vec2 loopAt(float r) { float t = uPhase * TAU; return vec2(cos(t), sin(t)) * r; }
float noiseL(vec2 p, float r) { return snoise(vec4(p, loopAt(r))); }
float fbm(vec2 p, float r) {
  float a = 0.5, s = 0.0;
  for (int i = 0; i < 5; i++) { s += a * noiseL(p, r); p = p * 2.03 + vec2(17.1, 9.3); a *= 0.5; }
  return s;
}
float hash12(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec3 ramp(float t) {
  t = clamp(t, 0.0, 1.0) * 4.0;
  int i = int(floor(min(t, 3.999)));
  float f = smoothstep(0.0, 1.0, fract(t));
  return mix(uPal[i], uPal[i + 1], f);
}
vec3 stars(vec2 frag, float density) {
  vec2 cell = floor(frag / 3.0);
  float h = hash12(cell);
  if (h > density) return vec3(0.0);
  float tw = 0.55 + 0.45 * sin(uPhase * TAU * (1.0 + floor(h * 1000.0 - floor(h * 1000.0 / 3.0) * 3.0)) + h * 50.0);
  return vec3(1.0) * tw * pow(hash12(cell + 7.7), 6.0) * 1.4;
}
`;

const SCENES: Record<GenerativeKind, string> = {
  silk: `
vec3 scene(vec2 frag) {
  vec2 p = frag / uRes.y;
  // Large, smooth folds: a few gentle waves bent by slow noise, lit like satin.
  vec2 w = vec2(noiseL(p * 0.55, 0.3), noiseL(p * 0.55 + vec2(3.1, 7.7), 0.3));
  float f = p.x * 0.9 + p.y * 0.55 + 0.6 * w.x + 0.22 * noiseL(p * 1.1 + w, 0.22);
  float folds = sin(f * 6.0);
  float n = 0.5 + 0.5 * folds;
  vec3 base = ramp(0.12 + 0.72 * (0.5 + 0.5 * sin(f * 1.6 + 0.8 + w.y)));
  vec3 c = base * (0.38 + 0.62 * n);
  c += uPal[4] * pow(n, 36.0) * 0.6;
  c += uPal[3] * pow(n, 7.0) * 0.12;
  vec2 uv = frag / uRes;
  c *= 0.82 + 0.18 * smoothstep(0.0, 0.6, uv.y);
  return c;
}`,
  mesh: `
vec3 scene(vec2 frag) {
  float aspect = uRes.x / uRes.y;
  vec2 p = frag / uRes.y;
  p += 0.06 * vec2(noiseL(p * 1.3, 0.3), noiseL(p * 1.3 + 9.0, 0.3));
  float t = uPhase * TAU;
  vec2 c0 = vec2(aspect * (0.18 + 0.10 * cos(t)), 0.30 + 0.14 * sin(t));
  vec2 c1 = vec2(aspect * (0.55 + 0.16 * sin(t)), 0.70 + 0.12 * cos(2.0 * t));
  vec2 c2 = vec2(aspect * (0.85 - 0.10 * cos(t)), 0.35 + 0.18 * sin(2.0 * t));
  vec2 c3 = vec2(aspect * (0.35 + 0.12 * cos(2.0 * t)), 0.85 - 0.10 * sin(t));
  float s = 0.42;
  float w0 = exp(-dot(p - c0, p - c0) / (s * s));
  float w1 = exp(-dot(p - c1, p - c1) / (s * s));
  float w2 = exp(-dot(p - c2, p - c2) / (s * s));
  float w3 = exp(-dot(p - c3, p - c3) / (s * s));
  float base = 0.18;
  vec3 c = (uPal[0] * base + uPal[1] * w0 + uPal[2] * w1 + uPal[3] * w2 + uPal[4] * w3) / (base + w0 + w1 + w2 + w3);
  return c * (0.85 + 0.15 * noiseL(p * 3.0, 0.2));
}`,
  aurora: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = frag / uRes.y;
  vec3 c = mix(uPal[1], uPal[0], pow(uv.y, 0.6));
  c += stars(frag, 0.03) * smoothstep(0.25, 0.9, uv.y);
  for (int k = 0; k < 3; k++) {
    float fk = float(k);
    // A curtain: a soft, wavy lower edge glowing brightest, rays rising from it and fading, green turning violet up high.
    float edge = 0.26 + 0.15 * fk + 0.10 * noiseL(vec2(p.x * 0.6 + fk * 3.3, fk), 0.35) + 0.03 * sin(p.x * 2.5 + uPhase * TAU * (fk + 1.0));
    float h = uv.y - edge;
    float curtain = smoothstep(-0.07, 0.01, h) * exp(-max(h, 0.0) * (2.2 + fk));
    float rays = 0.35 + 0.65 * pow(0.5 + 0.5 * noiseL(vec2(p.x * 11.0 + fk * 7.0, fk * 3.0), 0.6), 2.2);
    vec3 col = mix(uPal[2], uPal[3], 0.5 + 0.5 * noiseL(vec2(p.x * 0.9, fk + 4.0), 0.3));
    col = mix(col, uPal[4], clamp(h * 2.4, 0.0, 1.0));
    c += col * curtain * rays * (0.95 - fk * 0.2);
  }
  // Its glow on the snow below.
  c += uPal[2] * 0.12 * smoothstep(0.35, 0.0, uv.y);
  return c;
}`,
  nebula: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = frag / uRes.y * 0.55;
  // Gas warped by gas, slowly: large soft clouds with a bright, warm heart.
  vec2 q = vec2(fbm(p, 0.22), fbm(p + vec2(5.2, 1.3), 0.22));
  float r = fbm(p + 1.8 * q + vec2(1.7, 9.2), 0.25);
  float density = smoothstep(-0.25, 0.4, r);
  vec3 c = mix(uPal[0], uPal[1], smoothstep(-0.4, 0.4, q.x) * 0.9 + 0.1);
  c = mix(c, uPal[2], density * 0.9);
  c += uPal[3] * pow(density, 2.0) * 0.6;
  c += uPal[4] * pow(density, 4.0) * 0.55;
  c *= 0.75 + 0.35 * smoothstep(-0.3, 0.4, q.y);
  c += stars(frag, 0.03) * (1.0 - density * 0.8);
  return c;
}`,
  caustics: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = mod(frag / uRes.y * TAU * 0.9, TAU) - 250.0;
  float t = uPhase * TAU;
  vec2 i = p;
  float c = 1.0;
  float inten = 0.005;
  for (int n = 0; n < 5; n++) {
    float k = n == 0 ? -2.0 : n == 1 ? -1.0 : n == 2 ? 1.0 : n == 3 ? 1.0 : 2.0;
    float tt = t * k + float(n) * 0.7;
    i = p + vec2(cos(tt - i.x) + sin(tt + i.y), sin(tt - i.y) + cos(tt + i.x));
    c += 1.0 / length(vec2(p.x / (sin(i.x + tt) / inten), p.y / (cos(i.y + tt) / inten)));
  }
  c /= 5.0;
  c = 1.17 - pow(c, 1.4);
  float light = clamp(pow(abs(c), 8.0), 0.0, 1.0);
  vec3 col = mix(uPal[1], uPal[0], uv.y * 0.9);
  col = mix(col, uPal[2], 0.3 * (0.5 + 0.5 * noiseL(frag / uRes.y * 1.2, 0.3)));
  col += mix(uPal[3], uPal[4], light) * light * 0.85;
  return col;
}`,
  bokeh: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  float aspect = uRes.x / uRes.y;
  vec2 p = vec2(uv.x * aspect, uv.y);
  vec3 c = mix(uPal[0], uPal[1], smoothstep(0.0, 1.0, uv.y * 0.8 + 0.1 * noiseL(p, 0.2)));
  for (int i = 0; i < 46; i++) {
    float fi = float(i);
    float h1 = hash12(vec2(fi, 1.3));
    float h2 = hash12(vec2(fi, 7.1));
    float h3 = hash12(vec2(fi, 3.7));
    float depth = h3;
    float speed = 1.0 + floor(h1 * 2.0);
    float y = fract(h2 + uPhase * speed * 0.5);
    float x = h1 * aspect + 0.03 * sin(uPhase * TAU + fi);
    vec2 o = vec2(x, -0.15 + y * 1.3);
    float r = mix(0.035, 0.16, depth);
    float d = length(p - o);
    float soft = mix(0.25, 0.75, depth);
    float disc = smoothstep(r, r * (1.0 - soft), d);
    float rim = smoothstep(r, r * 0.92, d) - smoothstep(r * 0.92, r * 0.8, d);
    vec3 col = uPal[2 + int(mod(fi, 3.0))];
    float a = mix(0.55, 0.16, depth);
    c += col * (disc * a + rim * a * 0.35 * (1.0 - depth));
  }
  return c;
}`,
  topo: `
vec3 scene(vec2 frag) {
  vec2 p = frag / uRes.y * 1.6;
  float f = fbm(p * 0.7, 0.25) * 0.5 + 0.5;
  vec3 c = mix(uPal[0], uPal[2], f * 0.8);
  float k = f * 18.0;
  float line = abs(fract(k) - 0.5) / fwidth(k);
  float l = 1.0 - smoothstep(0.0, 1.3, line);
  float major = step(0.5, fract(floor(k) / 5.0 + 0.1)) < 0.5 ? 1.0 : 0.45;
  c = mix(c, mix(uPal[3], uPal[4], f), l * 0.65 * major);
  c *= 0.7 + 0.3 * smoothstep(0.2, 0.9, f);
  return c;
}`,
  prism: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = frag / uRes.y;
  float t = uPhase * TAU;
  vec3 c = mix(uPal[1], uPal[0], uv.y);
  // Two lights, their beams split into colour, sweeping slowly; a soft wash of colour where they cross.
  vec3 beams = vec3(0.0);
  for (int s = 0; s < 2; s++) {
    float fs = float(s);
    vec2 o = s == 0 ? vec2(-0.25, 1.3) : vec2(uRes.x / uRes.y + 0.25, 1.25);
    vec2 d = p - o;
    float a = atan(d.y, d.x) * (s == 0 ? 1.0 : -1.0);
    float fall = exp(-length(d) * 0.45);
    for (int k = 0; k < 3; k++) {
      float fk = float(k);
      float base = a * 5.0 + 0.7 * sin(t + fk * 2.1 + fs * 1.4) + fk * 1.9;
      vec3 split = vec3(pow(0.5 + 0.5 * cos(base + 0.07), 10.0), pow(0.5 + 0.5 * cos(base), 10.0), pow(0.5 + 0.5 * cos(base - 0.07), 10.0));
      beams += split * fall * (0.55 + 0.45 * sin(t * (fk + 1.0) + fk + fs));
    }
  }
  c += beams * mix(uPal[3], uPal[4], 0.45) * 0.75;
  c += uPal[2] * 0.35 * exp(-length(p - vec2(0.5 * uRes.x / uRes.y + 0.3 * cos(t), 0.4 + 0.1 * sin(2.0 * t))) * 1.6);
  return c;
}`,
  ocean: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = frag / uRes.y;
  float aspect = uRes.x / uRes.y;
  float t = uPhase * TAU;
  float hz = 0.46;
  vec2 sun = vec2(aspect * 0.62, hz + 0.09);
  vec3 c;
  if (uv.y > hz) {
    // The sky at dusk: warm at the horizon, deep blue above, a low sun and thin clouds.
    float s = (uv.y - hz) / (1.0 - hz);
    c = mix(uPal[3], uPal[1], pow(s, 0.5));
    c = mix(c, uPal[0], smoothstep(0.5, 1.0, s));
    float d = length(p - sun);
    c += uPal[4] * exp(-d * 9.0) * 0.9 + uPal[3] * exp(-d * 2.5) * 0.25;
    c += uPal[4] * smoothstep(0.055, 0.05, d);
    c = mix(c, uPal[2] * 0.8, 0.25 * smoothstep(0.2, 0.7, noiseL(vec2(p.x * 1.5, s * 6.0), 0.2)) * smoothstep(0.0, 0.3, s));
  } else {
    // The sea: swells in perspective, the sun's path glittering on them.
    float depth = (hz - uv.y) / hz;
    float z = 1.0 / (depth + 0.03);
    float x = p.x * z * 0.25;
    float w = sin(x * 3.0 + z * 0.9 + t * 2.0) * 0.5 + sin(x * 7.0 - z * 1.7 + t * 3.0) * 0.25 + sin(x * 13.0 + z * 2.3 - t * 4.0) * 0.12 + noiseL(vec2(x * 2.0, z * 1.5), 0.4) * 0.4;
    c = mix(uPal[3] * 0.5, uPal[1], smoothstep(0.0, 0.6, depth));
    c = mix(c, uPal[0], smoothstep(0.4, 1.0, depth));
    float path = exp(-pow((p.x - sun.x) * (2.0 + depth * 1.5), 2.0) * 3.0);
    float glint = pow(max(0.0, w * 0.5 + 0.5), 6.0);
    c += uPal[4] * path * glint * (1.0 - depth * 0.6) * 0.9;
    c += uPal[2] * 0.15 * (w * 0.5 + 0.5) * (1.0 - depth);
  }
  return c;
}`,
  lava: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  float aspect = uRes.x / uRes.y;
  vec2 p = vec2(uv.x * aspect, uv.y);
  float t = uPhase * TAU;
  // Warm wax rising and sinking: blobs that merge (each rises a whole number of times a loop, off screen when it wraps).
  float f = 0.0;
  for (int i = 0; i < 14; i++) {
    float fi = float(i);
    float h1 = hash12(vec2(fi, 2.1));
    float h2 = hash12(vec2(fi, 5.3));
    float h3 = hash12(vec2(fi, 8.7));
    float sp = 1.0 + floor(h3 * 2.0);
    float y = -0.35 + fract(h2 + uPhase * sp) * 1.7;
    float x = aspect * (0.08 + 0.84 * h1) + 0.06 * sin(t * sp + fi);
    float r = 0.07 + 0.09 * h3;
    vec2 d = p - vec2(x, y);
    d.x *= 1.0 + 0.15 * sin(t + fi);
    f += r * r / max(dot(d, d), 1e-4);
  }
  float m = smoothstep(0.9, 1.3, f);
  float edge = smoothstep(0.7, 1.0, f) - m;
  vec3 bg = mix(uPal[0], uPal[1], uv.y * 0.8 + 0.2 * noiseL(p * 1.5, 0.2));
  vec3 wax = mix(uPal[2], uPal[3], clamp((f - 1.0) * 0.6, 0.0, 1.0));
  wax = mix(wax, uPal[4], pow(clamp((f - 1.3) * 0.25, 0.0, 1.0), 1.5));
  vec3 c = mix(bg, wax, m);
  c += uPal[3] * max(edge, 0.0) * 0.5;
  c += uPal[2] * 0.25 * smoothstep(0.2, 0.9, f) * (1.0 - m);
  return c;
}`,
  ink: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = frag / uRes.y * 0.9;
  // Ink spreading in still water: noise warped by noise warped by noise.
  vec2 q = vec2(fbm(p, 0.18), fbm(p + vec2(4.2, 1.7), 0.18));
  vec2 r = vec2(fbm(p + 3.0 * q + vec2(1.7, 9.2), 0.22), fbm(p + 3.0 * q + vec2(8.3, 2.8), 0.22));
  float f = fbm(p + 2.5 * r, 0.25);
  float ink = smoothstep(-0.1, 0.6, f);
  vec3 c = mix(uPal[0], uPal[1], uv.y * 0.5 + 0.3);
  c = mix(c, uPal[2], ink * 0.85);
  c = mix(c, uPal[3], smoothstep(0.3, 0.9, length(r)) * ink * 0.7);
  c += uPal[4] * pow(smoothstep(0.4, 0.9, f), 3.0) * 0.5;
  return c;
}`,
  marble: `
vec3 scene(vec2 frag) {
  vec2 p = frag / uRes.y;
  // Black marble with veins of gold, slowly flowing.
  vec2 q = vec2(fbm(p * 0.8, 0.2), fbm(p * 0.8 + vec2(5.2, 1.3), 0.2));
  float v = sin((p.x * 1.2 + p.y * 0.8) * 5.0 + 4.0 * q.x + 2.0 * q.y);
  float vein = pow(1.0 - abs(v), 18.0);
  float fine = pow(1.0 - abs(sin((p.x - p.y) * 14.0 + 6.0 * q.y)), 40.0);
  vec3 c = mix(uPal[0], uPal[1], 0.5 + 0.5 * q.x);
  c = mix(c, uPal[2], 0.25 * smoothstep(-0.2, 0.5, q.y));
  c += uPal[4] * vein * 0.9 + uPal[3] * fine * 0.35;
  return c;
}`,
  foil: `
vec3 scene(vec2 frag) {
  vec2 p = frag / uRes.y;
  float t = uPhase * TAU;
  // Holographic foil: colours of a thin film shifting with its thickness, a shine sweeping across it.
  float th = fbm(p * 0.9, 0.25) * 1.4 + p.x * 0.35 + p.y * 0.2;
  vec3 film = 0.5 + 0.5 * cos(TAU * (th * 1.3 + vec3(0.0, 0.33, 0.67)));
  // Pastel, not a rainbow: the film's colour softened toward grey, over a dark base, brighter only where it catches the light.
  film = mix(vec3(dot(film, vec3(0.333))), film, 0.5);
  float catchLight = 0.35 + 0.65 * smoothstep(-0.3, 0.6, noiseL(p * 0.7 + 3.0, 0.25));
  vec3 c = mix(uPal[0], mix(uPal[2], film, 0.6), 0.55 * catchLight);
  float sweep = exp(-pow((p.x - p.y * 0.6) - (uRes.x / uRes.y) * (0.5 + 0.6 * sin(t)), 2.0) * 4.0);
  c += mix(film, uPal[4], 0.5) * sweep * 0.3;
  return c * 0.9;
}`,
  fireflies: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  float aspect = uRes.x / uRes.y;
  vec2 p = vec2(uv.x * aspect, uv.y);
  float t = uPhase * TAU;
  // A summer night in the woods: dark leaves, and fireflies wandering and blinking.
  vec3 c = mix(uPal[0], uPal[1], smoothstep(0.0, 1.0, uv.y));
  float leaves = fbm(p * 2.2, 0.15);
  c = mix(c, uPal[2] * 0.6, smoothstep(0.1, 0.6, leaves) * 0.5 * (1.0 - uv.y * 0.5));
  for (int i = 0; i < 40; i++) {
    float fi = float(i);
    float h1 = hash12(vec2(fi, 1.1));
    float h2 = hash12(vec2(fi, 4.4));
    float h3 = hash12(vec2(fi, 9.9));
    vec2 o = vec2(h1 * aspect + 0.08 * sin(t * (1.0 + floor(h2 * 2.0)) + fi), h2 * 0.9 + 0.05 + 0.06 * cos(t * (1.0 + floor(h3 * 2.0)) + fi * 1.7));
    float blink = pow(0.5 + 0.5 * sin(t * (1.0 + floor(h3 * 3.0)) + fi * 2.3), 3.0);
    float d = length(p - o);
    c += uPal[3] * exp(-d * d * 1800.0) * blink * 1.2 + uPal[4] * exp(-d * 60.0) * blink * 0.12;
  }
  return c;
}`,
  warp: `
vec3 scene(vec2 frag) {
  vec2 p = (frag - uRes * 0.5) / uRes.y;
  float r = length(p);
  float a = atan(p.y, p.x);
  // Through hyperspace: stars streaking out of the centre (each one a whole number of times a loop).
  vec3 c = uPal[0] + uPal[1] * 0.6 * exp(-r * 1.6);
  // A tunnel of faint light around the way ahead.
  c += uPal[3] * 0.12 * (0.5 + 0.5 * noiseL(vec2(a * 2.0, log(r + 0.05) * 2.0), 0.4)) * smoothstep(0.05, 0.6, r);
  for (int l = 0; l < 5; l++) {
    float fl = float(l);
    float sectors = 70.0 + fl * 37.0;
    float id = floor((a / TAU + 0.5) * sectors);
    float h = hash12(vec2(id, fl * 13.0 + 1.0));
    if (h > 0.8) continue;
    float z = fract(h * 7.0 + uPhase * (1.0 + mod(fl, 3.0)));
    float pos = pow(z, 2.0) * 1.4;
    float len = 0.03 + 0.35 * z * z;
    float da = abs(fract((a / TAU + 0.5) * sectors) - 0.5);
    float line = smoothstep(0.25, 0.0, da);
    float seg = smoothstep(pos - len, pos, r) * smoothstep(pos + 0.012, pos, r);
    c += mix(uPal[2], uPal[4], clamp(h * 1.4, 0.0, 1.0)) * line * seg * (0.25 + z) * 1.5;
  }
  c += uPal[4] * 0.35 * exp(-r * 9.0) + uPal[3] * 0.25 * exp(-r * 4.0);
  return c;
}`,
  galaxy: `
vec3 scene(vec2 frag) {
  vec2 p = (frag - uRes * 0.5) / uRes.y;
  p.y *= 1.6;
  float t = uPhase * TAU;
  float r = length(p);
  float a = atan(p.y, p.x);
  // A spiral galaxy turning: two arms (half a turn a loop), dust, a bright core, stars.
  float arms = 0.5 + 0.5 * sin(2.0 * (a - 3.2 * log(r + 0.02)) + t);
  float n = noiseL(p * 4.0, 0.3) * 0.5 + 0.5;
  float density = pow(arms, 3.0) * exp(-r * 2.2) * (0.6 + 0.6 * n);
  vec3 c = uPal[0] + stars(frag, 0.025);
  c += mix(uPal[2], uPal[3], arms) * density * 1.6;
  c += uPal[4] * exp(-r * 9.0) * 1.1;
  c += uPal[1] * exp(-r * 1.5) * 0.35;
  return c;
}`,
  clouds: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = frag / uRes.y;
  // The sky at the blue hour, soft clouds forming and drifting apart.
  vec3 sky = mix(uPal[3], uPal[1], pow(uv.y, 0.7));
  sky = mix(sky, uPal[0], smoothstep(0.6, 1.0, uv.y));
  float cl = fbm(vec2(p.x * 0.8, p.y * 1.6), 0.3);
  float cover = smoothstep(-0.05, 0.45, cl);
  vec3 cloud = mix(uPal[2], uPal[4], smoothstep(0.0, 0.6, cl + uv.y * 0.3));
  return mix(sky, cloud, cover * 0.85);
}`,
  snow: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = frag / uRes.y;
  float t = uPhase * TAU;
  // Snow falling at night in four layers (each falls a whole number of its cells a loop, its pattern repeating).
  vec3 c = mix(uPal[1], uPal[0], uv.y);
  c += uPal[2] * 0.25 * exp(-uv.y * 3.0);
  for (int l = 0; l < 4; l++) {
    float fl = float(l);
    float s = 0.18 - fl * 0.035;
    float rows = 6.0 + fl * 4.0;
    vec2 q = p / s;
    q.y += uPhase * rows;
    vec2 id = floor(q);
    vec2 f = fract(q) - 0.5;
    vec2 hid = vec2(id.x, mod(id.y, rows));
    float h = hash12(hid + fl * 17.0);
    if (h < 0.55) {
      vec2 o = vec2(hash12(hid + 3.1) - 0.5, hash12(hid + 7.7) - 0.5) * 0.6;
      o.x += 0.15 * sin(t + h * 20.0);
      float rad = 0.07 + 0.05 * h;
      float d = length(f - o);
      c += uPal[4] * smoothstep(rad, rad * 0.25, d) * (0.85 - fl * 0.17);
    }
  }
  return c;
}`,
  ribbons: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = frag / uRes.y;
  float t = uPhase * TAU;
  // Soft ribbons of colour flowing across, a sheen running along them.
  vec3 c = mix(uPal[0], uPal[1], uv.y * 0.6);
  for (int i = 0; i < 4; i++) {
    float fi = float(i);
    float y = 0.22 + fi * 0.17 + 0.12 * sin(p.x * (1.2 + fi * 0.35) + t * (fi < 2.0 ? 1.0 : -1.0) + fi * 1.7) + 0.05 * noiseL(vec2(p.x * 0.8, fi), 0.3);
    float d = uv.y - y;
    float band = exp(-d * d * (260.0 - fi * 40.0));
    float sheen = 0.5 + 0.5 * sin(p.x * 3.0 - t * 2.0 + fi);
    c += mix(uPal[2 + int(mod(fi, 3.0))], vec3(1.0), 0.15 * sheen) * band * 0.55;
  }
  return c;
}`,
  circuit: `
vec3 scene(vec2 frag) {
  vec2 uv = frag / uRes;
  vec2 p = frag / uRes.y;
  // A grid of traces with pulses of data running along them (a whole number of runs a loop).
  vec3 c = mix(uPal[0], uPal[1], 0.3 + 0.4 * uv.y);
  float g = 0.08;
  vec2 cell = p / g;
  vec2 id = floor(cell);
  vec2 f = fract(cell);
  float lw = fwidth(cell.x) * 1.2;
  float row = 1.0 - smoothstep(0.0, lw, min(f.y, 1.0 - f.y));
  float col = 1.0 - smoothstep(0.0, lw, min(f.x, 1.0 - f.x));
  c += uPal[2] * (row + col) * 0.1;
  float hr = hash12(vec2(floor(cell.y + 0.5), 3.3));
  float hc = hash12(vec2(floor(cell.x + 0.5), 6.1));
  float ur = fract((hr > 0.5 ? 1.0 : -1.0) * p.x * 0.8 + hr * 5.0 - uPhase * (1.0 + floor(hr * 3.0)));
  float uc = fract((hc > 0.5 ? 1.0 : -1.0) * p.y * 0.8 + hc * 5.0 - uPhase * (1.0 + floor(hc * 3.0)));
  float tr = pow(ur, 14.0) * step(hr, 0.55);
  float tc = pow(uc, 14.0) * step(hc, 0.4);
  c += mix(uPal[3], uPal[4], tr) * tr * row * 1.3;
  c += mix(uPal[3], uPal[4], tc) * tc * col * 1.3;
  vec2 node = abs(f - 0.5);
  float dot2 = smoothstep(0.12, 0.05, length(node - 0.5));
  c += uPal[3] * dot2 * 0.25;
  return c;
}`,
};

const SCENE_MAIN = `
void main() { outColor = vec4(scene(gl_FragCoord.xy), 1.0); }
`;

const VERTEX = `#version 300 es
in vec2 aPos;
void main() { gl_Position = vec4(aPos, 0.0, 1.0); }
`;

const BLUR = `#version 300 es
precision highp float;
uniform sampler2D uTex;
uniform vec2 uDir;
uniform vec2 uRes;
out vec4 outColor;
void main() {
  vec2 uv = gl_FragCoord.xy / uRes;
  float w[7] = float[](0.1964825501511404, 0.2969069646728344, 0.09447039785044732, 0.010381362401148057, 0.0, 0.0, 0.0);
  vec3 c = texture(uTex, uv).rgb * w[0];
  for (int i = 1; i < 4; i++) {
    vec2 o = uDir * (float(i) * 1.5) / uRes;
    c += texture(uTex, uv + o).rgb * w[i];
    c += texture(uTex, uv - o).rgb * w[i];
  }
  outColor = vec4(c, 1.0);
}
`;

// A picture as the scene, covering the canvas (cropped to its shape, centred).
const PICTURE = `#version 300 es
precision highp float;
uniform sampler2D uPic;
uniform vec2 uRes;
uniform vec2 uScale;
out vec4 outColor;
void main() {
  vec2 uv = gl_FragCoord.xy / uRes;
  outColor = vec4(texture(uPic, (uv - 0.5) * uScale + 0.5).rgb, 1.0);
}
`;

const COMPOSE = `#version 300 es
precision highp float;
uniform sampler2D uScene;
uniform sampler2D uBlur;
uniform vec2 uRes;
uniform float uPhase;
uniform vec3 uAccent;
uniform float uRadius;
uniform float uUnit;
uniform int uCards;
uniform vec4 uCard[${MAX_CARDS}];
uniform int uDials;
uniform vec4 uDial[${MAX_DIALS}];
uniform vec2 uDialAngle[${MAX_DIALS}];
out vec4 outColor;
const float TAU = 6.28318530718;
float hash12(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
float sdBox(vec2 p, vec2 b, float r) { vec2 q = abs(p) - b + r; return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r; }
void main() {
  // Canvas pixels with y down (as the panel's layout), and the texture's uv.
  vec2 px = vec2(gl_FragCoord.x, uRes.y - gl_FragCoord.y);
  vec2 uv = gl_FragCoord.xy / uRes;
  vec3 c = texture(uScene, uv).rgb;
  // A soft shadow under every card, then each card.
  float shade = 0.0;
  for (int i = 0; i < ${MAX_CARDS}; i++) {
    if (i >= uCards) break;
    vec4 k = uCard[i];
    vec2 hs = k.zw * 0.5;
    float d = sdBox(px - (k.xy + hs) - vec2(0.0, 6.0 * uUnit), hs, uRadius);
    shade = max(shade, (1.0 - smoothstep(0.0, 22.0 * uUnit, d)) * 0.38);
  }
  c *= 1.0 - shade;
  for (int i = 0; i < ${MAX_CARDS}; i++) {
    if (i >= uCards) break;
    vec4 k = uCard[i];
    vec2 hs = k.zw * 0.5;
    vec2 centre = k.xy + hs;
    float d = sdBox(px - centre, hs, uRadius);
    float inside = 1.0 - smoothstep(-0.75, 0.75, d);
    if (inside <= 0.0 && d > 2.0 * uUnit) continue;
    // Frosted: the scene blurred, a little lighter and richer, under a dark veil and a sheen from the top.
    vec3 g = texture(uBlur, uv).rgb;
    float l = dot(g, vec3(0.299, 0.587, 0.114));
    g = mix(vec3(l), g, 1.25) * 1.06 + 0.02;
    float ty = clamp((px.y - k.y) / max(1.0, k.w), 0.0, 1.0);
    // The veil darker where the scene behind is bright, so light text always reads on it.
    float veil = 0.28 + 0.42 * smoothstep(0.3, 0.8, l);
    vec3 glass = mix(g, vec3(0.015, 0.02, 0.04), veil) + vec3(1.0) * 0.07 * pow(1.0 - ty, 3.0);
    c = mix(c, glass, inside);
    // The edge: light from the top left, and a highlight travelling round the card.
    float edge = 1.0 - smoothstep(0.0, 1.1 * uUnit, abs(d + 0.6 * uUnit));
    vec2 dir = normalize(px - centre + 1e-4);
    float sky = 0.28 + 0.42 * clamp(dot(dir, normalize(vec2(-0.55, -0.85))), 0.0, 1.0);
    float ang = atan(dir.y, dir.x);
    float run = pow(max(0.0, cos(ang - uPhase * TAU - float(i) * 0.9)), 30.0);
    c = mix(c, mix(vec3(1.0), uAccent, 0.35 + 0.65 * run), edge * clamp(sky + run * 0.9, 0.0, 1.0));
    // A fine inner highlight along the top.
    float top = (1.0 - smoothstep(0.0, 1.0 * uUnit, abs(px.y - (k.y + 1.6 * uUnit)))) * step(k.x + uRadius, px.x) * step(px.x, k.x + k.z - uRadius);
    c += vec3(1.0) * top * 0.18 * inside;
  }
  // Under each dial: an etched track and fine ticks along its sweep.
  for (int i = 0; i < ${MAX_DIALS}; i++) {
    if (i >= uDials) break;
    vec4 dl = uDial[i];
    vec2 q = px - dl.xy;
    float rr = length(q);
    float track = dl.w;
    float mid = dl.z - track * 0.5;
    float groove = 1.0 - smoothstep(track * 0.5 + 2.0 * uUnit, track * 0.5 + 3.5 * uUnit, abs(rr - mid));
    c *= 1.0 - groove * 0.22;
    float rim = 1.0 - smoothstep(0.0, 0.9 * uUnit, abs(rr - (dl.z + 3.0 * uUnit)));
    float ang = degrees(atan(q.y, q.x));
    float rel = mod(ang - uDialAngle[i].x + 720.0, 360.0);
    float within = step(rel, uDialAngle[i].y);
    c += uAccent * rim * 0.35 * within;
    // Fine ticks every 6 degrees, longer every 30, just outside the dial.
    float m6 = mod(rel, 6.0);
    float arc = min(m6, 6.0 - m6) * 0.017453 * rr;
    float tick = (1.0 - smoothstep(0.45 * uUnit, 1.0 * uUnit, arc)) * within;
    float m30 = mod(rel, 30.0);
    bool major = min(m30, 30.0 - m30) < 0.5;
    float ring = step(dl.z + 5.0 * uUnit, rr) * step(rr, dl.z + (major ? 13.0 : 9.0) * uUnit);
    c += vec3(1.0) * tick * ring * (major ? 0.45 : 0.25);
  }
  // A whisper of grain, so gradients never band.
  c += (hash12(gl_FragCoord.xy + uPhase * 911.0) - 0.5) / 255.0 * 2.0;
  outColor = vec4(c, 1.0);
}
`;

function hexToRgb(hex: string): [number, number, number] {
  const n = Number.parseInt(hex.slice(1), 16);
  return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
}

interface Target {
  fb: WebGLFramebuffer;
  tex: WebGLTexture;
  w: number;
  h: number;
}

/**
 * Draws a generated scene and its glass cards into a canvas. One renderer can draw many panels in turn (the editor
 * shares one, as a page may hold only a few WebGL contexts).
 */
export class GenerativeRenderer {
  readonly canvas: HTMLCanvasElement | OffscreenCanvas;
  private readonly gl: WebGL2RenderingContext;
  private readonly scenes = new Map<GenerativeKind, WebGLProgram>();
  private readonly blur: WebGLProgram;
  private readonly compose: WebGLProgram;
  private readonly picture: WebGLProgram;
  private readonly pictureTexture: WebGLTexture;
  private readonly quad: WebGLBuffer;
  /** Render targets by size: a shared renderer draws previews of a few sizes in turn (the newest kept, at most 8). */
  private readonly sizes = new Map<string, { scene: Target; half: Target; half2: Target }>();
  private targets: { scene: Target; half: Target; half2: Target } | null = null;

  constructor(canvas?: HTMLCanvasElement | OffscreenCanvas) {
    this.canvas = canvas ?? (typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(16, 16) : document.createElement('canvas'));
    const gl = this.canvas.getContext('webgl2', { premultipliedAlpha: false, preserveDrawingBuffer: true, antialias: false }) as WebGL2RenderingContext | null;
    if (!gl) throw new Error('This browser cannot draw generated backgrounds (no WebGL2).');
    this.gl = gl;
    this.quad = gl.createBuffer()!;
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    this.blur = this.program(BLUR);
    this.compose = this.program(COMPOSE);
    this.picture = this.program(PICTURE);
    this.pictureTexture = gl.createTexture()!;
  }

  private static support: boolean | undefined;
  /** Whether this browser can draw them (asked once: the context made to ask is let go at once). */
  static supported(): boolean {
    if (GenerativeRenderer.support === undefined) {
      try {
        const gl = document.createElement('canvas').getContext('webgl2');
        GenerativeRenderer.support = !!gl;
        gl?.getExtension('WEBGL_lose_context')?.loseContext();
      } catch {
        GenerativeRenderer.support = false;
      }
    }
    return GenerativeRenderer.support;
  }

  private program(fragment: string): WebGLProgram {
    const gl = this.gl;
    const make = (type: number, source: string) => {
      const shader = gl.createShader(type)!;
      gl.shaderSource(shader, source);
      gl.compileShader(shader);
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader) ?? 'shader');
      return shader;
    };
    const program = gl.createProgram()!;
    gl.attachShader(program, make(gl.VERTEX_SHADER, VERTEX));
    gl.attachShader(program, make(gl.FRAGMENT_SHADER, fragment));
    gl.bindAttribLocation(program, 0, 'aPos');
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? 'program');
    return program;
  }

  private sceneProgram(kind: GenerativeKind): WebGLProgram {
    let p = this.scenes.get(kind);
    if (!p) {
      p = this.program(COMMON + SCENES[kind] + SCENE_MAIN);
      this.scenes.set(kind, p);
    }
    return p;
  }

  private target(w: number, h: number): Target {
    const gl = this.gl;
    const tex = gl.createTexture()!;
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    const fb = gl.createFramebuffer()!;
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
    return { fb, tex, w, h };
  }

  private ensure(w: number, h: number) {
    const gl = this.gl;
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
    const key = `${w}x${h}`;
    let targets = this.sizes.get(key);
    if (targets) {
      this.sizes.delete(key);
    } else {
      if (this.sizes.size >= 8) {
        const [oldest, old] = this.sizes.entries().next().value!;
        this.sizes.delete(oldest);
        for (const t of Object.values(old)) {
          gl.deleteFramebuffer(t.fb);
          gl.deleteTexture(t.tex);
        }
      }
      const hw = Math.max(1, Math.round(w / 4));
      const hh = Math.max(1, Math.round(h / 4));
      targets = { scene: this.target(w, h), half: this.target(hw, hh), half2: this.target(hw, hh) };
    }
    this.sizes.set(key, targets);
    this.targets = targets;
    return targets;
  }

  private draw(program: WebGLProgram, target: Target | null, w: number, h: number) {
    const gl = this.gl;
    gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.fb : null);
    gl.viewport(0, 0, w, h);
    gl.useProgram(program);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  /**
   * Draws one frame: the scene at `phase` (0–1 of its loop) and the cards and dials (in canvas pixels, y down) over it.
   * `unit` is the size of a pixel of the panel at 100 % (edges and ticks follow it).
   */
  render(kind: GenerativeKind, width: number, height: number, phase: number, cards: readonly GlassCard[], dials: readonly GlassDial[], options: GlassOptions = {}) {
    const gl = this.gl;
    const w = Math.max(1, Math.round(width));
    const h = Math.max(1, Math.round(height));
    const t = this.ensure(w, h);
    const info = GENERATIVE[kind];
    const palette = (options.palette ?? info.palette).map(hexToRgb).flat();
    const scene = this.sceneProgram(kind);
    gl.useProgram(scene);
    gl.uniform2f(gl.getUniformLocation(scene, 'uRes'), w, h);
    gl.uniform1f(gl.getUniformLocation(scene, 'uPhase'), phase);
    gl.uniform3fv(gl.getUniformLocation(scene, 'uPal'), palette);
    this.draw(scene, t.scene, w, h);
    this.glass(w, h, phase, cards, dials, { ...options, accent: options.accent ?? info.accent });
  }

  /**
   * Draws a picture (a still, or an animation's frame) covering the canvas, and the cards and dials over it, the glass
   * showing the picture blurred behind each card; `phase` moves the light round the cards' edges.
   */
  renderPicture(source: TexImageSource, sourceWidth: number, sourceHeight: number, width: number, height: number, phase: number, cards: readonly GlassCard[], dials: readonly GlassDial[], options: GlassOptions = {}) {
    const gl = this.gl;
    const w = Math.max(1, Math.round(width));
    const h = Math.max(1, Math.round(height));
    const t = this.ensure(w, h);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, this.pictureTexture);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    // Cover: the picture's shape against the canvas's decides which way it is cropped.
    const pictureAspect = sourceWidth / Math.max(1, sourceHeight);
    const canvasAspect = w / h;
    const scale = pictureAspect > canvasAspect ? [canvasAspect / pictureAspect, 1] : [1, pictureAspect / canvasAspect];
    const program = this.picture;
    gl.useProgram(program);
    gl.uniform1i(gl.getUniformLocation(program, 'uPic'), 0);
    gl.uniform2f(gl.getUniformLocation(program, 'uRes'), w, h);
    gl.uniform2f(gl.getUniformLocation(program, 'uScale'), scale[0]!, scale[1]!);
    this.draw(program, t.scene, w, h);
    this.glass(w, h, phase, cards, dials, { ...options, accent: options.accent ?? '#FFFFFF' });
  }

  private glass(w: number, h: number, phase: number, cards: readonly GlassCard[], dials: readonly GlassDial[], options: GlassOptions & { accent: string }) {
    const gl = this.gl;
    const t = this.targets!;
    // Its blur, at a quarter size: down, across, then up and down again.
    const blur = this.blur;
    gl.useProgram(blur);
    gl.activeTexture(gl.TEXTURE0);
    gl.uniform1i(gl.getUniformLocation(blur, 'uTex'), 0);
    gl.uniform2f(gl.getUniformLocation(blur, 'uRes'), t.half.w, t.half.h);
    for (let pass = 0; pass < 3; pass++) {
      gl.bindTexture(gl.TEXTURE_2D, pass === 0 ? t.scene.tex : t.half2.tex);
      gl.uniform2f(gl.getUniformLocation(blur, 'uDir'), 1, 0);
      this.draw(blur, t.half, t.half.w, t.half.h);
      gl.bindTexture(gl.TEXTURE_2D, t.half.tex);
      gl.uniform2f(gl.getUniformLocation(blur, 'uDir'), 0, 1);
      this.draw(blur, t.half2, t.half2.w, t.half2.h);
    }

    // The cards over it, to the canvas.
    const compose = this.compose;
    gl.useProgram(compose);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, t.scene.tex);
    gl.uniform1i(gl.getUniformLocation(compose, 'uScene'), 0);
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, t.half2.tex);
    gl.uniform1i(gl.getUniformLocation(compose, 'uBlur'), 1);
    gl.uniform2f(gl.getUniformLocation(compose, 'uRes'), w, h);
    gl.uniform1f(gl.getUniformLocation(compose, 'uPhase'), phase);
    gl.uniform3fv(gl.getUniformLocation(compose, 'uAccent'), hexToRgb(options.accent));
    const unit = options.unit ?? 1;
    gl.uniform1f(gl.getUniformLocation(compose, 'uUnit'), unit);
    gl.uniform1f(gl.getUniformLocation(compose, 'uRadius'), (options.radius ?? 18) * unit);
    const cs = cards.slice(0, MAX_CARDS);
    gl.uniform1i(gl.getUniformLocation(compose, 'uCards'), cs.length);
    if (cs.length) gl.uniform4fv(gl.getUniformLocation(compose, 'uCard'), cs.flatMap((c) => [c.x, c.y, c.w, c.h]));
    const ds = dials.slice(0, MAX_DIALS);
    gl.uniform1i(gl.getUniformLocation(compose, 'uDials'), ds.length);
    if (ds.length) {
      gl.uniform4fv(gl.getUniformLocation(compose, 'uDial'), ds.flatMap((d) => [d.cx, d.cy, d.r, d.track]));
      gl.uniform2fv(gl.getUniformLocation(compose, 'uDialAngle'), ds.flatMap((d) => [d.start, d.sweep]));
    }
    this.draw(compose, null, w, h);
  }
}
