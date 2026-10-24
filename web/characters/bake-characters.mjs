// Bakes the shipped characters: renders every clip of each drawing (bake.html, in headless Edge or Chrome) and packs
// the frames into the ".char" files the app ships, src/PcDiag.Core/Overlay/Characters/<id>.char (see CharacterPack.cs
// for the format). Run after changing a drawing:  node characters/bake-characters.mjs [id ...]   (from web/)
// Set BROWSER to a Chromium browser's exe when Edge is not in its usual place.
import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { stripTypeScriptTypes } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const out = path.resolve(here, '../../src/PcDiag.Core/Overlay/Characters');
const ids = process.argv.slice(2).length ? process.argv.slice(2) : ['effects'];
const browserExe = process.env.BROWSER ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** The ".char" file: "PCDC", version 1, a reserved byte, the manifest's length, the manifest, then the PNG frames. */
function pack(baked) {
  const frames = baked.clips.flatMap((c) => c.frames.map((f) => Buffer.from(f, 'base64')));
  let i = 0;
  const manifest = Buffer.from(
    JSON.stringify({
      name: baked.name,
      width: baked.width,
      height: baked.height,
      anchor: baked.anchor,
      ...(baked.head ? { head: baked.head } : {}),
      tall: baked.tall,
      faces: baked.faces,
      clips: baked.clips.map((c) => ({ action: c.action, fps: c.fps, ...(c.at ? { at: c.at, mirror: c.mirror } : {}), frames: c.frames.map(() => frames[i++].length) })),
    }),
    'utf8',
  );
  const head = Buffer.alloc(10);
  head.write('PCDC', 0, 'ascii');
  head.writeUInt8(1, 4);
  head.writeUInt32LE(manifest.length, 6);
  return Buffer.concat([head, manifest, ...frames]);
}

// The page and its scripts, over HTTP (a module cannot be imported from a file:// page), and the editor's PNG encoder
// as JavaScript.
const png8 = stripTypeScriptTypes(readFileSync(path.resolve(here, '../src/features/overlay/characters/png8.ts'), 'utf8'));
const server = createServer((request, response) => {
  const name = path.basename(new URL(request.url, 'http://x').pathname);
  try {
    const body = name === 'png8.js' ? png8 : readFileSync(path.join(here, name));
    response.writeHead(200, { 'content-type': name.endsWith('.html') ? 'text/html' : 'text/javascript' });
    response.end(body);
  } catch {
    response.writeHead(404);
    response.end();
  }
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const site = `http://127.0.0.1:${server.address().port}`;

const profile = mkdtempSync(path.join(tmpdir(), 'pcdiag-bake-'));
const port = 9400 + Math.floor(Math.random() * 400);
const browser = spawn(browserExe, ['--headless=new', '--disable-gpu', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' });
try {
  let target;
  for (let i = 0; i < 75 && !target; i++) {
    await sleep(200);
    try {
      target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === 'page');
    } catch {
      // Not up yet.
    }
  }
  if (!target) throw new Error(`The browser did not start (${browserExe}).`);
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    ws.onopen = resolve;
    ws.onerror = reject;
  });
  let next = 0;
  const pending = new Map();
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.id && pending.has(msg.id)) {
      pending.get(msg.id)(msg);
      pending.delete(msg.id);
    }
  };
  const send = (method, params = {}) =>
    new Promise((resolve) => {
      const n = ++next;
      pending.set(n, resolve);
      ws.send(JSON.stringify({ id: n, method, params }));
    });
  await send('Page.enable');
  await send('Page.navigate', { url: `${site}/bake.html?bake` });
  await sleep(1500);
  for (const id of ids) {
    const started = Date.now();
    const reply = await send('Runtime.evaluate', { expression: id === 'effects' ? 'bakeEffects()' : `bake(${JSON.stringify(id)})`, returnByValue: true, awaitPromise: true });
    const baked = reply.result?.result?.value;
    if (!baked) throw new Error(`${id}: ${JSON.stringify(reply.result?.exceptionDetails ?? reply.error ?? reply)}`);
    const file = path.join(out, `${id}.char`);
    const bytes = pack(baked);
    writeFileSync(file, bytes);
    const counts = baked.clips.map((c) => `${c.action} ${c.frames.length}`).join(', ');
    console.log(`${id}: ${baked.width} × ${baked.height}, ${counts}; ${(bytes.length / 1024).toFixed(0)} KB in ${((Date.now() - started) / 1000).toFixed(1)} s`);
  }
  ws.close();
} finally {
  server.close();
  browser.kill();
  await sleep(500);
  try {
    rmSync(profile, { recursive: true, force: true });
  } catch {
    // Still locked; it is in the temp folder.
  }
}
