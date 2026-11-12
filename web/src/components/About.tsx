import { useTranslations } from "next-intl";
import { WHATSAPP_NUMBER } from "@/lib/site";
import { WhatsAppButton } from "./WhatsAppButton";

// "About": who builds DropFlow, and "Contact us" (#contact, linked from the footer): how to reach the team. Plain links,
// nothing is fetched. The contact block is its own named region with a heading, so people, search engines and site audits
// (Kashef looks for a contact section) find it.
export function About() {
  const t = useTranslations("about");
  const tc = useTranslations("contact");
  return (
    <section id="about" aria-labelledby="about-title" className="mx-auto max-w-6xl px-4 pb-16 sm:px-6 lg:pb-24">
      <div className="reveal glass rounded-4xl p-6 sm:p-10">
        <div className="grid gap-8 lg:grid-cols-[1fr_auto] lg:items-center">
          <div>
            <h2 id="about-title" className="text-3xl font-bold leading-tight tracking-tight sm:text-4xl">
              {t("title")}
            </h2>
            <p className="mt-4 max-w-3xl text-lg leading-relaxed text-muted">{t("text")}</p>
            <p className="mt-4 text-base font-medium">{t("team")}</p>
          </div>
          <div id="contact" role="region" aria-labelledby="contact-title" className="grid justify-items-start gap-2 lg:justify-items-center">
            <h3 id="contact-title" className="text-lg font-bold">
              {tc("title")}
            </h3>
            <WhatsAppButton testId="about-whatsapp" />
            <address dir="ltr" className="text-sm font-medium not-italic text-muted">
              {WHATSAPP_NUMBER}
            </address>
          </div>
        </div>
      </div>
    </section>
  );
}
