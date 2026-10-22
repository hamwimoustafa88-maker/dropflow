"use client";

import { useRef, useState } from "react";
import { useTranslations } from "next-intl";

// One shell command with a copy button. The command is always left-to-right (also on the Arabic page) and wraps instead of
// scrolling, so a phone never scrolls sideways. Copying uses the Clipboard API (no network); if the browser refuses it,
// the command is selected so Ctrl+C / long-press copies it. Without JavaScript the text is still selectable (select-all).
export function CopyCode({ code, label }: { code: string; label: string }) {
  const t = useTranslations("developers");
  const [done, setDone] = useState(false);
  const ref = useRef<HTMLElement>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const flash = () => {
    setDone(true);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setDone(false), 1800);
  };
  const selectText = () => {
    const el = ref.current;
    const sel = window.getSelection();
    if (!el || !sel) return;
    const range = document.createRange();
    range.selectNodeContents(el);
    sel.removeAllRanges();
    sel.addRange(range);
  };
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      flash();
    } catch {
      selectText();
    }
  };

  return (
    <div dir="ltr" className="flex items-start gap-2 rounded-2xl border border-border bg-surface py-1 pe-1 ps-4">
      <span aria-hidden="true" className="select-none py-2.5 font-mono text-sm leading-relaxed text-muted">
        $
      </span>
      <code ref={ref} data-testid="dev-code" className="min-w-0 flex-1 select-all py-2.5 text-start font-mono text-sm leading-relaxed text-fg wrap-anywhere">
        {code}
      </code>
      <button
        type="button"
        onClick={copy}
        aria-label={t("copyLabel", { label })}
        data-testid="dev-copy"
        data-copied={done}
        className="inline-flex size-11 shrink-0 items-center justify-center rounded-xl text-muted transition-colors duration-200 hover:bg-surface-2 hover:text-fg data-[copied=true]:text-accent"
      >
        {done ? (
          <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
            <path d="M5 12.5l4.5 4.5L19 7.5" />
          </svg>
        ) : (
          <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <rect x="8" y="8" width="12" height="12" rx="2.5" />
            <path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2" />
          </svg>
        )}
      </button>
      <span role="status" className="sr-only">
        {done ? t("copied") : ""}
      </span>
    </div>
  );
}
