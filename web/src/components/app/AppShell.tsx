"use client";

import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { useLocale, useTranslations } from "next-intl";
import { GitHubLink } from "@/components/GitHubLink";
import { Logo } from "@/components/Logo";
import { Link } from "@/i18n/navigation";
import { capsVersion, getMediaCaps, subscribeCaps } from "@/engine/caps";
import { categoryForExtension } from "@/engine/catalog-web";
import { ensureImageCaps } from "@/engine/converters/images";
import { loadMediaCaps } from "@/engine/converters/media";
import { getSession, serverSnapshot } from "@/engine/app-session";
import { extensionOf, formatBytes } from "@/engine/names";
import { defaultValues } from "@/engine/option-values";
import { registry } from "@/engine/registry";
import type { Converter, OptionValues } from "@/engine/types";
import {
  WEB_ACTIONS,
  WHEEL_ORDER,
  buildWheels,
  firstNonEmpty,
  isWebAction,
  pageWedges,
  wheelForKey,
  wheelForModifiers,
  type ActionState,
  type Wedge,
  type WheelKind,
} from "@/engine/wheel";
import { windowsHint } from "@/engine/windows-hint";
import { JobList, ResultList } from "./Activity";
import { DropOverlay } from "./DropOverlay";
import { OptionsPanel } from "./OptionsPanel";
import { Wheel, type WheelItem } from "./Wheel";
import { WindowsHintTile } from "./WindowsHint";
import { canShareFiles, copyBlobText, downloadAll, shareFiles } from "./browser-actions";
import { captureDrop, dedupeFiles, dragHasFiles, dragHasLink } from "./dropped-files";
import { LinkError, extractUrl, fetchLink, parseHttpUrl } from "@/engine/lib/fetch-link";
import { usePwa } from "./usePwa";

const KEY_OF: Record<WheelKind, "shift" | "ctrl" | "alt"> = { convert: "shift", tools: "ctrl", actions: "alt" };
const LIMITS_MB = [50, 100, 250, 500, 1024, 0];
const DEFAULT_LIMIT_MB = 100;
const LIMIT_STORAGE_KEY = "dropflow.bigMb";
const TEXT_INPUT_LIMIT = 2 * 1024 * 1024;

const LIMIT_EVENT = "dropflow:limit";

function subscribeLimit(cb: () => void): () => void {
  window.addEventListener("storage", cb);
  window.addEventListener(LIMIT_EVENT, cb);
  return () => {
    window.removeEventListener("storage", cb);
    window.removeEventListener(LIMIT_EVENT, cb);
  };
}

function readLimit(): number {
  try {
    const raw = window.localStorage.getItem(LIMIT_STORAGE_KEY);
    const n = Number(raw);
    return raw !== null && LIMITS_MB.includes(n) ? n : DEFAULT_LIMIT_MB;
  } catch {
    return DEFAULT_LIMIT_MB; // storage can be blocked
  }
}

function isTyping(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  if (!el || !el.tagName) return false;
  return el.tagName === "INPUT" || el.tagName === "TEXTAREA" || el.tagName === "SELECT" || el.isContentEditable;
}

function CloseIcon() {
  return (
    <svg viewBox="0 0 24 24" className="size-4" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" aria-hidden="true">
      <path d="M6 6l12 12M18 6L6 18" />
    </svg>
  );
}

