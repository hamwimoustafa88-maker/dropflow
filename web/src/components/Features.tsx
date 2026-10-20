import { useTranslations } from "next-intl";

// Five tiles, almost no text. Each tile's visual is pure CSS/SVG and decorative (aria-hidden).

function PdfVisual() {
  return (
    <div className="relative h-full">
      {[
        { r: "-rotate-12", x: "start-6", z: "z-0", o: "opacity-60" },
        { r: "rotate-0", x: "start-14", z: "z-10", o: "opacity-80" },
        { r: "rotate-12", x: "start-22", z: "z-20", o: "" },
      ].map((p, i) => (
        <div key={i} className={`absolute top-3 h-28 w-20 rounded-xl border border-border bg-surface p-2.5 shadow-lg ${p.r} ${p.x} ${p.z} ${p.o}`}>
          <div className="h-1.5 w-8 rounded bg-primary/70" />
          <div className="mt-2 space-y-1.5">
            <div className="h-1 rounded bg-border" />
            <div className="h-1 rounded bg-border" />
            <div className="h-1 w-2/3 rounded bg-border" />
          </div>
        </div>
      ))}
      <div dir="ltr" className="absolute end-2 top-2 flex flex-col gap-2 font-mono text-xs font-semibold">
        {["DOCX", "XLSX", "JPG"].map((c, i) => (
          <span key={c} style={{ "--d": `${i * 0.5}s` } as React.CSSProperties} className="float rounded-lg bg-primary-soft px-2.5 py-1 text-fg">
            {c}
          </span>
        ))}
      </div>
    </div>
  );
}

const WAVE = [30, 55, 40, 80, 65, 95, 50, 70, 35, 60, 85, 45, 75, 55, 90, 40, 65, 30, 70, 50];

function MediaVisual() {
  const clips = [
    { w: "w-[38%]", c: "bg-primary" },
    { w: "w-[24%]", c: "bg-accent" },
    { w: "w-[30%]", c: "bg-primary/60" },
  ];
  return (
    <div dir="ltr" className="flex h-full flex-col justify-center gap-3">
      <div className="relative flex h-9 gap-1.5 overflow-hidden rounded-lg">
        {clips.map((c, i) => (
          <div key={i} className={`${c.w} ${c.c} rounded-md`} />
        ))}
        <div className="playhead absolute inset-y-0 start-0 w-1/4">
          <div className="absolute inset-y-0 end-0 w-0.5 bg-fg" />
        </div>
      </div>
      <div className="flex h-12 items-center gap-[3px]">
        {WAVE.map((h, i) => (
          <div key={i} style={{ height: `${h}%` }} className="w-full rounded-full bg-border" />
        ))}
      </div>
    </div>
  );
}

function SpeechVisual({ caption }: { caption: string }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-4">
      <div dir="ltr" className="flex h-14 items-center gap-1" aria-hidden="true">
        {Array.from({ length: 22 }, (_, i) => (
          <div
            key={i}
            style={{ height: `${28 + ((i * 37) % 70)}%`, "--d": `${(i % 11) * 0.09}s` } as React.CSSProperties}
            className="wave-bar w-1 rounded-full bg-gradient-to-t from-primary to-accent"
          />
        ))}
      </div>
      <span className="rounded-full bg-surface-2 px-4 py-1.5 text-sm font-medium">{caption}</span>
    </div>
  );
}

function LocalVisual() {
  return (
    <div className="flex h-full items-center justify-center">
      <svg viewBox="0 0 120 120" className="size-28" fill="none">
        {[0, 0.9, 1.8].map((d) => (
          <circle key={d} cx="60" cy="60" r="44" stroke="var(--primary)" strokeWidth="1.5" className="pulse-ring" style={{ "--d": `${d}s` } as React.CSSProperties} />
        ))}
        <path d="M60 22 90 34v24c0 20-13 32-30 40-17-8-30-20-30-40V34l30-12Z" fill="var(--primary-soft)" stroke="var(--primary)" strokeWidth="3" strokeLinejoin="round" />
        <path d="m46 60 10 10 18-20" stroke="var(--accent)" strokeWidth="5" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </div>
  );
}

function LangVisual() {
  return (
    <div className="flex h-full items-center justify-center gap-4">
      <span className="flex size-20 items-center justify-center rounded-2xl bg-primary text-5xl font-bold text-on-primary" lang="ar">
        ع
      </span>
      <svg aria-hidden="true" width="26" height="26" viewBox="0 0 26 26" fill="none" stroke="var(--muted)" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M4 9h18l-5-5M22 17H4l5 5" />
      </svg>
      <span className="flex size-20 items-center justify-center rounded-2xl border border-border bg-surface text-5xl font-bold">A</span>
    </div>
  );
}

function Tile({ className = "", visual, title, text }: { className?: string; visual: React.ReactNode; title: string; text: string }) {
  return (
    <article className={`reveal group glass relative overflow-hidden rounded-4xl p-6 transition-transform duration-300 hover:-translate-y-1 hover:border-primary/50 sm:p-8 ${className}`}>
      <div aria-hidden="true" className="h-40">
        {visual}
      </div>
      <h3 className="mt-6 text-2xl font-bold tracking-tight">{title}</h3>
      <p className="mt-1 text-lg text-muted">{text}</p>
    </article>
  );
}

export function Features() {
  const t = useTranslations("features");
  return (
    <section id="features" className="mx-auto max-w-6xl px-4 py-16 sm:px-6 lg:py-24">
      <h2 className="sr-only">{t("title")}</h2>
      <div className="grid gap-4 md:grid-cols-6">
        <Tile className="md:col-span-3" visual={<PdfVisual />} title={t("pdf.title")} text={t("pdf.text")} />
        <Tile className="md:col-span-3" visual={<MediaVisual />} title={t("media.title")} text={t("media.text")} />
        <Tile className="md:col-span-2" visual={<SpeechVisual caption={t("speech.caption")} />} title={t("speech.title")} text={t("speech.text")} />
        <Tile className="md:col-span-2" visual={<LocalVisual />} title={t("local.title")} text={t("local.text")} />
        <Tile className="md:col-span-2" visual={<LangVisual />} title={t("lang.title")} text={t("lang.text")} />
      </div>
    </section>
  );
}
