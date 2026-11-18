import { useTranslations } from "next-intl";

// "How it works": numbered steps on a vertical gradient line, each with a small looping CSS/SVG scene (decorative, aria-hidden).
// Only steps that the released app really does are listed (STEPS); add new ones here when they ship.

export const STEPS = ["drag", "key", "pick", "done"] as const;
type Step = (typeof STEPS)[number];

function FileIcon({ label, tone = "primary" }: { label: string; tone?: "primary" | "mint" }) {
  return (
    <div className="relative h-20 w-16 rounded-xl border border-border bg-surface shadow-soft">
      <div className="absolute inset-x-2.5 top-3 space-y-1.5">
        <div className="h-1 rounded bg-border" />
        <div className="h-1 rounded bg-border" />
        <div className="h-1 w-2/3 rounded bg-border" />
      </div>
      <span
        dir="ltr"
        className={`absolute -start-2 bottom-2.5 rounded-md px-1.5 py-0.5 font-mono text-[10px] font-semibold ${tone === "mint" ? "bg-accent text-on-ok" : "bg-primary text-on-primary"}`}
      >
        {label}
      </span>
    </div>
  );
}

function Cursor() {
  return (
    <svg width="22" height="22" viewBox="0 0 24 24" className="drop-shadow" aria-hidden="true">
      <path d="M4 3l15 7-6.5 2L10 19z" fill="var(--fg)" stroke="var(--surface)" strokeWidth="1.5" strokeLinejoin="round" />
    </svg>
  );
}

function MiniWheel({ active }: { active: number }) {
  const wedges = Array.from({ length: 6 }, (_, i) => {
    const a0 = ((i * 60 - 90 + 3) * Math.PI) / 180;
    const a1 = (((i + 1) * 60 - 90 - 3) * Math.PI) / 180;
    const p = (r: number, a: number) => `${(60 + r * Math.cos(a)).toFixed(1)} ${(60 + r * Math.sin(a)).toFixed(1)}`;
    return `M ${p(22, a0)} L ${p(54, a0)} A 54 54 0 0 1 ${p(54, a1)} L ${p(22, a1)} A 22 22 0 0 0 ${p(22, a0)} Z`;
  });
  return (
    <svg viewBox="0 0 120 120" className="wheel-pop size-32">
      {wedges.map((d, i) => (
        <path key={i} d={d} fill={i === active ? "var(--primary)" : "var(--surface-2)"} stroke="var(--surface)" strokeWidth="3" />
      ))}
      <circle cx="60" cy="60" r="18" fill="var(--surface)" stroke="var(--border)" />
    </svg>
  );
}

function Scene({ step }: { step: Step }) {
  switch (step) {
    case "drag":
      return (
        <div dir="ltr" className="relative flex h-full items-center justify-center">
          <div className="step-drag flex items-end gap-1">
            <FileIcon label="HEIC" />
            <Cursor />
          </div>
        </div>
      );
    case "key":
      return (
        <div dir="ltr" className="flex h-full items-center justify-center gap-2.5">
          {["Shift", "Ctrl", "Alt"].map((k, i) => (
            <kbd
              key={k}
              style={{ "--d": `${i * 1.1}s` } as React.CSSProperties}
              className="step-key rounded-xl border border-border border-b-4 bg-surface px-3.5 py-2 font-mono text-sm font-semibold"
            >
              {k}
            </kbd>
          ))}
        </div>
      );
    case "pick":
      return (
        <div className="flex h-full items-center justify-center">
          <MiniWheel active={1} />
        </div>
      );
    case "done":
      return (
        <div dir="ltr" className="flex h-full items-center justify-center gap-5">
          <FileIcon label="HEIC" />
          <div className="relative">
            <FileIcon label="JPG" tone="mint" />
            <span className="step-check absolute -end-2.5 -top-2.5 flex size-7 items-center justify-center rounded-full bg-accent text-on-ok shadow-soft">
              <svg width="15" height="15" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="m3.5 8.5 3 3 6-7" />
              </svg>
            </span>
          </div>
        </div>
      );
  }
}

export function Steps() {
  const t = useTranslations("steps");
  return (
    <section id="how" className="mx-auto max-w-5xl px-4 py-16 sm:px-6 lg:py-24" data-testid="steps">
      <h2 className="reveal text-center text-4xl font-bold tracking-tight sm:text-6xl">{t("title")}</h2>
      <ol className="relative mt-14 grid gap-10 md:gap-14">
        <span aria-hidden="true" className="step-line absolute inset-y-4 start-6 w-0.5 md:start-1/2 md:-translate-x-1/2 rtl:md:translate-x-1/2" />
        {STEPS.map((s, i) => (
          <li key={s} className="reveal relative grid items-center gap-5 ps-16 md:grid-cols-2 md:gap-16 md:ps-0">
            <span
              aria-hidden="true"
              className="step-dot absolute start-0 top-0 flex size-12 items-center justify-center rounded-2xl text-lg font-bold text-on-primary md:start-1/2 md:top-1/2 md:-translate-x-1/2 md:-translate-y-1/2 rtl:md:translate-x-1/2"
            >
              {String(i + 1).padStart(2, "0")}
            </span>
            <div className={i % 2 ? "md:order-2 md:ps-10" : "md:pe-10 md:text-end"}>
              <h3 className="text-2xl font-bold tracking-tight sm:text-3xl">{t(`${s}.title`)}</h3>
              <p className="mt-1 text-lg text-muted">{t(`${s}.text`)}</p>
            </div>
            <div aria-hidden="true" className={`glass h-44 rounded-4xl ${i % 2 ? "md:order-1" : ""}`}>
              <Scene step={s} />
            </div>
          </li>
        ))}
      </ol>
    </section>
  );
}
