import { useTranslations } from "next-intl";
import { WHATSAPP_NUMBER } from "@/lib/site";
import { WhatsAppButton } from "./WhatsAppButton";

// "About": who builds DropFlow and how to reach the team. Plain links, nothing is fetched.
export function About() {
  const t = useTranslations("about");
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
          <div className="grid justify-items-start gap-2 lg:justify-items-center">
            <WhatsAppButton testId="about-whatsapp" />
            <span dir="ltr" className="text-sm font-medium text-muted">
              {WHATSAPP_NUMBER}
            </span>
          </div>
        </div>
      </div>
    </section>
  );
}
