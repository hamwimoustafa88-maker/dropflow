# ADR-0005: Accounts, registration and payments are deferred

Date: 2026-10-20 · Status: Accepted

## Decision
No sign-up, login, subscription or payment code is written before **Phase 9**. The landing page, the Windows app and the
web app are fully usable anonymously. When Phase 9 starts: Supabase Auth for accounts, a Merchant of Record
(Lemon Squeezy or Paddle) for paid plans, and settings/recipe sync as the first account-bound features. DropFlow is proprietary
(ADR-0010), so the app itself can also be sold.

## Consequences
Architecture must not assume a user identity: settings are local files; the catalog has no entitlement fields yet.
