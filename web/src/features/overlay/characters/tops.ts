/** A character's body in a frame: the top of its head (-1: none found) and its middle there, and its left and right edges, in the frame's pixels. */
export interface Body {
  top: number;
  middle: number;
  left: number;
  right: number;
}

/**
 * A character's body in a frame decoded (as the recorder's CharacterPlayer.Measure): the top of its head, the first row
 * with a solid stretch at least `wide` pixels across (ears, an antenna, hair spikes passed over), and that stretch's
 * middle; and its left and right edges. Null where it cannot be read.
 */
export function bodyOf(bitmap: ImageBitmap, wide: number): Body | null {
  if (typeof OffscreenCanvas === 'undefined') return null;
  const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  ctx.drawImage(bitmap, 0, 0);
  const { width, height } = bitmap;
  const { data } = ctx.getImageData(0, 0, width, height);
  const body: Body = { top: -1, middle: 0, left: width, right: -1 };
  const solid = (x: number, y: number) => data[(y * width + x) * 4 + 3]! > 96;
  for (let y = 0; y < height; y++) {
    let run = 0;
    for (let x = 0; x < width; x++) {
      if (!solid(x, y)) {
        run = 0;
        continue;
      }
      body.left = Math.min(body.left, x);
      body.right = Math.max(body.right, x);
      if (++run === wide && body.top < 0) {
        let end = x;
        while (end + 1 < width && solid(end + 1, y)) end++;
        body.top = y;
        body.middle = (x - wide + 1 + end) / 2;
      }
    }
  }
  return body;
}
