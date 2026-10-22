"use client";

import { useEffect, useRef, useSyncExternalStore, type SyntheticEvent } from "react";
import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import {
  assetDetail,
  assetsOf,
  detectTarget,
  downloadUrl,
  highlightFor,
  isReleased,
  type DownloadAsset,
  type Platform,
  type Target,
} from "@/lib/downloads";

// The download row: Windows (split button: x64 installer + a list), macOS and Linux (a list of files), Android (one file)
// and the web app. Every file link is a direct GitHub release download (src/lib/downloads.ts); nothing is fetched by the page.
// Lists are native <details> disclosures, so they open and close without JavaScript; with it they also close on Esc
// (focus returns to the button) and on a click outside, and keep inside the viewport. The visitor's own system is
// highlighted after hydration by swapping colours only (every variant has the same 1 px border: no layout shift).

const BOX = "min-h-13 items-center justify-center rounded-2xl border text-base font-semibold transition-colors duration-200";
const BASE = `inline-flex ${BOX}`;
const HOT = "border-transparent bg-primary text-on-primary shadow-lg shadow-primary/25";
const COLD = "glass text-fg hover:border-primary";
const MENU_NAME = "dropflow-download"; // one list open at a time (exclusive <details>, no JS needed)

const noSubscribe = () => () => {};
function readTarget(): Target | null {
  const nav = navigator as Navigator & { userAgentData?: { platform?: string } };
  return detectTarget(nav.userAgent, nav.userAgentData?.platform);
}

