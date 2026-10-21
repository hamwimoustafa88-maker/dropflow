# ADR-0011: Everyday documents become PDF and pictures through the built-in browser engine

Date: 2026-10-21 · Status: Accepted

## Context
The owner ran the app, converted a document to Markdown and text (fine), then to an image, and the app offered to download LibreOffice
(350 MB). The owner does not want anything external to be downloaded. Until then, every document-to-PDF/image pair went through
LibreOffice.

## Decision
- `DropFlow.Engines.Pdf` defines `IHtmlPdfRenderer` (print an HTML file to a PDF). `DropFlow.Engines.Web.WebPageRenderer` implements it
  with the WebView2 runtime that ships with Windows (one reused hidden window, network blocked unless a job allows it; see ADR-0008).
- `DocumentHtml` builds one self-contained HTML page in-process from HTML, text, Markdown, Word, spreadsheet/CSV/TSV/JSON and EPUB
  files (the existing rich-text model and readers). `BrowserDocumentTools` prints it to PDF; for PNG/JPG the PDF's pages are drawn with
  PDFium like any PDF (one page = one file, several = a folder).
- When the app supplies a renderer, `LibreOfficePlanner` drops these pairs, so LibreOffice is used only for what a browser cannot read
  (doc, odt, rtf, pages, pptx/ppt/odp/key, xls/ods/numbers). The CLI has no browser engine and keeps the LibreOffice pairs.
- A cross-module test asserts that html, txt, md, docx, csv, tsv, xlsx, json and epub reach pdf, png and jpg with no engine, and that the
  registry has no overlapping converters.
- Separate from this, helper-program install prompts are **off by default** (`AppSettings.OfferEngineInstalls`): a tool that needs a
  missing program says so and points to Settings > Engines instead of asking to download it.

## Consequences
- Layout is that of a web page, not of Word: fonts, headers/footers, page breaks, columns and images embedded in a DOCX are not
  reproduced (the rich model keeps headings, paragraphs, lists, tables, bold/italic/links, RTL). Users who need Word-exact output can
  install LibreOffice from Settings, but the app no longer routes these pairs through it.
- Still needing a helper program: video/audio (FFmpeg), qpdf tools, legacy Office formats and PowerPoint (LibreOffice), 7z creation.
  Bundling those in the installer is the way to make them "download-free" (licence review needed: ADR-0010).
