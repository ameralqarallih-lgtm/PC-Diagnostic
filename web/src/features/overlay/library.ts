import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGet, apiPost, apiRequest } from '@/lib/api/client';
import type { LibraryPicture, OverlayImage } from '@/lib/api/types';
import { decodeAnimatedFile, decodeVideoFile, encodeFrames, packAnimation } from './anim';
import { uploadBlob, uploadPicture } from './useOverlay';

/**
 * The picture library: the pictures uploaded (stills and animations), kept on this PC where the recorder reads them
 * and copied to the storage locations that keep pictures (Settings › Storage); and those only in a storage location,
 * brought here when chosen.
 */
export function useLibrary(enabled = true) {
  return useQuery({ queryKey: ['overlay-library'], queryFn: ({ signal }) => apiGet<LibraryPicture[]>('/overlay/images', signal), enabled, staleTime: 5_000 });
}

/** Removes a picture from the library (here and in every storage location); refused while a panel shows it. */
export function useDeletePicture() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiRequest('DELETE', `/overlay/images/${encodeURIComponent(id)}`),
    onSuccess: () => client.invalidateQueries({ queryKey: ['overlay-library'] }),
  });
}

/** Brings a picture kept only in another storage location to this PC (where the recorder reads it). */
export function useFetchPicture() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiPost(`/overlay/images/${encodeURIComponent(id)}/fetch`),
    onSuccess: () => client.invalidateQueries({ queryKey: ['overlay-library'] }),
  });
}

/** The largest side of an uploaded animation's frames, and its most bytes (the recorder takes 48 MB). */
export const ANIMATION_SIZE = 1280;
const MAX_ANIMATION_BYTES = 46 * 1024 * 1024;

export const ACCEPTED = 'image/png,image/jpeg,image/gif,image/webp,image/avif,image/bmp,image/svg+xml,video/mp4,video/webm,video/quicktime';

const nameOf = (file: File) => file.name.replace(/\.[^.]+$/, '').slice(0, 80) || 'Picture';

/**
 * Adds a file to the library: a photo or artwork as a still; an animated GIF, WebP or PNG, or a short video (up to
 * 20 seconds; its end fades into its start so it loops), as an animation. `onProgress` says what is happening.
 */
export async function uploadToLibrary(file: File, onProgress?: (what: string, done: number) => void): Promise<OverlayImage> {
  const name = nameOf(file);
  const video = file.type.startsWith('video/');
  let decoded: { fps: number; frames: ImageBitmap[] } | null = null;
  if (video) {
    decoded = await decodeVideoFile(file, { maxSide: ANIMATION_SIZE, onProgress: (d) => onProgress?.('Reading the video', d) });
    if (!decoded) throw new Error('This browser cannot play that video. MP4 (H.264) or WebM work everywhere.');
  } else if (/gif|webp|png|avif/i.test(file.type)) {
    onProgress?.('Reading the frames', 0);
    decoded = await decodeAnimatedFile(file, ANIMATION_SIZE).catch(() => null);
  }
  if (!decoded) {
    onProgress?.('Saving the picture', 0.5);
    return uploadPicture(file, name);
  }
  try {
    // A video's end fades into its start (about half a second); an animated picture was made to loop already.
    const blend = video ? Math.round(decoded.fps * 0.6) : 0;
    let quality = 0.85;
    for (;;) {
      const encoded = await encodeFrames(decoded.frames, { quality, blend, onProgress: (d) => onProgress?.('Making the animation', d) });
      const blob = await packAnimation(decoded.fps, encoded.width, encoded.height, encoded.frames);
      if (blob.size <= MAX_ANIMATION_BYTES || quality < 0.5) {
        if (blob.size > MAX_ANIMATION_BYTES) throw new Error('That animation is too large even compressed: try a shorter or smaller one.');
        onProgress?.('Saving the animation', 1);
        return await uploadBlob(blob, { name });
      }
      quality -= 0.15;
    }
  } finally {
    for (const frame of decoded.frames) frame.close();
  }
}

/** A size in bytes for people. */
export function bytes(n: number) {
  return n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`;
}
