import { useSyncExternalStore } from 'react';

/**
 * Safe mode for every tune-up and control action: on, the recorder leaves anything other programs are using now (the
 * program in front, one with a window open, one busy with the CPU, the GPU, the network or a disk, a running browser's
 * cache) and says what it left and why. On by default, remembered in this browser.
 */
const KEY = 'pcdiag.tuneup.safe';
const listeners = new Set<() => void>();

function read(): boolean {
  try {
    return localStorage.getItem(KEY) !== 'off';
  } catch {
    return true;
  }
}

let current = read();

/** Whether safe mode is on (read when an action is sent). */
export function isSafeMode(): boolean {
  return current;
}

export function setSafeMode(on: boolean) {
  current = on;
  try {
    localStorage.setItem(KEY, on ? 'on' : 'off');
  } catch {
    // Not remembered in this browser; it still applies now.
  }
  for (const listener of listeners) listener();
}

export function useSafeMode(): [boolean, (on: boolean) => void] {
  const on = useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    () => current,
  );
  return [on, setSafeMode];
}
