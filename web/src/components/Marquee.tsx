import { categories } from "@/lib/catalog";

// Wordless social proof: every format DropFlow can write, drifting past in two opposite rows.
const FORMATS = [...new Set(categories.flatMap((c) => c.outputs))].map((f) => (f === "pdfa" ? "pdf/a" : f));
const ROW_A = FORMATS.filter((_, i) => i % 2 === 0);
const ROW_B = FORMATS.filter((_, i) => i % 2 === 1);

function Row({ items, reverse = false }: { items: string[]; reverse?: boolean }) {
  // The track is rendered twice so the loop is seamless at translateX(-100%).
  return (
    <div className={`marquee ${reverse ? "marquee-reverse" : ""}`}>
      {[0, 1].map((n) => (
        <div key={n} className="marquee-track">
          {items.map((f) => (
            <span key={`${n}-${f}`} className="rounded-xl border border-border bg-surface px-4 py-2 font-mono text-sm uppercase text-muted">
              {f}
            </span>
          ))}
        </div>
      ))}
    </div>
  );
}

export function Marquee() {
  return (
    <div dir="ltr" aria-hidden="true" className="mb-10 space-y-3 py-6">
      <Row items={ROW_A} />
      <Row items={ROW_B} reverse />
    </div>
  );
}
