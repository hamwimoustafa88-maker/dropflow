"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslations } from "next-intl";

type SampleId = "image" | "video" | "audio" | "pdf";
type WheelId = "convert" | "tools" | "actions";

const SAMPLES: Record<SampleId, { file: string; convert: string[]; tools: string[] }> = {
  image: { file: "photo.heic", convert: ["PNG", "JPG", "WEBP", "AVIF", "PDF", "TIFF"], tools: ["compress", "crop", "metadata", "removeGps", "resize", "removeBg"] },
  video: { file: "clip.mov", convert: ["MP4", "WEBM", "GIF", "MP3", "MKV", "AVI"], tools: ["compress", "trim", "crop", "speed", "mute", "transcribe"] },
  audio: { file: "voice.wav", convert: ["MP3", "M4A", "FLAC", "OGG", "OPUS", "AIFF"], tools: ["compress", "trim", "normalize", "channels", "bleep", "transcribe"] },
  pdf: { file: "report.pdf", convert: ["DOCX", "PPTX", "XLSX", "JPG", "EPUB", "TXT"], tools: ["compress", "merge", "split", "organize", "protect", "unlock"] },
};
const ACTIONS = ["share", "float", "copy", "move", "copyPath", "terminal"];
const WHEELS: { id: WheelId; key: "shift" | "ctrl" | "alt" }[] = [
  { id: "convert", key: "shift" },
  { id: "tools", key: "ctrl" },
  { id: "actions", key: "alt" },
];

const CX = 200;
const CY = 200;
const R_OUT = 178;
const R_IN = 72;
const N = 6;

function polar(r: number, deg: number) {
  const a = (deg * Math.PI) / 180;
  // Rounded so server and client render byte-identical paths (avoids hydration mismatches).
  const round = (n: number) => Math.round(n * 100) / 100;
  return [round(CX + r * Math.cos(a)), round(CY + r * Math.sin(a))] as const;
}

function wedgePath(i: number) {
  const a0 = i * 60 - 120;
  const a1 = a0 + 60;
  const [x0, y0] = polar(R_OUT, a0);
  const [x1, y1] = polar(R_OUT, a1);
  const [x2, y2] = polar(R_IN, a1);
  const [x3, y3] = polar(R_IN, a0);
  return `M ${x0} ${y0} A ${R_OUT} ${R_OUT} 0 0 1 ${x1} ${y1} L ${x2} ${y2} A ${R_IN} ${R_IN} 0 0 0 ${x3} ${y3} Z`;
}

function labelPos(i: number) {
  return polar((R_OUT + R_IN) / 2, i * 60 - 90);
}

