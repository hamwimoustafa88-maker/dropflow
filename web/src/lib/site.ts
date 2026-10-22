// The site's own address and the public links shown on it. Every URL here is a link the visitor may click (or the site's
// own canonical address, used in <head> metadata, robots.txt and the sitemap); none is ever fetched by the page.

/** Canonical origin for metadata, robots.txt, sitemap.xml and JSON-LD. Override with NEXT_PUBLIC_SITE_URL (no trailing slash). */
export const SITE_URL = (process.env.NEXT_PUBLIC_SITE_URL || "https://dropflow.scouthub.dev").replace(/\/+$/, "");

// The installer lives on the GitHub release page (latest release); the page lists the Setup file and the checksums.
export const RELEASES_URL = "https://github.com/hamwimoustafa88-maker/dropflow/releases/latest";
/** GitHub's stable "file of the latest release" address: `<this>/<asset file name>` downloads that file directly. */
export const RELEASE_DOWNLOAD_URL = "https://github.com/hamwimoustafa88-maker/dropflow/releases/latest/download";
export const REPO_URL = "https://github.com/hamwimoustafa88-maker/dropflow";
export const CONTRIBUTING_URL = "https://github.com/hamwimoustafa88-maker/dropflow/blob/main/CONTRIBUTING.md";
export const LICENSE_URL = "https://github.com/hamwimoustafa88-maker/dropflow/blob/main/LICENSE";
export const TRADEMARK_URL = "https://github.com/hamwimoustafa88-maker/dropflow/blob/main/TRADEMARK.md";
export const NOTICES_URL = "https://github.com/hamwimoustafa88-maker/dropflow/blob/main/THIRD-PARTY-NOTICES.md";
export const ISSUES_URL = "https://github.com/hamwimoustafa88-maker/dropflow/issues";
export const SECURITY_URL = "https://github.com/hamwimoustafa88-maker/dropflow/blob/main/SECURITY.md";
export const SIGNING_POLICY_URL = "https://github.com/hamwimoustafa88-maker/dropflow/blob/main/docs/code-signing-policy.md";
export const GPL_URL = "https://www.gnu.org/licenses/gpl-3.0.html";
/** Contact for anything that is not a bug or a security report (shown on the legal pages and in the footer). */
export const WHATSAPP_URL = "https://wa.me/96170863903";
/** The same number as shown to people (always left-to-right, even inside Arabic text). */
export const WHATSAPP_NUMBER = "+961 70 863 903";

/** Links a legal-page section can show, by key (labels are in messages `legal.links.<key>`). */
export const LEGAL_LINKS = {
  gpl: GPL_URL,
  license: LICENSE_URL,
  source: REPO_URL,
  trademark: TRADEMARK_URL,
  notices: NOTICES_URL,
} as const;
export type LegalLinkKey = keyof typeof LEGAL_LINKS;