export function AppShell() {
  const t = useTranslations("app");
  const tc = useTranslations("common");
  const locale = useLocale();
  const pwa = usePwa();

  // ---- engine state -------------------------------------------------------------------------------------------
  const session = useMemo(() => (typeof window === "undefined" ? null : getSession()), []);
  const snap = useSyncExternalStore(session?.subscribe ?? (() => () => {}), session?.getSnapshot ?? serverSnapshot, serverSnapshot);
  const capsV = useSyncExternalStore(subscribeCaps, capsVersion, () => 0);

  // ---- UI state -----------------------------------------------------------------------------------------------
  const [files, setFiles] = useState<File[]>([]);
  const [wheelKind, setWheelKind] = useState<WheelKind>("convert");
  const [open, setOpen] = useState(false);
  const [page, setPage] = useState(0);
  const [pending, setPending] = useState<Wedge | null>(null);
  const [drag, setDrag] = useState<{ kind: WheelKind } | null>(null);
  const [toast, setToast] = useState("");
  const [focusToken, setFocusToken] = useState(0);
  const bigMb = useSyncExternalStore(subscribeLimit, readLimit, () => DEFAULT_LIMIT_MB);
  const [engineFailed, setEngineFailed] = useState(false);
  const [retry, setRetry] = useState(0);
  // "Drop a link": one download at a time, with progress and cancel.
  const [link, setLink] = useState<{ url: string; received: number; total: number } | null>(null);
  const [linkError, setLinkError] = useState("");
  const [linkText, setLinkText] = useState("");
  const linkAbort = useRef<AbortController | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const activityRef = useRef<HTMLDivElement>(null);
  const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const say = useCallback((message: string) => {
    setToast(message);
    if (toastTimer.current) clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast(""), 2600);
  }, []);

  useEffect(() => {
    void ensureImageCaps();
    return () => {
      if (toastTimer.current) clearTimeout(toastTimer.current);
    };
  }, []);

  // ---- derived: wheels -----------------------------------------------------------------------------------------
  const results = snap.results;
  const needsMedia = useMemo(
    () =>
      files.some((f) => {
        const id = categoryForExtension(extensionOf(f.name))?.id;
        return id === "audio" || id === "video";
      }),
    [files],
  );
  // `capsV` re-derives these when the capability store changes (media probe finished, image formats detected).
  const mediaReady = capsV >= 0 && getMediaCaps() !== null;
  const engineLoading = needsMedia && !mediaReady && !engineFailed;

  useEffect(() => {
    if (!needsMedia || getMediaCaps()) return;
    let cancelled = false;
    loadMediaCaps().catch(() => {
      if (!cancelled) setEngineFailed(true);
    });
    return () => {
      cancelled = true;
    };
  }, [needsMedia, retry]);

  const subject = useMemo(() => (results.length ? results.map((r) => r.file) : files), [results, files]);
  const actionState = useMemo<ActionState>(
    () => ({
      hasResults: results.length > 0,
      canShare: canShareFiles(subject),
      hasText: results.length ? results.some((r) => r.isText) : files.some((f) => f.type.startsWith("text/") && f.size <= TEXT_INPUT_LIMIT),
    }),
    [results, files, subject],
  );
  const built = useMemo(
    () => buildWheels(files.map((f) => ({ name: f.name, size: f.size })), registry, actionState),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- capsV: converters read the capability store inside supports()
    [files, actionState, capsV],
  );
  // While the ffmpeg core is being probed the wheel must not show a half-built ring: the centre shows the loader instead.
  const wheels = useMemo(() => (engineLoading ? { convert: [], tools: [], actions: built.actions } : built), [engineLoading, built]);
  const activeKind = engineLoading ? wheelKind : (firstNonEmpty(wheels, wheelKind) ?? wheelKind);
  const pages = useMemo(() => pageWedges(wheels[activeKind]), [wheels, activeKind]);
  const pageIndex = Math.min(page, pages.length - 1);
  const wedges = pages[pageIndex];
  const labelOf = useCallback((w: Wedge) => w.label ?? (w.labelKey ? t(w.labelKey as never) : w.operation), [t]);
  const items: WheelItem[] = wedges.map((w) => ({ id: w.id, label: labelOf(w), mono: w.wheel === "convert", more: w.more }));

  const running = snap.jobs.filter((j) => j.state === "running");
  const busyIds = useMemo(() => new Set(snap.jobs.filter((j) => j.state === "running" || j.state === "waiting").map((j) => j.operation)), [snap.jobs]);
  const progress = running.length ? running.reduce((n, j) => n + j.progress, 0) / running.length : null;
  const active = snap.jobs.some((j) => j.state === "running" || j.state === "waiting");

  const hint = useMemo(() => windowsHint(files, registry), [files]);
  const bigFiles = useMemo(() => (bigMb > 0 ? files.filter((f) => f.size >= bigMb * 1024 * 1024) : []), [files, bigMb]);

  // ---- adding files --------------------------------------------------------------------------------------------
  const addFiles = useCallback(
    (list: File[], kind: WheelKind | null) => {
      if (list.length === 0) return;
      setFiles((prev) => dedupeFiles(prev, list));
      setOpen(true);
      setPending(null);
      setPage(0);
      if (kind) setWheelKind(kind);
      setFocusToken((n) => n + 1);
      say(t("status.added", { count: list.length }));
    },
    [say, t],
  );
  const addRef = useRef(addFiles);
  useEffect(() => {
    addRef.current = addFiles;
  }, [addFiles]);

  const getLink = useCallback(
    async (raw: string, kind: WheelKind | null) => {
      const url = parseHttpUrl(raw);
      if (!url) {
        setLinkError(t("link.err.invalid"));
        return;
      }
      linkAbort.current?.abort();
      const controller = new AbortController();
      linkAbort.current = controller;
      setLinkError("");
      setLink({ url, received: 0, total: 0 });
      try {
        const file = await fetchLink(url, {
          signal: controller.signal,
          onProgress: (received, total) => setLink((l) => (l && l.url === url ? { url, received, total } : l)),
        });
        setLinkText("");
        addRef.current([file], kind);
      } catch (e) {
        const err = e instanceof LinkError ? e : new LinkError("blocked");
        if (err.code !== "aborted") setLinkError(t(`link.err.${err.code}`, { status: err.status ?? 0 }));
      } finally {
        if (linkAbort.current === controller) {
          linkAbort.current = null;
          setLink(null);
        }
      }
    },
    [t],
  );
  const linkRef = useRef(getLink);
  useEffect(() => {
    linkRef.current = getLink;
  }, [getLink]);

  // Window-level drag & drop: the whole page is the drop zone; Shift / Ctrl / Alt held at drop pick the wheel.
  useEffect(() => {
    let depth = 0;
    const droppable = (dt: DataTransfer | null) => dragHasFiles(dt) || dragHasLink(dt);
    const onEnter = (e: DragEvent) => {
      if (!droppable(e.dataTransfer)) return;
      e.preventDefault();
      depth++;
      setDrag({ kind: wheelForModifiers(e) ?? "convert" });
    };
    const onOver = (e: DragEvent) => {
      if (!droppable(e.dataTransfer)) return;
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = "copy";
      const kind = wheelForModifiers(e) ?? "convert";
      setDrag((d) => (d?.kind === kind ? d : { kind }));
    };
    const onLeave = (e: DragEvent) => {
      if (!droppable(e.dataTransfer)) return;
      depth = Math.max(0, depth - 1);
      if (depth === 0) setDrag(null);
    };
    const onDrop = (e: DragEvent) => {
      if (!e.dataTransfer || !droppable(e.dataTransfer)) return;
      e.preventDefault();
      depth = 0;
      setDrag(null);
      const kind = wheelForModifiers(e);
      if (dragHasFiles(e.dataTransfer)) {
        void captureDrop(e.dataTransfer)().then((list) => addRef.current(list, kind));
        return;
      }
      // A dropped link: text/uri-list from a browser, or plain text that is exactly one http(s) address.
      const url = extractUrl(e.dataTransfer.getData("text/uri-list")) ?? extractUrl(e.dataTransfer.getData("text/plain"));
      if (url) void linkRef.current(url, kind);
    };
    const onPaste = (e: ClipboardEvent) => {
      if (isTyping(e.target)) return;
      const list = [...(e.clipboardData?.files ?? [])];
      if (list.length) {
        e.preventDefault();
        addRef.current(list, null);
        return;
      }
      const url = extractUrl(e.clipboardData?.getData("text/uri-list")) ?? extractUrl(e.clipboardData?.getData("text/plain"));
      if (url) {
        e.preventDefault();
        void linkRef.current(url, null);
      }
    };
    window.addEventListener("dragenter", onEnter);
    window.addEventListener("dragover", onOver);
    window.addEventListener("dragleave", onLeave);
    window.addEventListener("drop", onDrop);
    window.addEventListener("paste", onPaste);
    return () => {
      window.removeEventListener("dragenter", onEnter);
      window.removeEventListener("dragover", onOver);
      window.removeEventListener("dragleave", onLeave);
      window.removeEventListener("drop", onDrop);
      window.removeEventListener("paste", onPaste);
    };
  }, []);

  // ---- keyboard: tap Shift / Ctrl / Alt to switch wheels, Esc to close ------------------------------------------
  const live = useRef({ open: false, pending: false, files: 0 });
  useEffect(() => {
    live.current = { open, pending: pending !== null, files: files.length };
  });
  const selectWheel = useCallback((kind: WheelKind) => {
    setWheelKind(kind);
    setPage(0);
    setPending(null);
  }, []);
  useEffect(() => {
    let tap: string | null = null;
    const onDown = (e: KeyboardEvent) => {
      if (e.repeat) return;
      if (wheelForKey(e.key)) {
        tap = live.current.open && live.current.files > 0 && !isTyping(e.target) ? e.key : null;
        if (e.key === "Alt" && tap) e.preventDefault();
        return;
      }
      tap = null;
      if (e.key === "Escape") {
        if (live.current.pending) setPending(null);
        else if (live.current.open) setOpen(false);
      }
    };
    const onUp = (e: KeyboardEvent) => {
      const kind = wheelForKey(e.key);
      if (!kind || tap !== e.key) return;
      tap = null;
      if (e.key === "Alt") e.preventDefault();
      selectWheel(kind);
      setFocusToken((n) => n + 1);
    };
    window.addEventListener("keydown", onDown);
    window.addEventListener("keyup", onUp);
    return () => {
      window.removeEventListener("keydown", onDown);
      window.removeEventListener("keyup", onUp);
    };
  }, [selectWheel]);

  // ---- running things ------------------------------------------------------------------------------------------
  const converterFor = useCallback((operation: string): Converter | undefined => registry.find(operation, extensionOf(files[0]?.name ?? "")), [files]);

  const start = useCallback(
    (wedge: Wedge, options: OptionValues) => {
      session?.submit({ operation: wedge.operation, files, options, multi: wedge.multi });
      setPending(null);
      setFocusToken((n) => n + 1);
      // On narrow screens the activity list sits below the wheel: bring it into view (no-op when already visible).
      requestAnimationFrame(() =>
        activityRef.current?.scrollIntoView({ block: "nearest", behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" }),
      );
    },
    [session, files],
  );

  const runAction = useCallback(
    async (operation: string) => {
      try {
        if (operation === WEB_ACTIONS.download) {
          await downloadAll(results, `${(results[0]?.sourceName ?? "DropFlow").replace(/\.[^.]+$/, "")} (DropFlow).zip`);
          say(t("results.saved"));
        } else if (operation === WEB_ACTIONS.share) {
          await shareFiles(subject);
        } else if (operation === WEB_ACTIONS.copy) {
          const texts = results.length ? results.filter((r) => r.isText).map((r) => r.file) : files.filter((f) => f.type.startsWith("text/"));
          if (await copyBlobText(texts)) say(t("results.copied"));
        }
      } catch {
        say(t("err.generic"));
      }
    },
    [results, files, subject, say, t],
  );

  const onPick = useCallback(
    (index: number) => {
      const wedge = wedges[index];
      if (!wedge) return;
      if (wedge.more) {
        setPage((p) => (Math.min(p, pages.length - 1) + 1) % pages.length);
        return;
      }
      if (isWebAction(wedge.operation)) {
        void runAction(wedge.operation);
        return;
      }
      const converter = converterFor(wedge.operation);
      if (!converter) return;
      const options = converter.options(wedge.operation);
      if (options.length > 0 || converter.warning?.(wedge.operation)) setPending(wedge);
      else start(wedge, defaultValues(options));
    },
    [wedges, pages.length, converterFor, start, runAction],
  );

  const pickFiles = (e: React.ChangeEvent<HTMLInputElement>) => {
    const list = [...(e.target.files ?? [])];
    e.target.value = ""; // allow picking the same file again later
    addFiles(list, null);
  };

  const removeFile = (index: number) => {
    setFiles((prev) => prev.filter((_, i) => i !== index));
    setPending(null);
  };
  const clearFiles = () => {
    setFiles([]);
    setPending(null);
    setOpen(false);
  };
  const changeLimit = (mb: number) => {
    try {
      window.localStorage.setItem(LIMIT_STORAGE_KEY, String(mb));
    } catch {
      /* ignore */
    }
    window.dispatchEvent(new Event(LIMIT_EVENT));
  };

  const pendingConverter = pending ? converterFor(pending.operation) : undefined;
  const centerTitle = engineLoading ? t("status.engine") : files.length === 1 ? files[0].name : t("wheel.center", { count: files.length });
  const centerSub = engineFailed ? t("status.engineFailed") : running.length ? t("status.running") : undefined;
  const pageLabel = pages.length > 1 ? ` ${t("sr.wheelPage", { page: pageIndex + 1, total: pages.length })}` : "";

  const limitLabel = (mb: number) => (mb === 0 ? t("big.off") : mb >= 1024 ? t("big.gb", { n: mb / 1024 }) : t("big.mb", { n: mb }));

  return (
    <>
      <a
        href="#app-main"
        className="sr-only focus:not-sr-only focus:fixed focus:inset-s-4 focus:top-4 focus:z-50 focus:rounded-lg focus:bg-primary focus:px-4 focus:py-2 focus:text-on-primary"
      >
        {tc("skip")}
      </a>
      <header className="sticky top-0 z-30 border-b border-border/60 bg-bg/70 backdrop-blur-xl">
        <div className="mx-auto flex h-16 max-w-6xl items-center justify-between gap-3 px-4 sm:px-6">
          <a href={`/${locale}`} aria-label={t("home")} className="rounded-lg">
            <Logo />
          </a>
          <div className="flex items-center gap-2">
            {!pwa.online ? (
              <span data-testid="offline-badge" className="rounded-full bg-surface-2 px-3 py-1 text-sm font-semibold text-(--warn)">
                {t("offline.badge")}
              </span>
            ) : null}
            {pwa.update ? (
              <button type="button" onClick={pwa.applyUpdate} data-testid="update-button" className="min-h-11 rounded-full bg-primary px-4 text-sm font-bold text-on-primary">
                {t("offline.update")}
              </button>
            ) : null}
            <GitHubLink />
            <nav aria-label={tc("language")} className="inline-flex rounded-full border border-border bg-surface p-0.5">
              {(["ar", "en"] as const).map((l) => (
                <a
                  key={l}
                  href={`/${l}/app`}
                  lang={l}
                  hrefLang={l}
                  aria-current={l === locale ? "page" : undefined}
                  className={`grid min-h-11 min-w-12 place-items-center rounded-full px-3 text-sm font-semibold transition-colors duration-200 ${l === locale ? "bg-primary text-on-primary" : "text-muted hover:text-fg"}`}
                >
                  {l === "ar" ? "ع" : "EN"}
                </a>
              ))}
            </nav>
          </div>
        </div>
      </header>

      <main id="app-main" className="mx-auto max-w-6xl px-4 pb-28 pt-6 sm:px-6" tabIndex={-1}>
        <h1 className="sr-only">{t("meta.title")}</h1>

        {link || linkError ? (
          <div role="status" aria-live="polite" data-testid="link-status" className="mx-auto mb-4 flex max-w-2xl flex-wrap items-center justify-center gap-3 rounded-2xl border border-border bg-surface px-4 py-3 text-sm">
            {link ? (
              <>
                <span className="font-semibold">
                  {t("link.downloading")}{" "}
                  <span dir="ltr">
                    {formatBytes(link.received)}
                    {link.total ? ` / ${formatBytes(link.total)}` : ""}
                  </span>
                </span>
                <button type="button" onClick={() => linkAbort.current?.abort()} data-testid="link-cancel" className="min-h-11 rounded-full border border-border px-4 font-semibold">
                  {t("link.cancel")}
                </button>
              </>
            ) : (
              <span data-testid="link-error" className="font-semibold text-(--danger)">
                {linkError}
              </span>
            )}
          </div>
        ) : null}

        {files.length === 0 ? (
          <section aria-label={t("drop.zone")} data-testid="dropzone" data-over={drag ? "true" : undefined} className="drop-card mx-auto grid max-w-2xl justify-items-center gap-6 rounded-4xl px-6 py-14 text-center sm:py-20">
            <div className="relative grid size-40 place-items-center">
              <svg viewBox="0 0 400 400" className="orbit absolute inset-0 size-full" aria-hidden="true">
                <circle cx="200" cy="200" r="190" fill="none" stroke="var(--border)" strokeWidth="3" strokeDasharray="3 12" strokeLinecap="round" />
              </svg>
              <svg viewBox="0 0 48 48" className="size-20" aria-hidden="true">
                {Array.from({ length: 6 }, (_, i) => {
                  const a0 = ((i * 60 - 90 + 4) * Math.PI) / 180;
                  const a1 = (((i + 1) * 60 - 90 - 4) * Math.PI) / 180;
                  const p = (r: number, a: number) => `${(24 + r * Math.cos(a)).toFixed(2)} ${(24 + r * Math.sin(a)).toFixed(2)}`;
                  return (
                    <path
                      key={i}
                      d={`M ${p(9, a0)} L ${p(22, a0)} A 22 22 0 0 1 ${p(22, a1)} L ${p(9, a1)} A 9 9 0 0 0 ${p(9, a0)} Z`}
                      fill={i === 0 ? "var(--accent)" : "var(--primary)"}
                      opacity={i === 0 ? 1 : 0.9}
                    />
                  );
                })}
                <path d="M24 17c3 3.6 4.5 5.8 4.5 7.8a4.5 4.5 0 0 1-9 0c0-2 1.5-4.2 4.5-7.8Z" fill="var(--bg)" />
              </svg>
            </div>
            <p className="text-4xl font-bold tracking-tight sm:text-5xl">{t("drop.title")}</p>
            <div className="flex flex-wrap items-center justify-center gap-3">
              <button
                type="button"
                onClick={() => inputRef.current?.click()}
                data-testid="choose-files"
                className="min-h-13 rounded-2xl bg-primary px-7 text-base font-bold text-on-primary shadow-lg shadow-primary/25"
              >
                {t("drop.choose")}
              </button>
              <span className="text-muted">{t("drop.paste")}</span>
              <kbd dir="ltr" className="rounded-lg border border-border bg-surface px-2 py-1 font-mono text-sm">
                Ctrl + V
              </kbd>
            </div>
            <form
              data-testid="link-form"
              className="flex w-full max-w-md flex-wrap items-center justify-center gap-2"
              onSubmit={(e) => {
                e.preventDefault();
                void getLink(linkText, null);
              }}
            >
              <label htmlFor="link-input" className="sr-only">
                {t("link.label")}
              </label>
              <input
                id="link-input"
                type="url"
                inputMode="url"
                dir="ltr"
                autoComplete="off"
                spellCheck={false}
                value={linkText}
                onChange={(e) => setLinkText(e.target.value)}
                placeholder={t("link.placeholder")}
                data-testid="link-input"
                className="min-h-11 min-w-0 flex-1 rounded-xl border border-border bg-surface px-3 text-sm text-fg placeholder:text-muted"
              />
              <button type="submit" disabled={!linkText.trim() || !!link} data-testid="link-get" className="min-h-11 rounded-xl border border-border bg-surface px-4 text-sm font-semibold text-fg disabled:opacity-60">
                {t("link.get")}
              </button>
            </form>
            <ul className="flex flex-wrap justify-center gap-2" dir="ltr" aria-label={t("wheel.picker")}>
              {WHEEL_ORDER.map((k) => (
                <li key={k} className="flex flex-col items-center rounded-2xl border border-border bg-surface px-4 py-2">
                  <kbd className="font-mono text-xs text-muted">{t(`keys.${KEY_OF[k]}`)}</kbd>
                  <span className="text-sm font-semibold">{t(`wheels.${k}`)}</span>
                </li>
              ))}
            </ul>
            <p className="flex items-center gap-2 text-sm font-medium text-muted">
              <svg viewBox="0 0 24 24" className="size-4 text-(--ok)" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <rect x="4" y="11" width="16" height="10" rx="2" />
                <path d="M8 11V7a4 4 0 0 1 8 0v4" />
              </svg>
              {t("drop.privacy")}
            </p>
          </section>
        ) : (
          <div className="grid grid-cols-[minmax(0,1fr)] gap-6">
            <section aria-label={t("files.list")} className="flex flex-wrap items-center gap-2">
              <ul className="contents">
                {files.map((f, i) => (
                  <li key={`${f.name}|${f.size}|${f.lastModified}`} data-testid="file-chip" className="glass flex max-w-full items-center gap-2 rounded-full py-1 ps-4 pe-1">
                    <span className="max-w-40 truncate font-medium sm:max-w-64" dir="auto" title={f.name}>
                      {f.name}
                    </span>
                    <span className="text-sm text-muted" dir="ltr">
                      {formatBytes(f.size)}
                    </span>
                    <button
                      type="button"
                      onClick={() => removeFile(i)}
                      aria-label={t("files.remove", { name: f.name })}
                      className="grid size-9 place-items-center rounded-full text-muted hover:bg-surface-2 hover:text-fg"
                    >
                      <CloseIcon />
                    </button>
                  </li>
                ))}
              </ul>
              <button type="button" onClick={() => inputRef.current?.click()} className="min-h-11 rounded-full border border-border bg-surface px-4 text-sm font-semibold">
                + {t("files.add")}
              </button>
              <button type="button" onClick={clearFiles} className="min-h-11 rounded-full px-3 text-sm font-semibold text-muted hover:text-fg">
                {t("files.clear")}
              </button>
            </section>

            {bigFiles.length > 0 ? (
              <p role="status" data-testid="big-warning" className="flex flex-wrap items-center gap-x-2 rounded-2xl bg-surface-2 px-4 py-2.5 text-sm font-semibold text-(--warn)">
                <svg viewBox="0 0 24 24" className="size-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                  <path d="M12 3 2 21h20L12 3Z" />
                  <path d="M12 10v5M12 18.5v.01" />
                </svg>
                <span dir="auto">{bigFiles[0].name}</span>
                <span dir="ltr">{formatBytes(bigFiles[0].size)}</span>
                {bigFiles.length > 1 ? <span dir="ltr">+{bigFiles.length - 1}</span> : null}
                <span>· {t("big.slow")}</span>
              </p>
            ) : null}

            <div className="grid grid-cols-[minmax(0,1fr)] gap-8 lg:grid-cols-[minmax(0,28rem)_minmax(0,1fr)] lg:items-start lg:gap-12">
              <div>
                {pending && pendingConverter ? (
                  <OptionsPanel
                    key={pending.id}
                    operation={pending.operation}
                    title={labelOf(pending)}
                    options={pendingConverter.options(pending.operation)}
                    warning={pendingConverter.warning?.(pending.operation)}
                    onRun={(values) => start(pending, values)}
                    onCancel={() => {
                      setPending(null);
                      setFocusToken((n) => n + 1);
                    }}
                  />
                ) : open ? (
                  <>
                    <Wheel
                      items={items}
                      label={`${t(`wheels.${activeKind}`)}${pageLabel}`}
                      wheelKey={`${activeKind}:${pageIndex}:${wedges.map((w) => w.id).join(",")}`}
                      busyIds={busyIds}
                      onPick={onPick}
                      title={centerTitle}
                      subtitle={centerSub}
                      progress={progress}
                      loading={engineLoading}
                      focusToken={focusToken}
                    />
                    {wedges.length === 0 && !engineLoading ? <p className="mt-2 text-center font-semibold text-muted">{t("wheel.empty")}</p> : null}
                  </>
                ) : (
                  <div className="glass mx-auto grid aspect-square w-full max-w-md place-items-center rounded-full">
                    <button
                      type="button"
                      onClick={() => {
                        setOpen(true);
                        setFocusToken((n) => n + 1);
                      }}
                      data-testid="open-wheel"
                      className="min-h-13 rounded-2xl bg-primary px-7 text-base font-bold text-on-primary shadow-lg shadow-primary/25"
                    >
                      {t("wheel.open")}
                    </button>
                  </div>
                )}

                {engineFailed ? (
                  <p className="mt-3 flex items-center justify-center gap-3 text-sm font-semibold text-(--danger)" role="alert">
                    {t("status.engineFailed")}
                    <button
                      type="button"
                      onClick={() => {
                        setEngineFailed(false);
                        setRetry((n) => n + 1);
                      }}
                      className="min-h-11 rounded-full border border-border bg-surface px-4 text-fg"
                    >
                      {t("status.retry")}
                    </button>
                  </p>
                ) : null}

                <div role="group" aria-label={t("wheel.picker")} className="mx-auto mt-3 grid max-w-md grid-cols-3 gap-1.5 rounded-2xl bg-surface-2 p-1.5">
                  {WHEEL_ORDER.map((k) => (
                    <button
                      key={k}
                      type="button"
                      aria-pressed={activeKind === k}
                      disabled={wheels[k].length === 0}
                      data-wheel={k}
                      onClick={() => {
                        selectWheel(k);
                        setOpen(true);
                      }}
                      className={`flex min-h-14 flex-col items-center justify-center rounded-xl px-2 py-1.5 transition-colors duration-200 disabled:cursor-not-allowed disabled:opacity-45 ${
                        activeKind === k ? "bg-primary text-on-primary shadow-sm" : "text-muted hover:text-fg"
                      }`}
                    >
                      <kbd className="font-mono text-xs opacity-80">{t(`keys.${KEY_OF[k]}`)}</kbd>
                      <span className="text-sm font-semibold">{t(`wheels.${k}`)}</span>
                    </button>
                  ))}
                </div>

              </div>

              <div ref={activityRef} className="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-6">
                {active ? (
                  <div className="flex justify-end">
                    <button type="button" onClick={() => session?.cancelAll()} className="min-h-11 rounded-full border border-border bg-surface px-4 text-sm font-semibold">
                      {t("stop")}
                    </button>
                  </div>
                ) : null}
                <JobList jobs={snap.jobs} onCancel={(id) => session?.cancel(id)} onDismiss={(id) => session?.removeJob(id)} />
                <ResultList
                  results={results}
                  onRemove={(id) => session?.removeResult(id)}
                  onDownloadAll={() => void runAction(WEB_ACTIONS.download)}
                  onClear={() => session?.clearResults()}
                  onToast={say}
                />
                {hint ? <WindowsHintTile hint={hint} /> : null}
              </div>
            </div>
          </div>
        )}

        {files.length === 0 ? (
          <div className="mx-auto mt-8 grid max-w-2xl grid-cols-[minmax(0,1fr)] gap-6">
            <JobList jobs={snap.jobs} onCancel={(id) => session?.cancel(id)} onDismiss={(id) => session?.removeJob(id)} />
            <ResultList
              results={results}
              onRemove={(id) => session?.removeResult(id)}
              onDownloadAll={() => void runAction(WEB_ACTIONS.download)}
              onClear={() => session?.clearResults()}
              onToast={say}
            />
          </div>
        ) : null}
      </main>

      <footer className="border-t border-border">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-x-6 gap-y-2 px-4 py-4 text-sm text-muted sm:px-6">
          <p className="flex items-center gap-2 font-medium">
            <svg viewBox="0 0 24 24" className="size-4 text-(--ok)" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <rect x="4" y="11" width="16" height="10" rx="2" />
              <path d="M8 11V7a4 4 0 0 1 8 0v4" />
            </svg>
            {t("privacy")}
            {pwa.ready ? <span data-testid="offline-ready"> · {t("offline.ready")}</span> : null}
          </p>
          <div className="flex flex-wrap items-center gap-3">
            {pwa.ready && pwa.media === "missing" ? (
              <button type="button" onClick={pwa.saveMedia} data-testid="save-media" className="min-h-11 rounded-full border border-border bg-surface px-4 font-semibold text-fg">
                {t("offline.save")}
              </button>
            ) : null}
            {pwa.media === "saving" ? <span>{t("offline.saving")}</span> : null}
            {pwa.media === "saved" ? <span data-testid="media-saved">{t("offline.saved")}</span> : null}
            <Link href="/privacy" prefetch={false} data-testid="app-privacy" className="inline-flex min-h-11 items-center underline-offset-4 hover:text-fg hover:underline">
              {t("legal.privacy")}
            </Link>
            <Link href="/terms" prefetch={false} data-testid="app-terms" className="inline-flex min-h-11 items-center underline-offset-4 hover:text-fg hover:underline">
              {t("legal.terms")}
            </Link>
            <label className="flex items-center gap-2">
              <span>{t("big.limit")}</span>
              <select
                value={bigMb}
                onChange={(e) => changeLimit(Number(e.target.value))}
                data-testid="limit-select"
                className="min-h-11 rounded-xl border border-border bg-surface px-2 text-fg"
              >
                {LIMITS_MB.map((mb) => (
                  <option key={mb} value={mb}>
                    {limitLabel(mb)}
                  </option>
                ))}
              </select>
            </label>
          </div>
        </div>
      </footer>

      <input ref={inputRef} type="file" multiple hidden onChange={pickFiles} data-testid="file-input" aria-label={t("drop.choose")} />
      {drag ? <DropOverlay kind={drag.kind} /> : null}
      <div aria-live="polite" role="status" className="sr-only" data-testid="live">
        {toast}
      </div>
      {toast ? (
        <div aria-hidden="true" className="overlay-in pointer-events-none fixed inset-x-0 bottom-6 z-40 grid place-items-center">
          <span className="glass rounded-full px-5 py-2.5 text-sm font-bold shadow-xl">{toast}</span>
        </div>
      ) : null}
    </>
  );
}
