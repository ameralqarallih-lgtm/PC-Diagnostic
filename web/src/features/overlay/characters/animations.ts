import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { apiGet, apiPost, apiRequest } from '@/lib/api/client';
import type { AnimationsAnswer, CharacterAnimation } from '@/lib/api/types';
import { ACTIONS } from './CharactersTab';
import { refreshCharacterAnimations } from './library';

/** The emotions' ids (new animations the reactions use: made with one click). */
export const EMOTION_IDS = ['worried', 'panic', 'tired', 'cheer'] as const;

/** Whether it is an emotion (as the recorder judges it; those made before kinds by their ids). */
export const isEmotion = (a: CharacterAnimation) => (a.kindOf ? a.kindOf === 'emotion' : (EMOTION_IDS as readonly string[]).includes(a.id));

/** How far an animation is: its clips made, failed and under way. */
export function progressOf(a: CharacterAnimation) {
  const clips = Object.values(a.clips);
  return { done: clips.filter((c) => c.state === 'done').length, failed: clips.filter((c) => c.state === 'failed').length, busy: clips.filter((c) => c.state === 'queued' || c.state === 'running').length, all: clips.length };
}

/**
 * The new animations (made by the character generator for every character), followed every few seconds while one is
 * being made; each clip made is played by the characters loaded at once.
 */
export function useAnimations() {
  const query = useQuery({
    queryKey: ['overlay-animations'],
    queryFn: ({ signal }) => apiGet<AnimationsAnswer>('/overlay/animations', signal),
    refetchInterval: (q) => (q.state.data?.animations.some((a) => progressOf(a).busy > 0) ? 3000 : false),
  });
  // A clip made (or one removed): the characters take it.
  const made = query.data ? query.data.animations.map((a) => `${a.id}:${progressOf(a).done}`).join('|') : null;
  const seen = useRef<string | null>(null);
  useEffect(() => {
    if (made === null) return;
    if (seen.current !== null && seen.current !== made) refreshCharacterAnimations();
    seen.current = made;
  }, [made]);
  return query;
}

function useChange<T>(fn: (arg: T) => Promise<unknown>) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['overlay-animations'] });
      refreshCharacterAnimations();
    },
  });
}

/** A new animation (or emotion) from a prompt, for every character (or those listed). */
export const useMakeAnimation = () => useChange((start: { name: string; prompt: string; characters?: string[]; kind?: 'animation' | 'emotion' }) => apiPost<CharacterAnimation>('/overlay/animations', start));

/** Made again: one character's clip, or every character's (no character); its last clip plays until the new one is made. */
export const useRegenerate = () =>
  useChange(({ id, character }: { id: string; character?: string }) => apiPost(`/overlay/animations/${encodeURIComponent(id)}${character ? `/${encodeURIComponent(character)}` : ''}/regenerate`));

/** On or off: the whole animation (no character) or one character's clip. */
export const useSetActive = () =>
  useChange(({ id, character, active }: { id: string; character?: string; active: boolean }) =>
    apiRequest('PUT', `/overlay/animations/${encodeURIComponent(id)}${character ? `/${encodeURIComponent(character)}` : ''}/active`, { active }),
  );

/** One character's clip removed (it does the closest it has). */
export const useRemoveClip = () => useChange(({ id, character }: { id: string; character: string }) => apiRequest('DELETE', `/overlay/animations/${encodeURIComponent(id)}/${encodeURIComponent(character)}`));

/** Its name or prompt changed. */
export const useEditAnimation = () => useChange(({ id, name, prompt }: { id: string; name?: string; prompt?: string }) => apiRequest('PUT', `/overlay/animations/${encodeURIComponent(id)}`, { name, prompt }));

/** The emotions (worried, panic, tired, cheer), for every character. */
export const useMakeEmotions = () => useChange(() => apiPost('/overlay/animations/emotions', {}));

/** An animation's failed clips made again (and those of characters it does not have yet). */
export const useRetryAnimation = () => useChange((id: string) => apiPost(`/overlay/animations/${encodeURIComponent(id)}/retry`));

/** An animation removed (characters doing it then do the closest they have). */
export const useRemoveAnimation = () => useChange((id: string) => apiRequest('DELETE', `/overlay/animations/${encodeURIComponent(id)}`));

/** Every action a character can do: the built-in ones, the emotions, and the new animations made (by name). */
export function useAllActions(): readonly (readonly [string, string])[] {
  const made = useAnimations().data?.animations ?? [];
  const known = new Set(ACTIONS.map(([id]) => id));
  return [...ACTIONS, ...made.filter((a) => !known.has(a.id)).map((a) => [a.id, a.name] as const)];
}
