"use client";

import { useEffect, useId, useRef } from "react";
import { useTranslations } from "next-intl";
import type { Wedge, WheelKind } from "@/engine/wheel";

export interface AddRow {
  wedge: Wedge;
  label: string;
  /** Short explanation shown after the label (e.g. "Text (OCR)" for TXT on a picture). */
  hint?: string;
}

interface Props {
  /** The wheel items are added to. */
  wheel: WheelKind;
  /** Everything these files can do that is not on this wheel, grouped by family. */
  groups: Record<WheelKind, AddRow[]>;
  /** Items already added to this wheel (removable). */
  pinned: AddRow[];
  onPick: (wedge: Wedge) => void;
  onUnpin: (wedge: Wedge) => void;
  onBack: () => void;
}

const FAMILIES: readonly WheelKind[] = ["convert", "tools", "actions"];

// The second level of "+ Add" (mirrors the desktop AddListPanel): a compact list that takes the wheel's place. Groups are
// headed Convert to / Tools / Actions; picking a row runs it now and keeps it on this wheel for this kind of file. Rows
// already added sit on top with a remove button. Keyboard: Up/Down/Home/End move, Enter/Space pick, Esc goes back.
export function AddList({ wheel, groups, pinned, onPick, onUnpin, onBack }: Props) {
  const t = useTranslations("app");
  const headingId = useId();
  const root = useRef<HTMLElement>(null);

  useEffect(() => {
    root.current?.querySelector<HTMLElement>("[data-add-row]")?.focus();
  }, []);

  function onKeyDown(e: React.KeyboardEvent) {
    const rows = [...(root.current?.querySelectorAll<HTMLElement>("[data-add-row]") ?? [])];
    if (rows.length === 0) return;
    const i = rows.indexOf(document.activeElement as HTMLElement);
    let to: number | null = null;
    if (e.key === "ArrowDown") to = i < 0 ? 0 : (i + 1) % rows.length;
    else if (e.key === "ArrowUp") to = i < 0 ? rows.length - 1 : (i - 1 + rows.length) % rows.length;
    else if (e.key === "Home") to = 0;
    else if (e.key === "End") to = rows.length - 1;
    if (to !== null) {
      e.preventDefault();
      rows[to].focus();
    }
  }

  const rowClass =
    "flex min-h-11 w-full min-w-0 items-center gap-2 rounded-xl px-3 py-2 text-start transition-colors duration-150 hover:bg-surface-2 focus-visible:bg-surface-2 focus-visible:outline-2 focus-visible:outline-(--ring)";
  const label = (r: AddRow) => (
    <span className="flex min-w-0 flex-1 flex-wrap items-baseline gap-x-2">
      <span className={`font-semibold ${r.wedge.wheel === "convert" ? "font-mono" : ""}`} dir={r.wedge.wheel === "convert" ? "ltr" : undefined}>
        {r.label}
      </span>
      {r.hint ? <span className="text-sm text-muted">{r.hint}</span> : null}
    </span>
  );
  const empty = FAMILIES.every((f) => groups[f].length === 0);

  return (
    <section
      ref={root}
      aria-labelledby={headingId}
      data-testid="add-list"
      onKeyDown={onKeyDown}
      className="glass wheel-pop mx-auto w-full max-w-md rounded-3xl p-4 shadow-xl shadow-primary/10"
    >
      <div className="flex items-center justify-between gap-3">
        <h2 id={headingId} className="min-w-0 truncate text-lg font-bold">
          {t("add.title", { wheel: t(`wheels.${wheel}`) })}
        </h2>
        <button type="button" onClick={onBack} data-testid="add-back" className="min-h-11 shrink-0 rounded-full border border-border bg-surface px-4 text-sm font-semibold">
          {t("add.back")}
        </button>
      </div>
      <p className="mt-1 text-sm text-muted">{t("add.note")}</p>

      <div className="mt-3 grid max-h-[26rem] gap-3 overflow-y-auto pe-1">
        {pinned.length > 0 ? (
          <div>
            <h3 className="px-3 text-xs font-bold uppercase tracking-wide text-muted">{t("add.pinned")}</h3>
            <ul className="mt-1 grid gap-0.5">
              {pinned.map((r) => (
                <li key={r.wedge.id} className="flex items-center gap-1">
                  <span className="flex min-h-11 min-w-0 flex-1 items-center px-3">{label(r)}</span>
                  <button
                    type="button"
                    data-add-row
                    data-unpin={r.wedge.id}
                    onClick={() => onUnpin(r.wedge)}
                    aria-label={t("add.remove", { name: r.label })}
                    className="grid size-11 shrink-0 place-items-center rounded-full text-muted hover:bg-surface-2 hover:text-fg focus-visible:outline-2 focus-visible:outline-(--ring)"
                  >
                    <svg viewBox="0 0 24 24" className="size-4" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" aria-hidden="true">
                      <path d="M6 6l12 12M18 6L6 18" />
                    </svg>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
        {FAMILIES.map((family) =>
          groups[family].length > 0 ? (
            <div key={family}>
              <h3 className="px-3 text-xs font-bold uppercase tracking-wide text-muted">{t(`add.group.${family}`)}</h3>
              <ul className="mt-1 grid gap-0.5">
                {groups[family].map((r) => (
                  <li key={r.wedge.id}>
                    <button type="button" data-add-row data-add={r.wedge.id} onClick={() => onPick(r.wedge)} className={rowClass}>
                      <span aria-hidden="true" className="font-bold text-primary">
                        +
                      </span>
                      {label(r)}
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          ) : null,
        )}
        {empty && pinned.length === 0 ? <p className="px-3 text-sm font-semibold text-muted">{t("wheel.empty")}</p> : null}
      </div>
    </section>
  );
}
