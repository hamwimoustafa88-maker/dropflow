# ADR-0006: AI models are downloaded only after the user agrees

Date: 2026-10-21 · Status: Accepted

## Decision
Nothing downloads a model implicitly. A converter that lacks a model fails fast with `Model '<id>' is not installed`. The app
recognises that failure (`AiFailures.TryParseMissingModel`), shows a dialog with the model name, size, source host and licence, and
only after "Yes" downloads it through `ModelManager` (resumable with HTTP Range, SHA-256 pinned per catalog entry, written to
`name.part` and moved into place). The same job is then run again **once**; a second "not installed" is shown as a normal failure.
A catalog entry without a pinned hash is never downloaded. Settings lists every model with its size and a Download/Delete button.
Models with non-commercial or research-only licences (for example RMBG-1.4) are not in the catalog, and a test enforces it.

## Why
The product promise is "nothing leaves your device" and "no surprises"; models are hundreds of megabytes and come from a third party.
The user must see what is fetched, from where, under which licence, and be able to remove it.

## Consequences
The first run of an AI tool needs the options dialog before the consent dialog (the failure is only known when the job runs).
`Availability.NeedsModel` on `IConverter` would remove that detour and is a possible later refinement.
