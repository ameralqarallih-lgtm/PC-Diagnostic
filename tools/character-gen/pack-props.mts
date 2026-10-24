// Packs bake_props.py's frames into the props file (the app's format, pack.ts: a clip per prop, as the effects' file),
// each prop in 256 colours (png8.ts). Usage: node pack-props.mts WORK TARGET
import { readFileSync, writeFileSync } from 'node:fs';
import { encodeFrames8 } from '../../web/src/features/overlay/characters/png8.ts';
import { readPack, writePack } from '../../web/src/features/overlay/characters/pack.ts';

const [dir, target] = process.argv.slice(2) as [string, string];
const m = JSON.parse(readFileSync(`${dir}/manifest.json`, 'utf8'));
const clips = [];
for (const c of m.clips) {
  const frames = c.frames.map((f: string) => ({ data: new Uint8ClampedArray(readFileSync(`${dir}/${f}`)), width: m.width, height: m.height }));
  clips.push({ action: c.action, fps: c.fps, frames: await encodeFrames8(frames), at: 'feet' as const, mirror: c.mirror === true });
}
const bytes = writePack({ name: m.name, width: m.width, height: m.height, anchor: m.anchor, head: m.head, tall: m.tall, faces: m.faces, clips });
if (!readPack(bytes, { effects: true })) throw new Error('not a sound props file');
writeFileSync(target, bytes);
console.log(target, (bytes.length / 1024).toFixed(0), 'KB', clips.map((c) => c.action).join(', '));
