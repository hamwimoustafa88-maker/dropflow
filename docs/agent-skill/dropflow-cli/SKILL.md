---
name: dropflow-cli
description: Convert and process files locally with the `dropflow` command (images, PDF, documents, spreadsheets, data, e-books, subtitles, audio, video, archives). Use when a user asks to convert a file, shrink a video for WhatsApp or Discord, merge PDFs, or turn a folder of files into another format, and DropFlow is installed. Everything runs on the user's computer; nothing is uploaded.
---

# Driving DropFlow from the command line

DropFlow converts files **on this computer only**. It never uploads a file. Results are written **next to each source**
(`name (converted).ext`) or into `--out`; originals are never changed or deleted.

## 1. Find out what is possible

```
dropflow formats --json      # [{ "input": "heic", "targets": ["jpg", "png", ...] }, ...]
dropflow list --json         # every conversion and tool with its id and inputs
```

Only offer the user what `formats` / `list` report: DropFlow never claims a conversion it cannot do. Some targets are reached in
2-3 steps automatically (for example a spreadsheet to PNG goes through PDF); you do not have to chain them yourself.

## 2. Run, and read progress as events

```
dropflow convert photo.heic --to jpg --events
dropflow run vid.target-size clip.mp4 --opt sizeMb=16 --events
dropflow run preset:whatsapp-video clip.mp4 --events     # ready-made presets: whatsapp-video, discord-video, email-video, instagram-photo
```

`--events` prints one JSON object per line: `start`, `progress` (`value` 0-1), `output` (`path`), and a final `summary`
(`success`, `outputs`, `error`, `note`, `exitCode`). Use `--json` instead for a single object at the end.

## 3. Exit codes

| Code | Meaning | What to do |
|---|---|---|
| 0 | Success | Report the output paths. |
| 1 | A job failed, or a needed engine is missing | Show the `error` text to the user. |
| 2 | Usage error (bad arguments, missing file) | Fix the command; check the file path. |
| 3 | Nothing to convert: no tool for these files | Run `dropflow formats` and offer what exists. |

## 4. Folders

```
dropflow watch Inbox --to pdf      # converts every file that lands in Inbox into Inbox\DropFlow; Ctrl+C stops (exit 0)
```

## Rules

- Never pass passwords on the command line unless the user typed them for this purpose (`pdf.unlock` needs the real password; DropFlow
  never cracks passwords).
- AI models (speech to text, background removal, translation) download **only** after the user agrees:
  `dropflow models download <id> --yes`. Ask first.
- `dropflow doctor --json` shows the version, engines and models when something does not work.
