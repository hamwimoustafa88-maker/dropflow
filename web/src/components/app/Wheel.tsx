"use client";

import { useEffect, useRef, useState } from "react";
import { CX, CY, R_IN, R_OUT, fitLabel, labelPos, nextIndex, polar, wedgeMid, wedgePath } from "@/engine/wheel-geometry";

export interface WheelItem {
  id: string;
  label: string;
  /** Rendered in the mono font (format names like PNG). */
  mono?: boolean;
  /** Dots instead of text for the "more" wedge. */
  more?: boolean;
  /** The "+ Add" wedge (opens the list of everything else). */
  add?: boolean;
  /** Added by the person with "+ Add": marked with a small dot near the rim. */
  pinned?: boolean;
}

interface Props {
  items: readonly WheelItem[];
  /** Accessible name of the whole wheel, e.g. "Convert". */
  label: string;
  /** Changes whenever the set of wedges changes (kind or page): replays the pop animation and resets keyboard focus. */
  wheelKey: string;
  /** Wedges whose operation is currently running. */
  busyIds?: ReadonlySet<string>;
  onPick: (index: number) => void;
  title: string;
  subtitle?: string;
  /** 0..1 shows a progress ring in the centre. */
  progress?: number | null;
  /** True while an engine is loading: the centre shows a spinner. */
  loading?: boolean;
  /** Increment to move keyboard focus onto the wheel (after a drop). */
  focusToken: number;
}

function clip(text: string, max: number): string {
  const chars = [...text];
  return chars.length <= max ? text : chars.slice(0, max - 1).join("") + "…";
}

export function Wheel({ items, label, wheelKey, busyIds, onPick, title, subtitle, progress, loading, focusToken }: Props) {
  const n = items.length;
  const [focus, setFocus] = useState<{ key: string; index: number }>({ key: wheelKey, index: 0 });
  const index = focus.key === wheelKey ? Math.min(focus.index, Math.max(0, n - 1)) : 0;
  const refs = useRef<(SVGGElement | null)[]>([]);

  useEffect(() => {
    if (focusToken > 0) refs.current[index]?.focus();
    // Only a new token should steal focus, not every index change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusToken]);

  function move(to: number) {
    setFocus({ key: wheelKey, index: to });
    refs.current[to]?.focus();
  }

  function onKey(e: React.KeyboardEvent, i: number) {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      onPick(i);
      return;
    }
    const to = nextIndex(n, i, e.key);
    if (to !== null) {
      e.preventDefault();
      move(to);
    }
  }

  return (
    <div className="mx-auto w-full max-w-md" dir="ltr">
      <svg viewBox="0 0 400 400" className="wheel-pop block w-full" key={wheelKey} role="group" aria-label={label}>
        <circle cx={CX} cy={CY} r="196" fill="none" stroke="var(--border)" strokeWidth="1.5" strokeDasharray="3 9" strokeLinecap="round" className="orbit" />
        {items.map((item, i) => {
          const [lx, ly] = labelPos(n, i);
          const [px, py] = polar(R_OUT - 14, n <= 1 ? -90 : wedgeMid(n, i));
          const text = item.more ? "•••" : fitLabel(item.label, n);
          return (
            <g
              key={item.id}
              ref={(el) => {
                refs.current[i] = el;
              }}
              role="button"
              tabIndex={i === index ? 0 : -1}
              aria-label={item.label}
              data-wedge={item.id}
              data-pinned={item.pinned ? "true" : undefined}
              className="wedge-group"
              onClick={() => {
                setFocus({ key: wheelKey, index: i });
                onPick(i);
              }}
              onFocus={() => setFocus({ key: wheelKey, index: i })}
              onKeyDown={(e) => onKey(e, i)}
            >
              <path d={wedgePath(n, i)} className={item.add ? "wedge wedge-add" : "wedge"} data-busy={busyIds?.has(item.id) ? "true" : undefined} />
              <text
                x={lx}
                y={ly}
                textAnchor="middle"
                dominantBaseline="central"
                className={`wedge-label ${item.more ? "wheel-more-dots" : ""}`}
                style={item.mono ? { fontFamily: "var(--font-plex-mono), ui-monospace, monospace", fontSize: 15 } : undefined}
              >
                {text}
              </text>
              {item.pinned ? <circle cx={px} cy={py} r="3.5" className="wedge-pin" aria-hidden="true" /> : null}
            </g>
          );
        })}
        <circle cx={CX} cy={CY} r={R_IN - 8} fill="var(--bg)" stroke="var(--border)" />
        {loading ? (
          <g className="spin" style={{ transformOrigin: `${CX}px ${CY - 14}px` }} aria-hidden="true">
            <circle cx={CX} cy={CY - 14} r="14" fill="none" stroke="var(--border)" strokeWidth="4" />
            <path d={`M ${CX} ${CY - 28} A 14 14 0 0 1 ${CX + 14} ${CY - 14}`} fill="none" stroke="var(--primary)" strokeWidth="4" strokeLinecap="round" />
          </g>
        ) : (
          <g transform={`translate(${CX - 14} ${CY - 44})`} fill="none" stroke="var(--primary)" strokeWidth="2.4" strokeLinejoin="round" aria-hidden="true">
            <path d="M5 2h12l8 8v20a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2Z" />
            <path d="M17 2v8h8" />
          </g>
        )}
        <text x={CX} y={CY + 14} textAnchor="middle" className="wheel-center-title">
          {clip(title, 14)}
        </text>
        {subtitle ? (
          <text x={CX} y={CY + 32} textAnchor="middle" className="wheel-center-sub">
            {clip(subtitle, 18)}
          </text>
        ) : null}
        {progress != null && progress >= 0 ? (
          <circle cx={CX} cy={CY} r={R_IN - 3} fill="none" stroke="var(--accent)" strokeWidth="5" strokeLinecap="round" pathLength={100} strokeDasharray={`${Math.max(1, Math.round(progress * 100))} 100`} transform={`rotate(-90 ${CX} ${CY})`} />
        ) : null}
      </svg>
    </div>
  );
}
