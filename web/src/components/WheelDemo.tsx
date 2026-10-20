"use client";

import { useEffect, useId, useMemo, useRef, useState } from "react";
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
const R_OUT = 184;
const R_IN = 74;
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
  const uid = useId();
  const [sample, setSample] = useState<SampleId>("image");
  const [wheel, setWheel] = useState<WheelId>("convert");
  const [phase, setPhase] = useState<"idle" | "running" | "done">("idle");
  const [message, setMessage] = useState("");
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
    setMessage("");
    setActiveIndex(null);
  }

  function run(i: number) {
    if (timer.current) clearTimeout(timer.current);
    setActiveIndex(i);
    setPhase("running");
    setMessage(t("running", { file: current.file }));
    const label = labels[i];
    timer.current = setTimeout(() => {
      setPhase("done");
      if (wheel === "convert") setMessage(t("doneConvert", { file: `${base}.${label.toLowerCase()}` }));
      else if (wheel === "tools") setMessage(t("doneTool", { tool: label, file: current.file }));
      else setMessage(t("doneAction", { action: label, file: current.file }));
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

  const status = phase === "idle" ? t("idle", { file: current.file }) : message;

  return (
    <section aria-label={t("label")} className="rounded-3xl border border-border bg-surface p-5 shadow-sm sm:p-7">
      <div role="radiogroup" aria-label={t("fileLabel")} className="flex flex-wrap gap-2">
        {(Object.keys(SAMPLES) as SampleId[]).map((id) => (
          <button
            key={id}
            type="button"
            role="radio"
            aria-checked={sample === id}
            onClick={() => { setSample(id); reset(); }}
            dir="ltr"
            className={`min-h-11 rounded-xl border px-3.5 font-mono text-sm transition-colors duration-200 ${
              sample === id ? "border-primary bg-primary-soft text-fg" : "border-border text-muted hover:text-fg"
            }`}
          >
            {SAMPLES[id].file}
          </button>
        ))}
      </div>

      <div className="relative mx-auto mt-6 w-full max-w-95">
        <svg viewBox="0 0 400 400" className="wheel-pop block w-full" key={`${sample}-${wheel}`} role="group" aria-label={`${t("wheelLabel")}: ${t(`wheels.${wheel}`)}`}>
          {labels.map((label, i) => {
            const [lx, ly] = labelPos(i);
            return (
              <g
                key={i}
                ref={(el) => { wedgeRefs.current[i] = el; }}
                role="button"
                tabIndex={0}
                aria-label={label}
                onClick={() => run(i)}
                onKeyDown={(e) => onWheelKey(e, i)}
              >
                <path d={wedgePath(i)} className="wedge" data-active={activeIndex === i ? "true" : undefined} />
                <text x={lx} y={ly} textAnchor="middle" dominantBaseline="central" className="wedge-label">
                  {label}
                </text>
              </g>
            );
          })}
          <circle cx={CX} cy={CY} r={R_IN - 10} fill="var(--bg)" stroke="var(--border)" />
          <g transform={`translate(${CX - 14} ${CY - 26})`} fill="none" stroke="var(--primary)" strokeWidth="2.4" strokeLinejoin="round">
            <path d="M5 2h12l8 8v20a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2Z" />
            <path d="M17 2v8h8" />
          </g>
          <text x={CX} y={CY + 18} textAnchor="middle" className="fill-fg" style={{ fontSize: 11, fontWeight: 600 }}>
            {current.file}
          </text>
          {phase === "running" && (
            <circle cx={CX} cy={CY} r={R_IN - 4} fill="none" stroke="var(--accent)" strokeWidth="5" strokeLinecap="round" strokeDasharray="283 283" className="progress-ring" transform={`rotate(-90 ${CX} ${CY})`} pathLength={283} />
          )}
        </svg>
      </div>

      <div role="group" aria-label={t("wheelLabel")} className="mt-5 grid grid-cols-3 gap-2">
        {WHEELS.map((w) => (
          <button
            key={w.id}
            type="button"
            aria-pressed={wheel === w.id}
            id={`${uid}-${w.id}`}
            onClick={() => { setWheel(w.id); reset(); }}
            className={`flex min-h-14 flex-col items-center justify-center rounded-xl border px-2 py-1.5 transition-colors duration-200 ${
              wheel === w.id ? "border-primary bg-primary text-on-primary" : "border-border bg-surface text-fg hover:bg-surface-2"
            }`}
          >
            <kbd className="font-mono text-xs opacity-80">{t(`keys.${w.key}`)}</kbd>
            <span className="text-sm font-semibold">{t(`wheels.${w.id}`)}</span>
          </button>
        ))}
      </div>

      <p role="status" aria-live="polite" className="mt-4 min-h-12 rounded-xl bg-surface-2 px-4 py-3 text-sm text-fg">
        {status}
      </p>
      <p className="mt-2 text-xs text-muted">{t("hint")} {t("simulated")}</p>
    </section>
  );
}
