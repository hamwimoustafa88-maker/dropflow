import { useTranslations } from "next-intl";
import { SCOUTHUB_SOCIAL, SCOUTHUB_URL } from "@/lib/site";
import { SocialGlyph } from "./SocialGlyph";

// The closing line of every page: copyright, then "an initiative of the ScoutHub platform" with the platform's logo
// (a local copy of https://scouthub.dev/assets/img/favicon.svg, so the page never loads anything from another site),
// then ScoutHub's social accounts as icons (inline glyphs; each link is 44 px and named for screen readers).
export function SiteCredit({ className = "" }: { className?: string }) {
  const t = useTranslations("credit");
  return (
    <div
      className={`flex flex-col items-center justify-center gap-x-4 gap-y-1.5 text-center text-xs text-muted sm:flex-row sm:flex-wrap ${className}`}
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
      <ul aria-label={t("social.label")} className="-my-1.5 flex flex-wrap items-center justify-center" data-testid="credit-social">
        {SCOUTHUB_SOCIAL.map(({ key, url }) => (
          <li key={key}>
            <a
              href={url}
              target="_blank"
              rel="noopener noreferrer"
              aria-label={t(`social.${key}`)}
              data-testid={`credit-social-${key}`}
              className="inline-flex size-11 items-center justify-center rounded-full transition-colors duration-200 hover:bg-surface-2 hover:text-fg motion-reduce:transition-none"
            >
              <SocialGlyph name={key} />
            </a>
          </li>
        ))}
      </ul>
    </div>
  );
}
