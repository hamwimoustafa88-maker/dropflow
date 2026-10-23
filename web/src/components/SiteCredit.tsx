import { useTranslations } from "next-intl";
import { SCOUTHUB_URL } from "@/lib/site";

// The closing line of every page: copyright, then "an initiative of the ScoutHub platform" with the platform's logo
// (a local copy of https://scouthub.dev/assets/img/favicon.svg, so the page never loads anything from another site).
export function SiteCredit({ className = "" }: { className?: string }) {
  const t = useTranslations("credit");
  return (
    <div
      className={`flex flex-col items-center justify-center gap-x-4 gap-y-1.5 text-center text-xs text-muted sm:flex-row ${className}`}
      data-testid="site-credit"
    >
      <p data-testid="credit-rights">{t("rights")}</p>
      <p className="inline-flex items-center gap-2" data-testid="credit-initiative">
        {/* eslint-disable-next-line @next/next/no-img-element -- a 1 KB static SVG */}
        <img src="/icons/scouthub.svg" alt="" width={20} height={20} className="size-5 shrink-0" />
        <span>
          {t.rich("initiative", {
            link: (chunks) => (
              <a
                href={SCOUTHUB_URL}
                target="_blank"
                rel="noopener noreferrer"
                data-testid="credit-scouthub"
                className="font-semibold text-fg underline-offset-4 hover:underline"
              >
                {chunks}
              </a>
            ),
          })}
        </span>
      </p>
    </div>
  );
}
