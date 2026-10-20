import { ImageResponse } from "next/og";

export const alt = "DropFlow — Drag. Tap. Drop.";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

export function generateStaticParams() {
  return [{ locale: "ar" }, { locale: "en" }];
}

// Latin-only on purpose: the built-in OG font has no Arabic glyphs, so both locales share one share image.
export default async function Image() {
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          flexDirection: "column",
          justifyContent: "center",
          padding: 90,
          color: "#eef1ff",
          background: "linear-gradient(135deg, #070b1c 0%, #131b44 60%, #0e4a5c 140%)",
        }}
      >
        <div style={{ display: "flex", fontSize: 40, fontWeight: 700, color: "#8b95fb" }}>DropFlow</div>
        <div style={{ display: "flex", flexDirection: "column", marginTop: 24, fontSize: 150, fontWeight: 800, lineHeight: 1.02 }}>
          <span>Drag.</span>
          <span style={{ color: "#22d3ee" }}>Tap.</span>
          <span>Drop.</span>
        </div>
      </div>
    ),
    { ...size },
  );
}
