import { useTranslations } from "next-intl";
import { stats } from "@/lib/catalog";

export type FaqItem = { q: string; a: string };

/** The FAQ answers with their numbers filled in (shared by the section and its FAQPage JSON-LD). */
export function faqItems(raw: FaqItem[]): FaqItem[] {
  return raw.map(({ q, a }) => ({ q, a: a.replace("{conversions}", String(stats.conversions)) }));
}

// Six short questions as native <details> (no JavaScript); rendered on the server only.
export function Faq() {
  const t = useTranslations("faq");
  const items = faqItems(t.raw("items") as FaqItem[]);
  return (
    <section id="faq" aria-labelledby="faq-title" className="mx-auto max-w-3xl px-4 pb-16 sm:px-6 lg:pb-24">
      <h2 id="faq-title" className="reveal text-4xl font-bold tracking-tight sm:text-5xl">
        {t("title")}
      </h2>
      <div className="mt-8 grid gap-3">
        {items.map((item, i) => (
          <details key={i} className="glass group rounded-3xl px-5 py-1 open:pb-4" data-testid="faq-item">
            <summary className="flex min-h-13 cursor-pointer list-none items-center justify-between gap-4 [&::-webkit-details-marker]:hidden">
              <h3 className="text-lg font-semibold">{item.q}</h3>
              <svg aria-hidden="true" className="size-5 shrink-0 text-muted transition-transform duration-200 group-open:rotate-45" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round">
                <path d="M12 5v14M5 12h14" />
              </svg>
            </summary>
            <p className="text-muted">{item.a}</p>
          </details>
        ))}
      </div>
    </section>
  );
}
