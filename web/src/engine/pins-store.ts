// Where "+ Add" pins live in the browser: localStorage on this device only (never sent anywhere), with an in-memory copy
// so the feature still works for the session when storage is blocked (private windows, strict settings). Tiny
// subscription for useSyncExternalStore; other tabs pick changes up through the "storage" event.

import { addPin, removePin, sanitizePins, type Pins, type WheelKind } from "./wheel";

export const PINS_STORAGE_KEY = "dropflow.pins";

let pins: Pins | null = null;
let version = 0;
const listeners = new Set<() => void>();

function read(): Pins {
  try {
    const raw = typeof localStorage !== "undefined" ? localStorage.getItem(PINS_STORAGE_KEY) : null;
    return raw ? sanitizePins(JSON.parse(raw)) : {};
  } catch {
    return {};
  }
}

function write(next: Pins): void {
  pins = next;
  version++;
  try {
    if (typeof localStorage !== "undefined") localStorage.setItem(PINS_STORAGE_KEY, JSON.stringify(next));
  } catch {
    /* storage blocked or full: the in-memory copy still works for this session */
  }
  listeners.forEach((l) => l());
}

export function getPins(): Pins {
  pins ??= read();
  return pins;
}

export function pinsVersion(): number {
  return version;
}

export function subscribePins(fn: () => void): () => void {
  listeners.add(fn);
  const onStorage = (e: StorageEvent) => {
    if (e.key !== PINS_STORAGE_KEY) return;
    pins = read();
    version++;
    fn();
  };
  if (typeof window !== "undefined") window.addEventListener("storage", onStorage);
  return () => {
    listeners.delete(fn);
    if (typeof window !== "undefined") window.removeEventListener("storage", onStorage);
  };
}

export function pin(wheel: WheelKind, category: string, id: string): void {
  const next = addPin(getPins(), wheel, category, id);
  if (next !== getPins()) write(next);
}

export function unpin(wheel: WheelKind, category: string, id: string): void {
  const next = removePin(getPins(), wheel, category, id);
  if (next !== getPins()) write(next);
}

/** Tests only. */
export function resetPinsForTests(): void {
  pins = null;
  version = 0;
}
