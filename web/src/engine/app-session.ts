// The one Session of the page (created lazily on the client). Workers are created lazily inside the pool on the first
// "worker" job, so merely opening /app never starts a worker or loads an engine.

import { createRunner } from "./runner";
import { registry } from "./registry";
import { Session } from "./session";
import { createPool } from "./worker-factory";

let session: Session | null = null;

export function getSession(): Session {
  if (!session) {
    session = new Session({ registry, runner: createRunner(createPool()), concurrency: 2 });
  }
  return session;
}

const EMPTY = { jobs: [], results: [] } as const;
export const serverSnapshot = () => EMPTY;
