import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiGet, apiPost, apiRequest } from '@/lib/api/client';
import type { OverlayConfig, OverlayImage, OverlayResponse, OverlayState } from '@/lib/api/types';

/** The saved layout and the recorder's view of the screens. */
export function useOverlay() {
  return useQuery({ queryKey: ['overlay'], queryFn: ({ signal }) => apiGet<OverlayResponse>('/overlay', signal), staleTime: Infinity, refetchOnWindowFocus: false });
}

/** The monitors and values now (the preview refreshes them; sampling keeps going while the page is open). */
export function useOverlayState(enabled = true) {
  return useQuery({ queryKey: ['overlay-state'], queryFn: ({ signal }) => apiGet<OverlayState>('/overlay/state', signal), refetchInterval: 1500, enabled, retry: false });
}

/** Saves the layout; the recorder shows it at once. */
export function useSaveOverlay() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (config: OverlayConfig) => apiRequest<OverlayResponse>('PUT', '/overlay', config),
    onSuccess: (response) => {
      if (response) client.setQueryData<OverlayResponse>(['overlay'], (old) => (old ? { ...old, config: response.config, recorderProblem: response.recorderProblem, revision: response.revision } : response));
    },
  });
}

/** Starts or ends arranging the panels on the screens themselves (moving, resizing and editing them there). */
export function useArrange() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (on: boolean) => apiPost<OverlayState>('/overlay/arrange', { on }),
    onSuccess: (state) => {
      if (state) client.setQueryData<OverlayState>(['overlay-state'], state);
    },
  });
}

export function useIdentify() {
  return useMutation({ mutationFn: () => apiPost('/overlay/identify') });
}

/** Largest side of a background picture as uploaded (larger ones are shrunk here first). */
export const PICTURE_SIZE = 1920;

/**
 * Uploads a background picture: read here, shrunk to {@link PICTURE_SIZE} on its longest side and stored as PNG (when
 * it may have transparency) or JPEG, the forms the recorder draws.
 */
export async function uploadPicture(file: File, name?: string): Promise<OverlayImage> {
  const dataUrl = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(new Error('The file could not be read.'));
    reader.readAsDataURL(file);
  });
  const picture = await new Promise<HTMLImageElement>((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error('That file is not a picture this browser can show.'));
    image.src = dataUrl;
  });
  const k = Math.min(1, PICTURE_SIZE / Math.max(1, picture.naturalWidth, picture.naturalHeight));
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(picture.naturalWidth * k));
  canvas.height = Math.max(1, Math.round(picture.naturalHeight * k));
  canvas.getContext('2d')?.drawImage(picture, 0, 0, canvas.width, canvas.height);
  const transparent = /png|gif|webp|svg|avif/i.test(file.type);
  const blob = await new Promise<Blob>((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('The picture could not be converted.'))), transparent ? 'image/png' : 'image/jpeg', 0.9));
  return uploadBlob(blob, { name });
}

/**
 * Saves a picture (PNG, JPEG or an animation) where the recorder reads it; its id. An upload (named) joins the library
 * and is copied to the storage locations that keep pictures; a generated one (made for a panel) goes once unused.
 */
export async function uploadBlob(blob: Blob, options: { name?: string; generated?: boolean } = {}): Promise<OverlayImage> {
  const query = new URLSearchParams();
  if (options.name) query.set('name', options.name);
  if (options.generated) query.set('generated', 'true');
  const response = await fetch(`/api/v1/overlay/images${query.size ? `?${query}` : ''}`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { Accept: 'application/json', 'Content-Type': blob.type || 'application/octet-stream' },
    body: blob,
  });
  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as { detail?: string; title?: string } | null;
    throw new Error(problem?.detail ?? problem?.title ?? `The picture could not be saved (${response.status}).`);
  }

  return (await response.json()) as OverlayImage;
}


