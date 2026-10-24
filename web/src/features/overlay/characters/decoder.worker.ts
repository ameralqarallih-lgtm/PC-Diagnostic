/// <reference lib="webworker" />
import { bodyOf } from './tops';

/**
 * Decodes characters' frames off the page's thread: a PNG in, an ImageBitmap of the asked size out (transferred, not
 * copied), so decoding hundreds of frames never stops the page from answering; and, asked with a head's width, the
 * body in it (tops.ts).
 */
interface Ask {
  id: number;
  png: ArrayBuffer;
  width: number;
  height: number;
  wide?: number;
}

self.onmessage = async (e: MessageEvent<Ask>) => {
  const { id, png, width, height, wide } = e.data;
  try {
    const bitmap = await createImageBitmap(new Blob([png], { type: 'image/png' }), { resizeWidth: width, resizeHeight: height, resizeQuality: 'high' });
    const body = wide ? bodyOf(bitmap, wide) : null;
    (self as unknown as DedicatedWorkerGlobalScope).postMessage({ id, bitmap, body }, [bitmap]);
  } catch {
    (self as unknown as DedicatedWorkerGlobalScope).postMessage({ id, bitmap: null, body: null });
  }
};
