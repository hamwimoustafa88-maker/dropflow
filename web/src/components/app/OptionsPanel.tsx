"use client";

import { useEffect, useId, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { choiceKey, opKey } from "@/engine/catalog-web";
import { initialDraft, readValues } from "@/engine/option-values";
import type { OptionValues, ToolOption } from "@/engine/types";

interface Props {
  /** Operation id, used for per-operation label overrides. */
  operation: string;
  title: string;
  options: readonly ToolOption[];
  /** `app.warn.*` key of a short warning (e.g. "textToImages"), shown above the buttons. */
  warning?: string;
  onRun: (values: OptionValues) => void;
  onCancel: () => void;
}

export function OptionsPanel({ operation, title, options, warning, onRun, onCancel }: Props) {
  const t = useTranslations("app");
  const formId = useId();
  const [draft, setDraft] = useState<Record<string, string | boolean>>(() => initialDraft(options));
  const first = useRef<HTMLElement | null>(null);

  useEffect(() => {
    first.current?.focus();
  }, []);

  const values = readValues(options, draft);
  const set = (key: string, value: string | boolean) => setDraft((d) => ({ ...d, [key]: value }));

  const label = (key: string) => {
    const per = `optOp.${opKey(operation)}.${key}`;
    if (t.has(per)) return t(per);
    return t.has(`opt.${key}`) ? t(`opt.${key}`) : key;
  };
  const choiceLabel = (key: string, value: string) => {
    const k = `choice.${key}.${choiceKey(value)}`;
    if (t.has(k)) return t(k);
    return key === "speed" ? `${value}×` : value;
  };

  function submit(e: React.FormEvent) {
    e.preventDefault();
    if (values) onRun(values);
  }

  return (
    <form
      onSubmit={submit}
      aria-labelledby={`${formId}-heading`}
      data-testid="options-panel"
      className="glass wheel-pop mx-auto mt-4 w-full max-w-md rounded-3xl p-4 shadow-xl shadow-primary/10"
    >
      <h2 id={`${formId}-heading`} className="text-lg font-bold">
        {title}
      </h2>
      <div className="mt-3 grid gap-3">
        {options.map((o, idx) => {
          const id = `${formId}-${o.key}`;
          const setFirst = (el: HTMLElement | null) => {
            if (idx === 0) first.current = el;
          };
          return (
            <div key={o.key} className={o.kind === "bool" ? "flex items-center justify-between gap-3" : "grid gap-1.5"}>
              <label htmlFor={id} className="text-sm font-semibold text-muted">
                {label(o.key)}
                {o.kind === "range" ? <output className="ms-2 font-mono text-fg">{String(draft[o.key])}</output> : null}
              </label>
              {o.kind === "choice" ? (
                o.choices.length <= 4 ? (
                  <div role="radiogroup" aria-labelledby={id} id={id} className="flex flex-wrap gap-1.5" dir="ltr">
                    {o.choices.map((c, ci) => (
                      <button
                        key={c}
                        type="button"
                        role="radio"
                        aria-checked={draft[o.key] === c}
                        ref={ci === 0 ? setFirst : undefined}
                        onClick={() => set(o.key, c)}
                        className={`min-h-11 rounded-full border px-4 text-sm font-medium transition-colors duration-200 ${
                          draft[o.key] === c ? "border-primary bg-primary text-on-primary" : "border-border bg-surface hover:border-primary"
                        }`}
                      >
                        {choiceLabel(o.key, c)}
                      </button>
                    ))}
                  </div>
                ) : (
                  <select
                    id={id}
                    ref={setFirst}
                    value={String(draft[o.key])}
                    onChange={(e) => set(o.key, e.target.value)}
                    className="min-h-11 rounded-xl border border-border bg-surface px-3 text-base"
                  >
                    {o.choices.map((c) => (
                      <option key={c} value={c}>
                        {choiceLabel(o.key, c)}
                      </option>
                    ))}
                  </select>
                )
              ) : null}
              {o.kind === "number" ? (
                <input
                  id={id}
                  ref={setFirst}
                  type="number"
                  inputMode="decimal"
                  min={o.min}
                  max={o.max}
                  step={o.step ?? "any"}
                  value={String(draft[o.key])}
                  placeholder={t.has(`ph.${o.key}`) ? t(`ph.${o.key}`) : undefined}
                  onChange={(e) => set(o.key, e.target.value)}
                  dir="ltr"
                  className="min-h-11 rounded-xl border border-border bg-surface px-3 text-base"
                />
              ) : null}
              {o.kind === "range" ? (
                <input
                  id={id}
                  ref={setFirst}
                  type="range"
                  min={o.min}
                  max={o.max}
                  step={o.step ?? 1}
                  value={String(draft[o.key])}
                  onChange={(e) => set(o.key, e.target.value)}
                  dir="ltr"
                  className="min-h-11 w-full"
                />
              ) : null}
              {o.kind === "text" || o.kind === "password" ? (
                <input
                  id={id}
                  ref={setFirst}
                  type={o.kind === "password" ? "password" : "text"}
                  autoComplete="off"
                  spellCheck={false}
                  required={o.required}
                  value={String(draft[o.key])}
                  placeholder={t.has(`ph.${o.key}`) ? t(`ph.${o.key}`) : undefined}
                  onChange={(e) => set(o.key, e.target.value)}
                  dir="auto"
                  className="min-h-11 rounded-xl border border-border bg-surface px-3 text-base"
                />
              ) : null}
              {o.kind === "bool" ? (
                <input
                  id={id}
                  ref={setFirst}
                  type="checkbox"
                  role="switch"
                  checked={Boolean(draft[o.key])}
                  onChange={(e) => set(o.key, e.target.checked)}
                  className="switch"
                />
              ) : null}
            </div>
          );
        })}
      </div>
      {warning && t.has(`warn.${warning}`) ? (
        <p role="note" className="mt-3 rounded-xl bg-surface-2 px-3 py-2 text-sm font-semibold text-(--warn)">
          {t(`warn.${warning}`)}
        </p>
      ) : null}
      <div className="mt-4 flex gap-2">
        <button
          type="submit"
          disabled={!values}
          className="min-h-12 flex-1 rounded-2xl bg-primary px-5 text-base font-bold text-on-primary shadow-lg shadow-primary/25 transition-opacity disabled:cursor-not-allowed disabled:opacity-50"
        >
          {t("run")}
        </button>
        <button type="button" onClick={onCancel} className="min-h-12 rounded-2xl border border-border bg-surface px-5 text-base font-semibold">
          {t("cancel")}
        </button>
      </div>
    </form>
  );
}

