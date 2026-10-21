"use client";

import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";

// Service-worker lifecycle for /app only (this hook is used by the app shell and nowhere else, so the landing page
// never registers a worker). The worker file is generated at build time (scripts/build-sw.mjs) and does not exist in
// `next dev`, so registration is production-only.

function subscribeOnline(cb: () => void): () => void {
  window.addEventListener("online", cb);
  window.addEventListener("offline", cb);
  return () => {
    window.removeEventListener("online", cb);
    window.removeEventListener("offline", cb);
  };
}

export function usePwa() {
  const online = useSyncExternalStore(subscribeOnline, () => navigator.onLine, () => true);
  const [ready, setReady] = useState(false);
  const [update, setUpdate] = useState(false);
  const reg = useRef<ServiceWorkerRegistration | null>(null);
  const reloading = useRef(false);

  useEffect(() => {
    if (process.env.NODE_ENV !== "production" || !("serviceWorker" in navigator)) return;
    let cancelled = false;
    const sw = navigator.serviceWorker;

    const onController = () => {
      if (reloading.current) window.location.reload();
    };
    sw.addEventListener("controllerchange", onController);

    sw.register("/sw.js", { scope: "/", updateViaCache: "none" })
      .then((registration) => {
        if (cancelled) return;
        reg.current = registration;
        if (registration.waiting && sw.controller) setUpdate(true);
        registration.addEventListener("updatefound", () => {
          const worker = registration.installing;
          worker?.addEventListener("statechange", () => {
            if (worker.state === "installed" && sw.controller) setUpdate(true);
          });
        });
        void sw.ready.then((r) => {
          if (cancelled) return;
          setReady(true);
          // Extras (CJK CMaps) are cached in the background, after the shell is safe: never blocks the install.
          r.active?.postMessage({ type: "CACHE_OPTIONAL" });
        });
      })
      .catch(() => {
        /* registration can fail in private windows; the app still works online */
      });

    return () => {
      cancelled = true;
      sw.removeEventListener("controllerchange", onController);
    };
  }, []);

  const applyUpdate = useCallback(() => {
    reloading.current = true;
    const waiting = reg.current?.waiting;
    if (waiting) waiting.postMessage({ type: "SKIP_WAITING" });
    else window.location.reload();
  }, []);

  return { online, ready, update, applyUpdate };
}
