/**
 * Animated pictures (".anim"): JPEG frames played in a loop, the format the recorder plays (OverlayAnimation.cs).
 * Made here from a generated scene or an uploaded GIF, WebP or APNG; read here to play them in the editor.
 *
 *  0 "PCDA" · 4 version 1 · 5 reserved · 6 frames · 8 frames a second · 10 width · 12 height · 14 reserved
 * 16 each frame's length (4 bytes each), then the frames one after another (all little-endian).
 */

export interface Animation {
  fps: number;
  width: number;
  height: number;
  frames: Blob[];
}

/** The most frames an animation may have (the recorder's limit). */
export const MAX_FRAMES = 300;

/** Packs JPEG frames into an animation the recorder plays. */
export async function packAnimation(fps: number, width: number, height: number, frames: Blob[]): Promise<Blob> {
  const buffers = await Promise.all(frames.map((f) => f.arrayBuffer()));
  const head = new ArrayBuffer(16 + 4 * frames.length);
  const view = new DataView(head);
  new Uint8Array(head).set([0x50, 0x43, 0x44, 0x41], 0);
  view.setUint8(4, 1);
  view.setUint16(6, frames.length, true);
  view.setUint16(8, Math.round(fps), true);
  view.setUint16(10, Math.round(width), true);
  view.setUint16(12, Math.round(height), true);
  buffers.forEach((b, i) => view.setUint32(16 + 4 * i, b.byteLength, true));
  return new Blob([head, ...buffers], { type: 'application/octet-stream' });
}

/** Reads an animation; null when the bytes are not one. */
export function readAnimation(bytes: ArrayBuffer): Animation | null {
  const view = new DataView(bytes);
  if (bytes.byteLength < 16 || String.fromCharCode(...new Uint8Array(bytes, 0, 4)) !== 'PCDA' || view.getUint8(4) !== 1) return null;
  const count = view.getUint16(6, true);
  const fps = view.getUint16(8, true);
  const width = view.getUint16(10, true);
  const height = view.getUint16(12, true);
  if (count < 1 || count > MAX_FRAMES || fps < 1) return null;
  let at = 16 + 4 * count;
  const frames: Blob[] = [];
  for (let i = 0; i < count; i++) {
    const length = view.getUint32(16 + 4 * i, true);
    if (at + length > bytes.byteLength) return null;
    frames.push(new Blob([bytes.slice(at, at + length)], { type: 'image/jpeg' }));
    at += length;
  }
  return { fps, width, height, frames };
}

/** The frame shown at a moment (seconds on any steady clock), as the recorder picks it. */
export const frameAt = (animation: { fps: number; frames: unknown[] }, seconds: number) =>
  animation.frames.length === 0 ? 0 : Math.floor(Math.max(0, seconds) * animation.fps) % animation.frames.length;

/** A canvas's picture as a JPEG. */
export function jpegOf(canvas: HTMLCanvasElement | OffscreenCanvas, quality = 0.86): Promise<Blob> {
  if ('convertToBlob' in canvas) return canvas.convertToBlob({ type: 'image/jpeg', quality });
  return new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('The frame could not be encoded.'))), 'image/jpeg', quality));
}

/**
 * The frames of an animated picture file (GIF, WebP or APNG), as the browser decodes them (WebCodecs), shrunk to fit
 * `maxSide` and at most {@link MAX_FRAMES}; null when the browser cannot decode it or it has one frame.
 */
export async function decodeAnimatedFile(file: Blob, maxSide = 1280): Promise<{ fps: number; frames: ImageBitmap[] } | null> {
  const Decoder = (globalThis as unknown as { ImageDecoder?: new (init: { data: ArrayBuffer; type: string }) => ImageDecoderLike }).ImageDecoder;
  if (!Decoder || !file.type) return null;
  const decoder = new Decoder({ data: await file.arrayBuffer(), type: file.type });
  try {
    await decoder.tracks.ready;
    const track = decoder.tracks.selectedTrack;
    const count = Math.min(MAX_FRAMES, track?.frameCount ?? 1);
    if (count < 2) return null;
    const frames: ImageBitmap[] = [];
    let duration = 0;
    for (let i = 0; i < count; i++) {
      const { image } = await decoder.decode({ frameIndex: i });
      duration += image.duration ?? 100_000;
      const k = Math.min(1, maxSide / Math.max(image.displayWidth, image.displayHeight));
      frames.push(await createImageBitmap(image, { resizeWidth: Math.max(1, Math.round(image.displayWidth * k)), resizeHeight: Math.max(1, Math.round(image.displayHeight * k)), resizeQuality: 'high' }));
      image.close();
    }
    // The rate the file plays at on average (its frames' durations are in microseconds).
    const fps = Math.max(1, Math.min(60, Math.round(count / (duration / 1_000_000))));
    return { fps, frames };
  } finally {
    decoder.close();
  }
}

