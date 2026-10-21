"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { formatLabel, opKey } from "@/engine/catalog-web";
import { formatBytes, extOf } from "@/engine/names";
import type { JobError, JobView, ResultFile } from "@/engine/session";
import { copyBlobText } from "./browser-actions";

type T = ReturnType<typeof useTranslations<"app">>;

export function errorText(t: T, error?: JobError): string {
  const code = !error?.code || error.code === "unknown" ? "generic" : error.code;
  const key = `err.${code}`;
  if (!t.has(key as never)) return t("err.generic");
  try {
    return t(key as never, (error?.params ?? {}) as never);
  } catch {
    return t("err.generic");
  }
}

function opLabel(t: T, operation: string): string {
  if (operation.startsWith("convert:")) return `→ ${formatLabel(operation.slice(8))}`;
  const key = `ops.${opKey(operation)}`;
  return t.has(key as never) ? t(key as never) : operation;
}

function Spinner() {
  return (
    <svg className="spin size-5 shrink-0 text-primary" viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <circle cx="12" cy="12" r="9" stroke="var(--border)" strokeWidth="3" />
      <path d="M12 3a9 9 0 0 1 9 9" stroke="currentColor" strokeWidth="3" strokeLinecap="round" />
    </svg>
  );
}

function CloseIcon() {
  return (
    <svg viewBox="0 0 24 24" className="size-4" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" aria-hidden="true">
      <path d="M6 6l12 12M18 6L6 18" />
    </svg>
  );
}

export function JobList({ jobs, onCancel, onDismiss }: { jobs: readonly JobView[]; onCancel: (id: string) => void; onDismiss: (id: string) => void }) {
  const t = useTranslations("app");
  const visible = jobs.filter((j) => j.state !== "done");
  if (visible.length === 0) return null;
  return (
    <ul aria-label={t("status.running")} className="grid grid-cols-[minmax(0,1fr)] gap-2" data-testid="jobs">
      {visible.map((j) => {
        const active = j.state === "waiting" || j.state === "running";
        const name = j.inputNames.length > 1 ? `${j.inputNames[0]} +${j.inputNames.length - 1}` : j.inputNames[0];
        return (
          <li key={j.id} data-state={j.state} className="glass rounded-2xl p-3">
            <div className="flex items-center gap-3">
              {j.state === "running" ? <Spinner /> : null}
              <div className="min-w-0 flex-1">
                <p className="truncate font-semibold" dir="auto">
                  {name}
                </p>
                <p className="text-sm text-muted">
                  <span className="font-mono" dir="ltr">
                    {opLabel(t, j.operation)}
                  </span>
                  {" · "}
                  <span className={j.state === "error" ? "font-semibold text-(--danger)" : undefined}>
                    {j.state === "error" ? errorText(t, j.error) : t(`status.${j.state === "waiting" ? "waiting" : j.state === "running" ? "running" : "cancelled"}`)}
                  </span>
                </p>
              </div>
              <button
                type="button"
                onClick={() => (active ? onCancel(j.id) : onDismiss(j.id))}
                aria-label={active ? t("cancel") : t("dismiss")}
                className="grid size-11 shrink-0 place-items-center rounded-full text-muted hover:bg-surface-2 hover:text-fg"
              >
                <CloseIcon />
              </button>
            </div>
            {j.state === "running" ? (
              <div className="bar mt-2" role="progressbar" aria-label={name} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(j.progress * 100)}>
                <span style={{ width: `${Math.max(3, Math.round(j.progress * 100))}%` }} />
              </div>
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}

function FileBadge({ result }: { result: ResultFile }) {
  if (result.isImage) {
    return (
      // eslint-disable-next-line @next/next/no-img-element -- blob: preview of a local result, next/image cannot optimise it
      <img src={result.url} alt="" className="size-12 shrink-0 rounded-xl border border-border bg-surface-2 object-cover" />
    );
  }
  const ext = extOf(result.name).slice(0, 4).toUpperCase() || "FILE";
  return (
    <span dir="ltr" className="grid size-12 shrink-0 place-items-center rounded-xl border border-border bg-primary-soft font-mono text-xs font-bold">
      {ext}
    </span>
  );
}

export function ResultList({
  results,
  onRemove,
  onDownloadAll,
  onClear,
  onToast,
}: {
  results: readonly ResultFile[];
  onRemove: (id: string) => void;
  onDownloadAll: () => void;
  onClear: () => void;
  onToast: (message: string) => void;
}) {
  const t = useTranslations("app");
  const [copied, setCopied] = useState<string | null>(null);
  if (results.length === 0) return null;

  async function copy(r: ResultFile) {
    const ok = await copyBlobText([r.file]);
    if (ok) {
      setCopied(r.id);
      onToast(t("results.copied"));
      setTimeout(() => setCopied((c) => (c === r.id ? null : c)), 2000);
    }
  }

  return (
    <section aria-labelledby="results-title" data-testid="results" className="min-w-0">
      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
        <h2 id="results-title" className="text-xl font-bold">
          {t("results.title")}
        </h2>
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={onDownloadAll}
            data-testid="download-all"
            className="min-h-11 rounded-full bg-primary px-5 text-sm font-bold text-on-primary shadow-md shadow-primary/25"
          >
            {results.length > 1 ? `${t("results.all")} · ${t("results.zip")}` : t("results.download")}
          </button>
          <button type="button" onClick={onClear} className="min-h-11 rounded-full px-3 text-sm font-semibold text-muted hover:text-fg">
            {t("results.clear")}
          </button>
        </div>
      </div>
      <ul aria-label={t("results.list")} className="grid grid-cols-[minmax(0,1fr)] gap-2">
        {results.map((r) => (
          <li key={r.id} data-testid="result" className="glass flex items-center gap-3 rounded-2xl p-2.5 ps-3">
            <FileBadge result={r} />
            <div className="min-w-0 flex-1">
              <p className="truncate font-semibold" dir="auto" title={r.path}>
                {r.name}
              </p>
              <p className="text-sm text-muted" dir="ltr">
                {formatBytes(r.size)}
              </p>
            </div>
            {r.isText ? (
              <button
                type="button"
                onClick={() => void copy(r)}
                className="min-h-11 rounded-full border border-border bg-surface px-4 text-sm font-semibold"
              >
                {copied === r.id ? t("results.copied") : t("results.copy")}
              </button>
            ) : null}
            <a
              href={r.url}
              download={r.name}
              className="min-h-11 content-center rounded-full bg-primary-soft px-4 text-sm font-bold"
            >
              {t("results.download")}
            </a>
            <button
              type="button"
              onClick={() => onRemove(r.id)}
              aria-label={`${t("results.remove")}: ${r.name}`}
              className="grid size-11 shrink-0 place-items-center rounded-full text-muted hover:bg-surface-2 hover:text-fg"
            >
              <CloseIcon />
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
