import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { ImageResponse } from "next/og";
import { getTranslations } from "next-intl/server";

export const alt = "DropFlow — Drag. Tap. Drop. · اسحب. اضغط. أفلت.";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

export function generateStaticParams() {
  return [{ locale: "ar" }, { locale: "en" }];
}

// Rendered at build time. The Arabic image uses the site's own font (IBM Plex Sans Arabic Bold, fetched as TTF by
// scripts/og-font.mjs because next/og cannot read WOFF2); without it, both locales fall back to the Latin image.
const FONT = join(process.cwd(), ".og-cache", "IBMPlexSansArabic-Bold.ttf");

// next/og lays words out left to right; for Arabic, hand it the VISUAL order (words reversed, a final "." moved to the
// left end) so the image reads correctly right to left. Letters inside each word are shaped by the font.
function visual(text: string, rtl: boolean): string {
  if (!rtl) return text;
  return text
    .split(" ")
    .reverse()
    .map((w) => (/[.!?]$/.test(w) ? w.slice(-1) + w.slice(0, -1) : w))
    .join(" ");
}

export default async function Image({ params }: { params: Promise<{ locale: string }> }) {
  const { locale } = await params;
  const font = existsSync(FONT) ? readFileSync(FONT) : null;
  const lang = locale === "ar" && !font ? "en" : locale;
  const hero = await getTranslations({ locale: lang, namespace: "hero" });
  const meta = await getTranslations({ locale: lang, namespace: "meta" });
  const rtl = lang === "ar";
  const [a, b, c] = [hero("titleA"), hero("titleB"), hero("titleC")].map((w) => visual(w, rtl));
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          flexDirection: "column",
          justifyContent: "center",
          alignItems: lang === "ar" ? "flex-end" : "flex-start",
          padding: 90,
          color: "#eef1ff",
          fontFamily: font ? "Plex Arabic" : undefined,
          background: "linear-gradient(135deg, #070b1c 0%, #131b44 60%, #0e4a5c 140%)",
        }}
      >
        <div style={{ display: "flex", fontSize: 40, fontWeight: 700, color: "#8b95fb" }}>DropFlow</div>
        <div style={{ display: "flex", flexDirection: "column", alignItems: lang === "ar" ? "flex-end" : "flex-start", marginTop: 36, fontSize: 130, fontWeight: 700, lineHeight: 1.12 }}>
          <span>{a}</span>
          <span style={{ color: "#22d3ee" }}>{b}</span>
          <span>{c}</span>
        </div>
        <div style={{ display: "flex", marginTop: 18, fontSize: 34, color: "#a3acd8" }}>{visual(meta("ogTagline"), rtl)}</div>
      </div>
    ),
    { ...size, ...(font ? { fonts: [{ name: "Plex Arabic", data: font, weight: 700 as const, style: "normal" as const }] } : {}) },
  );
}
