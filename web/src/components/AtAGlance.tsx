import { useTranslations } from "next-intl";
import { stats } from "@/lib/catalog";
import { LATEST_VERSION } from "@/lib/downloads";

// "At a glance": the facts people look for before downloading, as one small table. Values come from the catalog and the
// release constant, so they never drift from the app.
export function AtAGlance() {
  const t = useTranslations("glance");
  const rows: [string, string][] = [
    [t("price"), t("priceValue")],
    [t("license"), "GPL-3.0"],
    [t("version"), LATEST_VERSION],
    [t("platforms"), "Windows · macOS · Linux · Android · Web"],
    [t("offline"), t("offlineValue")],
    [t("conversions"), String(stats.conversions)],
    [t("languages"), t("languagesValue")],
    [t("updates"), t("updatesValue")],
  ];
  return (
    <section id="glance" className="mx-auto max-w-4xl px-4 pb-16 sm:px-6 lg:pb-24" data-testid="glance">
      <h2 className="reveal text-center text-4xl font-bold tracking-tight sm:text-6xl">{t("title")}</h2>
      <dl className="reveal glass mt-10 grid overflow-hidden rounded-4xl sm:grid-cols-2">
        {rows.map(([k, v]) => (
          <div key={k} className="flex items-baseline justify-between gap-4 border-b border-border/70 px-6 py-4 sm:odd:border-e">
            <dt className="text-muted">{k}</dt>
            <dd className="text-end font-semibold" dir="auto">
              {v}
            </dd>
          </div>
        ))}
      </dl>
    </section>
  );
}
