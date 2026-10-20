"use client";

import { useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { categories, name, toolsOf } from "@/lib/catalog";
import type { Locale } from "@/i18n/routing";

const FORMAT_LABEL: Record<string, string> = { pdfa: "PDF/A", gz: "GZ", tgz: "TGZ" };
const fmt = (f: string) => FORMAT_LABEL[f] ?? f;

function Chip({ children, muted = false }: { children: React.ReactNode; muted?: boolean }) {
  return (
    <span
      dir="ltr"
      className={`inline-flex min-h-8 items-center rounded-lg border px-2.5 font-mono text-xs uppercase ${
        muted ? "border-dashed border-border text-muted" : "border-border bg-surface-2 text-fg"
      }`}
    >
      {children}
    </span>
  );
}

export function CategoryExplorer() {
  const t = useTranslations("explorer");
  const tc = useTranslations("common");
  const locale = useLocale() as Locale;
  const [active, setActive] = useState(categories[0].id);
  const tabRefs = useRef<Record<string, HTMLButtonElement | null>>({});

  const cat = categories.find((c) => c.id === active) ?? categories[0];
  const catTools = toolsOf(cat.id);

  function onKeyDown(e: React.KeyboardEvent, index: number) {
    const dir = (document.documentElement.dir === "rtl" ? -1 : 1) * (e.key === "ArrowRight" ? 1 : e.key === "ArrowLeft" ? -1 : 0);
    if (!dir) return;
    e.preventDefault();
    const next = categories[(index + dir + categories.length) % categories.length];
    setActive(next.id);
    tabRefs.current[next.id]?.focus();
  }

  return (
    <div>
      <div role="tablist" aria-label={t("tablist")} className="flex flex-wrap gap-2">
        {categories.map((c, i) => (
          <button
            key={c.id}
            ref={(el) => { tabRefs.current[c.id] = el; }}
            id={`tab-${c.id}`}
            role="tab"
            type="button"
            aria-selected={active === c.id}
            aria-controls={`panel-${c.id}`}
            tabIndex={active === c.id ? 0 : -1}
            onClick={() => setActive(c.id)}
            onKeyDown={(e) => onKeyDown(e, i)}
            className={`min-h-11 rounded-full border px-4 text-sm font-semibold transition-colors duration-200 ${
              active === c.id ? "border-primary bg-primary text-on-primary" : "border-border bg-surface text-muted hover:text-fg"
            }`}
          >
            {name(c, locale)}
          </button>
        ))}
      </div>

      <div id={`panel-${cat.id}`} role="tabpanel" aria-labelledby={`tab-${cat.id}`} tabIndex={0} className="mt-6 grid gap-6 rounded-3xl border border-border bg-surface p-6 sm:p-8 lg:grid-cols-2">
        <div>
          <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">{t("convertTo")}</h3>
          <div className="mt-3 flex flex-wrap gap-2">
            {cat.outputs.map((o) => {
              const late = cat.lateOutputs?.[o];
              return (
                <Chip key={o} muted={Boolean(late)}>
                  {fmt(o)}
                  {late ? <span className="sr-only"> — {t("laterOutput", { format: o.toUpperCase(), n: late })}</span> : null}
                </Chip>
              );
            })}
          </div>
          <h3 className="mt-6 text-sm font-semibold uppercase tracking-wide text-muted">{t("acceptsFrom")}</h3>
          <div className="mt-3 flex flex-wrap gap-2">
            {cat.inputs.map((o) => (
              <Chip key={o}>{fmt(o)}</Chip>
            ))}
          </div>
          <p className="mt-4 text-sm text-muted">
            {tc("phase", { n: cat.phase })} · {cat.platforms.map((p) => tc(p)).join(" · ")}
          </p>
        </div>

        <div>
          <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">{t("tools")}</h3>
          {catTools.length === 0 ? (
            <p className="mt-3 text-muted">{t("noTools")}</p>
          ) : (
            <ul className="mt-3 grid gap-2 sm:grid-cols-2">
              {catTools.map((tool) => (
                <li key={tool.id} className="flex items-start justify-between gap-2 rounded-xl bg-surface-2 px-3 py-2 text-sm">
                  <span>
                    {name(tool, locale)}
                    {tool.multi ? <span className="ms-1.5 text-xs text-muted">· {t("multi")}</span> : null}
                  </span>
                  <span className="shrink-0 text-xs text-muted">{tc("phase", { n: tool.phase })}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </div>
  );
}
