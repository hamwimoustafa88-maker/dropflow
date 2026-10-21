"use client";

import { useTranslations } from "next-intl";
import { WHEEL_ORDER, type WheelKind } from "@/engine/wheel";

const KEYS: Record<WheelKind, "shift" | "ctrl" | "alt"> = { convert: "shift", tools: "ctrl", actions: "alt" };

// Full-page overlay while a file is dragged over the window. pointer-events: none keeps the drop target = window.
// The highlighted chip shows which wheel the CURRENT modifier keys will open (Shift / Ctrl / Alt held at drop time).
export function DropOverlay({ kind }: { kind: WheelKind }) {
  const t = useTranslations("app");
  return (
    <div
      aria-hidden="true"
      data-testid="drop-overlay"
      data-kind={kind}
      className="overlay-in pointer-events-none fixed inset-0 z-50 grid place-items-center bg-bg/80 p-6 backdrop-blur-md"
    >
      <div className="grid justify-items-center gap-6">
        <div className="grid size-40 place-items-center rounded-full border-2 border-dashed border-primary bg-primary-soft">
          <svg viewBox="0 0 48 48" className="size-20 text-primary" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round">
            <path d="M24 6v24M14 22l10 10 10-10M8 40h32" />
          </svg>
        </div>
        <p className="text-4xl font-bold">{t("over.title")}</p>
        <div className="flex flex-wrap justify-center gap-2" dir="ltr">
          {WHEEL_ORDER.map((k) => (
            <span
              key={k}
              className={`flex flex-col items-center rounded-2xl border px-4 py-2 ${k === kind ? "border-primary bg-primary text-on-primary" : "border-border bg-surface text-muted"}`}
            >
              <kbd className="font-mono text-xs opacity-80">{t(`keys.${KEYS[k]}`)}</kbd>
              <span className="text-sm font-semibold">{t(`wheels.${k}`)}</span>
            </span>
          ))}
        </div>
      </div>
    </div>
  );
}
