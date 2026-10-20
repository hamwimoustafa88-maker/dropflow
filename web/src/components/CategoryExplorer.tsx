"use client";

import { useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { categories, name, toolsOf } from "@/lib/catalog";
import type { Locale } from "@/i18n/routing";

const MAX_TOOLS = 8;
const FORMAT_LABEL: Record<string, string> = { pdfa: "PDF/A", gz: "GZ", tgz: "TGZ" };
const fmt = (f: string) => FORMAT_LABEL[f] ?? f;

function Chip({ children, strong = false }: { children: React.ReactNode; strong?: boolean }) {
  return (
    <span
      dir="ltr"
      className={`inline-flex min-h-9 items-center rounded-lg px-3 font-mono text-sm uppercase ${
        strong ? "bg-primary-soft text-fg" : "border border-border bg-surface text-muted"
      }`}
    >
      {children}
    </span>
  );
}

export function CategoryExplorer() {
  const t = useTranslations("formats");
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
      <div role="tablist" aria-label={t("tablist")} className="-mx-4 flex gap-2 overflow-x-auto px-4 pb-2 scrollbar-none sm:mx-0 sm:flex-wrap sm:overflow-visible sm:px-0">
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
            className={`min-h-11 shrink-0 rounded-full border px-5 text-sm font-semibold transition-colors duration-200 ${
              active === c.id ? "border-primary bg-primary text-on-primary" : "border-border bg-surface text-muted hover:text-fg"
            }`}
          >
            {name(c, locale)}
          </button>
        ))}
      </div>

      <div id={`panel-${cat.id}`} role="tabpanel" aria-labelledby={`tab-${cat.id}`} tabIndex={0} className="glass mt-5 rounded-4xl p-6 sm:p-10">
        <div className="flex flex-col items-start gap-5 lg:flex-row lg:items-center">
          <div className="flex flex-wrap gap-2 lg:max-w-[40%]">
            {cat.inputs.map((o) => (
              <Chip key={o}>{fmt(o)}</Chip>
            ))}
          </div>
          <svg aria-hidden="true" width="40" height="24" viewBox="0 0 40 24" fill="none" stroke="var(--primary)" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" className="shrink-0 rotate-90 rtl:-rotate-90 lg:rotate-0 lg:rtl:rotate-180">
            <path d="M3 12h32M26 4l9 8-9 8" />
          </svg>
          <div className="flex flex-wrap gap-2">
            {cat.outputs.map((o) => (
              <Chip key={o} strong>
                {fmt(o)}
              </Chip>
            ))}
          </div>
        </div>

        {catTools.length > 0 && (
          <div className="mt-8 border-t border-border pt-6">
            <h3 className="text-xs font-semibold uppercase tracking-widest text-muted">{t("tools")}</h3>
            <ul className="mt-3 flex flex-wrap gap-2">
              {catTools.slice(0, MAX_TOOLS).map((tool) => (
                <li key={tool.id} className="rounded-lg bg-surface-2 px-3 py-1.5 text-sm">
                  {name(tool, locale)}
                </li>
              ))}
              {catTools.length > MAX_TOOLS && (
                <li dir="ltr" className="rounded-lg bg-primary-soft px-3 py-1.5 font-mono text-sm font-semibold">
                  +{catTools.length - MAX_TOOLS}
                </li>
              )}
            </ul>
          </div>
        )}
      </div>
    </div>
  );
}