export function WheelDemo() {
  const t = useTranslations("demo");
  const [sample, setSample] = useState<SampleId>("image");
  const [wheel, setWheel] = useState<WheelId>("convert");
  const [phase, setPhase] = useState<"idle" | "running" | "done">("idle");
  const [result, setResult] = useState("");
  const [activeIndex, setActiveIndex] = useState<number | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const wedgeRefs = useRef<(SVGGElement | null)[]>([]);

  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);

  const current = SAMPLES[sample];
  const labels = useMemo(() => {
    if (wheel === "convert") return current.convert;
    if (wheel === "tools") return current.tools.map((k) => t(`tool.${k}`));
    return ACTIONS.map((k) => t(`action.${k}`));
  }, [wheel, current, t]);

  const base = current.file.replace(/\.[^.]+$/, "");

  function reset() {
    if (timer.current) clearTimeout(timer.current);
    setPhase("idle");
    setResult("");
    setActiveIndex(null);
  }

  function run(i: number) {
    if (timer.current) clearTimeout(timer.current);
    setActiveIndex(i);
    setPhase("running");
    const label = labels[i];
    timer.current = setTimeout(() => {
      setResult(wheel === "convert" ? `${base}.${label.toLowerCase()}` : `${label} · ${current.file}`);
      setPhase("done");
    }, 900);
  }

  function onWheelKey(e: React.KeyboardEvent, i: number) {
    const move = (d: number) => {
      e.preventDefault();
      wedgeRefs.current[(i + d + N) % N]?.focus();
    };
    if (e.key === "ArrowRight" || e.key === "ArrowDown") move(1);
    else if (e.key === "ArrowLeft" || e.key === "ArrowUp") move(-1);
    else if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      run(i);
    }
  }

  const status = phase === "idle" ? t("idle") : phase === "running" ? t("running") : t("done", { result });

  return (
    <section aria-label={t("label")} className="relative">
      <div aria-hidden="true" className="absolute inset-0 -z-10 m-auto size-3/4 rounded-full bg-primary/25 blur-3xl" />
      <div className="glass rounded-4xl p-4 shadow-2xl shadow-primary/10 sm:p-6">
        <div role="radiogroup" aria-label={t("files")} dir="ltr" className="flex flex-wrap justify-center gap-1.5">
          {(Object.keys(SAMPLES) as SampleId[]).map((id) => (
            <button
              key={id}
              type="button"
              role="radio"
              aria-checked={sample === id}
              onClick={() => { setSample(id); reset(); }}
              className={`min-h-11 rounded-full border px-4 font-mono text-sm transition-colors duration-200 ${
                sample === id ? "border-primary bg-primary-soft text-fg" : "border-transparent text-muted hover:text-fg"
              }`}
            >
              {SAMPLES[id].file}
            </button>
          ))}
        </div>

        <div className="mx-auto mt-3 w-full max-w-md">
          <svg viewBox="0 0 400 400" className="wheel-pop block w-full" key={`${sample}-${wheel}`} role="group" aria-label={t(`wheels.${wheel}`)}>
            <circle cx={CX} cy={CY} r="196" fill="none" stroke="var(--border)" strokeWidth="1.5" strokeDasharray="3 9" strokeLinecap="round" className="orbit" />
            {labels.map((label, i) => {
              const [lx, ly] = labelPos(i);
              return (
                <g
                  key={i}
                  ref={(el) => { wedgeRefs.current[i] = el; }}
                  role="button"
                  className="wedge-group"
                  tabIndex={0}
                  aria-label={label}
                  onClick={() => run(i)}
                  onKeyDown={(e) => onWheelKey(e, i)}
                >
                  <path
                    d={wedgePath(i)}
                    className="wedge"
                    data-active={activeIndex === i ? "true" : undefined}
                    data-done={activeIndex === i && phase === "done" ? "true" : undefined}
                  />
                  <text x={lx} y={ly} textAnchor="middle" dominantBaseline="central" className="wedge-label">
                    {label}
                  </text>
                </g>
              );
            })}
            <circle cx={CX} cy={CY} r={R_IN - 8} fill="var(--bg)" stroke="var(--border)" />
            <g transform={`translate(${CX - 14} ${CY - 28})`} fill="none" stroke="var(--primary)" strokeWidth="2.4" strokeLinejoin="round">
              <path d="M5 2h12l8 8v20a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2Z" />
              <path d="M17 2v8h8" />
            </g>
            <text x={CX} y={CY + 20} textAnchor="middle" className="fill-fg" style={{ fontSize: 11, fontWeight: 600 }}>
              {current.file}
            </text>
            {phase === "running" && (
              <circle cx={CX} cy={CY} r={R_IN - 3} fill="none" stroke="var(--accent)" strokeWidth="5" strokeLinecap="round" strokeDasharray="283 283" className="progress-ring" transform={`rotate(-90 ${CX} ${CY})`} pathLength={283} />
            )}
          </svg>
        </div>

        <p
          role="status"
          aria-live="polite"
          dir="ltr"
          data-testid="demo-status"
          data-done={phase === "done" ? "true" : undefined}
          className={`mt-1 min-h-7 text-center text-sm ${phase === "done" ? "font-semibold text-(--ok)" : "font-medium text-muted"}`}
        >
          {status}
        </p>

        <div role="group" aria-label={t("label")} className="mt-3 grid grid-cols-3 gap-1.5 rounded-2xl bg-surface-2 p-1.5">
          {WHEELS.map((w) => (
            <button
              key={w.id}
              type="button"
              aria-pressed={wheel === w.id}
              onClick={() => { setWheel(w.id); reset(); }}
              className={`flex min-h-14 flex-col items-center justify-center rounded-xl px-2 py-1.5 transition-colors duration-200 ${
                wheel === w.id ? "bg-primary text-on-primary shadow-sm" : "text-muted hover:text-fg"
              }`}
            >
              <kbd className="font-mono text-xs">{t(`keys.${w.key}`)}</kbd>
              <span className="text-sm font-semibold">{t(`wheels.${w.id}`)}</span>
            </button>
          ))}
        </div>
      </div>
    </section>
  );
}
