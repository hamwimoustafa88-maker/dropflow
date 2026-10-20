import { useTranslations } from "next-intl";

// Inert until the first release exists: it looks like a button but is not a link, and says "Soon".
export function SoonButton({ kind, primary = false }: { kind: "windows" | "web"; primary?: boolean }) {
  const t = useTranslations("common");
  return (
    <span
      className={`inline-flex min-h-13 cursor-default select-none items-center gap-3 rounded-2xl px-5 text-base font-semibold ${
        primary ? "bg-primary text-on-primary shadow-lg shadow-primary/25" : "glass text-fg"
      }`}
    >
      <svg aria-hidden="true" width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        {kind === "windows" ? (
          <>
            <rect x="3" y="4" width="18" height="12" rx="2" />
            <path d="M8 20h8M12 16v4" />
          </>
        ) : (
          <>
            <circle cx="12" cy="12" r="9" />
            <path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18" />
          </>
        )}
      </svg>
      <span>{t(kind)}</span>
      <span className={`rounded-full px-2.5 py-0.5 text-xs font-bold ${primary ? "bg-on-primary/15" : "bg-surface-2 text-muted"}`}>{t("soon")}</span>
    </span>
  );
}