function PlatformIcon({ target }: { target: Target }) {
  const common = { "aria-hidden": true, width: 22, height: 22, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round", strokeLinejoin: "round" } as const;
  switch (target) {
    case "windows":
      return (
        <svg {...common}>
          <rect x="3.5" y="3.5" width="7.5" height="7.5" rx="1.2" />
          <rect x="13" y="3.5" width="7.5" height="7.5" rx="1.2" />
          <rect x="3.5" y="13" width="7.5" height="7.5" rx="1.2" />
          <rect x="13" y="13" width="7.5" height="7.5" rx="1.2" />
        </svg>
      );
    case "macos":
      return (
        <svg {...common}>
          <rect x="4" y="4.5" width="16" height="11" rx="2" />
          <path d="M2 19.5h20" />
        </svg>
      );
    case "linux":
      return (
        <svg {...common}>
          <rect x="3" y="4" width="18" height="16" rx="2.5" />
          <path d="M7 9.5l3 2.5-3 2.5M12.5 15H17" />
        </svg>
      );
    case "android":
      return (
        <svg {...common}>
          <rect x="6.5" y="2.5" width="11" height="19" rx="2.5" />
          <path d="M11 18.5h2" />
        </svg>
      );
    default:
      return (
        <svg {...common}>
          <circle cx="12" cy="12" r="9" />
          <path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18" />
        </svg>
      );
  }
}

const DownloadIcon = () => (
  <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
    <path d="M12 4v12M6 12l6 6 6-6M5 21h14" />
  </svg>
);

const Chevron = () => (
  <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" className="transition-transform duration-200 group-open/menu:rotate-180 motion-reduce:transition-none">
    <path d="M6 9l6 6 6-6" />
  </svg>
);

function SoonBadge() {
  const t = useTranslations("download");
  return <span className="rounded-full bg-surface-2 px-2 py-0.5 text-xs font-semibold text-muted">{t("soon")}</span>;
}

/** Keeps an opened list inside the viewport (from the sm breakpoint; on phones it spans the whole button row). */
function place(e: SyntheticEvent<HTMLDetailsElement>) {
  const d = e.currentTarget;
  const panel = d.querySelector<HTMLElement>("[data-panel]");
  if (!d.open || !panel) return;
  delete panel.dataset.align;
  delete panel.dataset.side;
  if (!window.matchMedia("(min-width: 40rem)").matches) return;
  const r = panel.getBoundingClientRect();
  if (r.right > window.innerWidth - 8 || r.left < 8) panel.dataset.align = "end";
  const anchor = panel.offsetParent?.getBoundingClientRect();
  // Opens upwards only when it does not fit below AND fits above, under the sticky header.
  if (anchor && r.bottom > window.innerHeight - 8 && anchor.top - r.height - 8 > 72) panel.dataset.side = "top";
}

function Menu({ platform, assets }: { platform: Platform; assets: DownloadAsset[] }) {
  const t = useTranslations("download");
  const name = t(`platforms.${platform}`);
  return (
    <div
      data-panel
      className="absolute inset-x-0 z-20 mt-2 rounded-2xl border border-border bg-surface p-1.5 text-start text-fg shadow-xl shadow-fg/10 sm:inset-x-auto sm:start-0 sm:top-full sm:w-80 sm:data-[align=end]:start-auto sm:data-[align=end]:end-0 sm:data-[side=top]:top-auto sm:data-[side=top]:bottom-full sm:data-[side=top]:mt-0 sm:data-[side=top]:mb-2"
    >
      <ul aria-label={t("menu", { platform: name })} className="grid gap-0.5">
        {assets.map((a) => (
          <li key={a.file}>
            {a.available ? (
              <a
                href={downloadUrl(a.file)}
                download
                rel="noopener"
                data-testid={`dl-file-${a.file}`}
                // A download keeps the page: close the list after the click.
                onClick={(e) => {
                  const d = e.currentTarget.closest("details");
                  if (d) d.open = false;
                }}
                className="flex min-h-12 items-center justify-between gap-3 rounded-xl px-3 text-sm font-semibold transition-colors duration-150 hover:bg-surface-2"
              >
                <span>{t(`assets.${a.label}`)}</span>{" "}
                <span dir="ltr" className="font-mono text-xs font-medium text-muted">
                  {assetDetail(a)}
                </span>
              </a>
            ) : (
              <span aria-disabled="true" data-testid={`dl-file-${a.file}`} className="flex min-h-12 items-center justify-between gap-3 rounded-xl px-3 text-sm font-semibold text-muted">
                <span>
                  {t(`assets.${a.label}`)} <span dir="ltr" className="font-mono text-xs font-medium">{assetDetail(a)}</span>
                </span>
                <SoonBadge />
              </span>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

const summaryReset = "list-none [&::-webkit-details-marker]:hidden";

function PlatformButton({ platform, hot, yours }: { platform: Platform; hot: boolean; yours: boolean }) {
  const t = useTranslations("download");
  const name = t(`platforms.${platform}`);
  const assets = assetsOf(platform);
  const tone = hot ? HOT : COLD;
  const mine = yours ? <span className="sr-only"> {t("yours")}</span> : null;

  if (!isReleased(platform)) {
    return (
      <span aria-disabled="true" data-platform={platform} data-testid={`dl-${platform}`} className={`${BASE} ${COLD} cursor-default gap-3 px-5 text-muted`}>
        <PlatformIcon target={platform} />
        <span>{name}</span>
        <SoonBadge />
      </span>
    );
  }

  // Windows: the main part downloads the x64 installer; the chevron opens the other files.
  if (platform === "windows") {
    const main = assets[0];
    return (
      <div data-platform={platform} data-hot={hot} className={`${BASE} ${tone} col-span-2 sm:relative`}>
        <a href={downloadUrl(main.file)} download rel="noopener" data-testid="dl-windows" className="inline-flex min-h-[3.125rem] flex-1 items-center justify-center gap-3 rounded-s-2xl ps-5 pe-3">
          <PlatformIcon target={platform} />
          <span>{name}</span>
          {mine}
          <DownloadIcon />
        </a>
        <details name={MENU_NAME} onToggle={place} className="group/menu">
          <summary aria-label={t("more", { platform: name })} data-testid="dl-windows-more" className={`${summaryReset} flex min-h-[3.125rem] items-center rounded-e-2xl border-s border-current/25 px-3`}>
            <Chevron />
          </summary>
          <Menu platform={platform} assets={assets} />
        </details>
      </div>
    );
  }

  // One file: a direct link.
  if (assets.length === 1) {
    return (
      <a href={downloadUrl(assets[0].file)} download rel="noopener" data-platform={platform} data-hot={hot} data-testid={`dl-${platform}`} className={`${BASE} ${tone} gap-3 px-5`}>
        <PlatformIcon target={platform} />
        <span>{name}</span>
        {mine}
        <DownloadIcon />
      </a>
    );
  }

  // Several files (macOS, Linux): the whole button opens the list.
  return (
    <details name={MENU_NAME} onToggle={place} data-platform={platform} data-hot={hot} className="group/menu sm:relative">
      <summary data-testid={`dl-${platform}`} className={`${summaryReset} flex ${BOX} ${tone} gap-3 px-5`}>
        <PlatformIcon target={platform} />
        <span>{name}</span>
        {mine}
        <Chevron />
      </summary>
      <Menu platform={platform} assets={assets} />
    </details>
  );
}

function WebButton({ hot, yours }: { hot: boolean; yours: boolean }) {
  const t = useTranslations("download");
  return (
    <Link
      href="/app"
      // No viewport prefetch: it would load the /app route chunks on the landing page. The click fetches them.
      prefetch={false}
      data-platform="web"
      data-hot={hot}
      className={`${BASE} ${hot ? HOT : COLD} gap-3 px-5`}
    >
      <PlatformIcon target="web" />
      <span>{t("platforms.web")}</span>
      {yours ? <span className="sr-only"> {t("yours")}</span> : null}
      <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" className="rtl:-scale-x-100">
        <path d="M5 12h14M13 6l6 6-6 6" />
      </svg>
    </Link>
  );
}

const ORDER: Platform[] = ["windows", "macos", "linux", "android"];

/** The whole row. `note`: the one-line "not signed yet" hint under it (shown once, in the hero). */
export function Downloads({ center = false, note = false, id }: { center?: boolean; note?: boolean; id?: string }) {
  const t = useTranslations("download");
  const ref = useRef<HTMLDivElement>(null);
  // Server and first client render: nothing detected (Windows highlighted). Then the visitor's own system.
  const detected = useSyncExternalStore(noSubscribe, readTarget, () => null);
  const hot = highlightFor(detected);

  useEffect(() => {
    const root = ref.current;
    if (!root) return;
    const open = () => [...root.querySelectorAll<HTMLDetailsElement>("details[open]")];
    const onDown = (e: PointerEvent) => {
      for (const d of open()) if (!d.contains(e.target as Node)) d.open = false;
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      for (const d of open()) {
        const focused = d.contains(document.activeElement);
        d.open = false;
        if (focused) d.querySelector("summary")?.focus();
      }
    };
    document.addEventListener("pointerdown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, []);

  return (
    <div id={id} className={center ? "text-center" : undefined}>
      <div ref={ref} role="group" aria-label={t("label")} data-testid="downloads" className={`relative grid grid-cols-2 gap-3 sm:flex sm:flex-wrap ${center ? "sm:justify-center" : ""}`}>
        {ORDER.map((p) => (
          <PlatformButton key={p} platform={p} hot={hot === p} yours={detected !== null && hot === p} />
        ))}
        <WebButton hot={hot === "web"} yours={detected !== null && hot === "web"} />
      </div>
      {note ? (
        <p data-testid="download-note" className="mt-4 text-sm text-muted">
          {t("note")}
        </p>
      ) : null}
    </div>
  );
}
