# ADR-0014: English and Arabic translation runs locally with Opus-MT (ONNX Runtime)

Date: 2026-10-23 · Status: Accepted (owner request: translate text and images, English to Arabic and Arabic to English, 100 % local) · Builds on ADR-0006

## Context
Translation is the first feature where "send it to a cloud service" is the obvious shortcut. DropFlow's promise is that file contents never leave the PC,
so the translation has to run on the PC, with a licence that allows redistribution of the weights' download.

## Decision
- **Engine:** Opus-MT (Helsinki-NLP, MarianMT) `opus-mt-en-ar` and `opus-mt-ar-en`, in the ONNX export of `Xenova/` (Transformers.js), pinned to commit hashes:
  int8 encoder + int8 merged decoder + `source.spm` + `vocab.json`, 108 MB per direction. int8 was compared with fp32 and uint8 on real sentences:
  int8 equals fp32, uint8 made visible mistakes. Run with the ONNX Runtime package the AI project already uses (CPU).
- **Licence:** the Hugging Face model cards of the Helsinki-NLP repositories say Apache-2.0; the OPUS-MT project describes the models as CC-BY-4.0. Both
  are permissive; attribution is in `THIRD-PARTY-NOTICES.md`. **Not used:** NLLB (CC-BY-NC), any research-only model, any cloud API (privacy and licence).
- **Tokenizer:** Microsoft.ML.Tokenizers 2.0.1 (MIT, + Google.Protobuf, BSD-3-Clause), the one package the owner approved. Its `SentencePieceTokenizer` reads the
  Marian `source.spm` correctly (NFKC, ligatures, full-width forms, Arabic with diacritics, mixed scripts); pieces are mapped to ids through `vocab.json` as
  the Hugging Face `MarianTokenizer` does. No own Unigram implementation was needed. Decoding needs no SentencePiece model.
- **Decoding:** own batched beam search (greedy = beam 1) with the merged decoder's key/value cache, a no-repeat 4-gram guard and a length guard. The English to
  Arabic model needs the `>>ara<<` target tag in front of its input.
- **Models are packs:** `ModelInfo.Files` lets one catalogue entry own several files (one id, one consent, one folder, per-file SHA-256 and resume).
  Downloads still happen only after the user agrees (ADR-0006); the CLI needs `dropflow models download <id> --yes`.
- **Tools:** `txt.translate` (SRT, VTT, TXT, Markdown, HTML), `img.translate` (OCR first, optional overlay), `pdf.translate` (text layer to TXT/MD). Direction
  `auto` is decided by the share of Arabic-script letters; text already in the target language is left alone.

## Consequences
- English to Arabic quality is limited (the card BLEU is 14 against 49 for Arabic to English): outputs are drafts. A larger model (`opus-mt-tc-big-en-ar`)
  has no published ONNX export; see `docs/deferred.md` C14.
- Web, macOS/Linux and Android do not translate yet (`docs/deferred.md` C12, C13).
- A loaded model takes about 0.6 GB of memory (measured: 590 MB working set of the whole process while translating at the "best" setting); it is
  released after three idle minutes.
