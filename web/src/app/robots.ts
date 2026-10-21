import type { MetadataRoute } from "next";
import { SITE_URL } from "@/lib/site";

// The whole site may be crawled and indexed (it went public with the GPL release, ADR-0013).
export default function robots(): MetadataRoute.Robots {
  return {
    rules: { userAgent: "*", allow: "/" },
    sitemap: `${SITE_URL}/sitemap.xml`,
  };
}