/**
 * The frames of a short video (MP4, WebM), at `fps` for at most `maxSeconds`, shrunk to fit `maxSide`; null when this
 * browser cannot play it.
 */
export async function decodeVideoFile(file: Blob, options: { maxSide?: number; fps?: number; maxSeconds?: number; onProgress?: (done: number) => void } = {}): Promise<{ fps: number; frames: ImageBitmap[] } | null> {
  const { maxSide = 1280, fps = 15, maxSeconds = 20 } = options;
  const url = URL.createObjectURL(file);
  const video = document.createElement('video');
  video.muted = true;
  video.playsInline = true;
  video.preload = 'auto';
  try {
    const ready = await new Promise<boolean>((resolve) => {
      video.onloadeddata = () => resolve(true);
      video.onerror = () => resolve(false);
      video.src = url;
    });
    if (!ready || !Number.isFinite(video.duration) || video.videoWidth < 1) return null;
    const count = Math.max(2, Math.min(MAX_FRAMES, Math.floor(Math.min(video.duration, maxSeconds) * fps)));
    const k = Math.min(1, maxSide / Math.max(video.videoWidth, video.videoHeight));
    const size = { resizeWidth: Math.max(1, Math.round(video.videoWidth * k)), resizeHeight: Math.max(1, Math.round(video.videoHeight * k)), resizeQuality: 'high' as const };
    const frames: ImageBitmap[] = [];
    for (let i = 0; i < count; i++) {
      await new Promise<void>((resolve) => {
        video.onseeked = () => resolve();
        video.currentTime = Math.min(video.duration - 0.001, i / fps);
      });
      frames.push(await createImageBitmap(video, size));
      options.onProgress?.((i + 1) / count);
    }
    return { fps, frames };
  } finally {
    video.removeAttribute('src');
    video.load();
    URL.revokeObjectURL(url);
  }
}

/**
 * Encodes frames as an animation's JPEGs. `blend` frames at the end fade into the first ones, so a clip that was
 * never made to loop plays round without a jump (those first frames then only appear in the fade).
 */
export async function encodeFrames(frames: readonly ImageBitmap[], options: { quality?: number; blend?: number; onProgress?: (done: number) => void } = {}): Promise<{ width: number; height: number; frames: Blob[] }> {
  const first = frames[0]!;
  const canvas = document.createElement('canvas');
  canvas.width = first.width;
  canvas.height = first.height;
  const ctx = canvas.getContext('2d')!;
  const blend = Math.max(0, Math.min(options.blend ?? 0, Math.floor(frames.length / 3)));
  const length = frames.length - blend;
  const out: Blob[] = [];
  for (let i = 0; i < length; i++) {
    // The sequence starts after the frames that the end fades into.
    const at = i + blend;
    ctx.globalAlpha = 1;
    ctx.drawImage(frames[at]!, 0, 0, canvas.width, canvas.height);
    const into = at - (frames.length - blend);
    if (into >= 0) {
      ctx.globalAlpha = (into + 1) / (blend + 1);
      ctx.drawImage(frames[into]!, 0, 0, canvas.width, canvas.height);
    }
    out.push(await jpegOf(canvas, options.quality ?? 0.85));
    options.onProgress?.((i + 1) / length);
  }
  return { width: canvas.width, height: canvas.height, frames: out };
}

interface ImageDecoderLike {
  tracks: { ready: Promise<void>; selectedTrack: { frameCount: number } | null };
  decode(options: { frameIndex: number }): Promise<{ image: VideoFrame & { duration: number | null } }>;
  close(): void;
}
