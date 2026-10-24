// Packs finish.py's frames into a ".char" (the app's format, pack.ts), each clip's frames in 256 colours sharing a
// palette (png8.ts, the editor's encoder). Usage: node pack.mts NAME   (Node runs the TypeScript as it is)
// In the repo it uses the editor's own pack.ts and png8.ts; installed, the copies beside it (lib, put there by the
// installer's build).
import { existsSync, readFileSync, writeFileSync } from 'node:fs';

const lib = (file: string) => new URL(existsSync(new URL(`./lib/${file}`, import.meta.url)) ? `./lib/${file}` : `../../web/src/features/overlay/characters/${file}`, import.meta.url).href;
const { encodeFrames8 } = (await import(lib('png8.ts'))) as typeof import('../../web/src/features/overlay/characters/png8.ts');
const { readPack, writePack } = (await import(lib('pack.ts'))) as typeof import('../../web/src/features/overlay/characters/pack.ts');

const name = process.argv[2]!;
const out = process.env.CHARGEN_OUT ?? new URL('../../artifacts/chargen', import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1');
const dir = `${decodeURIComponent(out)}/${name}/pack`;
const m = JSON.parse(readFileSync(`${dir}/manifest.json`, 'utf8'));
const clips = [];
for (const c of m.clips) {
  const frames = c.frames.map((f: string) => ({ data: new Uint8ClampedArray(readFileSync(`${dir}/${f}`)), width: m.width, height: m.height }));
  clips.push({ action: c.action, fps: c.fps, frames: await encodeFrames8(frames) });
}
const bytes = writePack({ name: m.name, width: m.width, height: m.height, anchor: m.anchor, head: m.head, tall: m.tall, faces: m.faces, clips });
// A new animation (animate.py) is an extra file: its clip alone, played with the character it was made for.
if (!readPack(bytes, { extra: m.extra === true })) throw new Error('not a sound character');
const file = `${decodeURIComponent(out)}/${name}/${name}.char`;
writeFileSync(file, bytes);
console.log(file, (bytes.length / 1024 / 1024).toFixed(2), 'MB', clips.map((c) => `${c.action} ${c.frames.length}`).join(', '));
