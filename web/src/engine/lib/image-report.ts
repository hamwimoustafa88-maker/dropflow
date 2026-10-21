// Plain-text metadata report for img.metadata "view". Pure, Node-compatible. English labels are fine inside the report.

import type { ExifData } from "./exif";
import type { PngTextEntry } from "./strip-meta";

export interface MetadataInfo {
  name: string;
  mime: string;
  size: number;
  width?: number;
  height?: number;
  exif: ExifData;
  pngText: PngTextEntry[];
  hasXmp: boolean;
}

function round(n: number, digits: number): string {
  const f = 10 ** digits;
  return String(Math.round(n * f) / f);
}

export function formatExposure(seconds: number): string {
  if (!(seconds > 0)) return `${seconds} s`;
  if (seconds >= 1) return `${round(seconds, 1)} s`;
  return `1/${Math.round(1 / seconds)} s`;
}

export function formatReport(info: MetadataInfo): string {
  const lines: string[] = [];
  const add = (k: string, v: string | number | undefined) => {
    if (v !== undefined && v !== "") lines.push(`${k}: ${v}`);
  };
  add("File", info.name);
  add("Type", info.mime);
  add("Size", `${info.size} bytes`);
  if (info.width && info.height) add("Dimensions", `${info.width} x ${info.height}`);

  const e = info.exif;
  const before = lines.length;
  add("Make", e.make);
  add("Model", e.model);
  add("Software", e.software);
  add("DateTime", e.dateTime);
  add("DateTimeOriginal", e.dateTimeOriginal);
  add("Orientation", e.orientation);
  if (e.exposureTime !== undefined) add("ExposureTime", formatExposure(e.exposureTime));
  if (e.fNumber !== undefined) add("FNumber", `f/${round(e.fNumber, 1)}`);
  add("ISO", e.iso);
  if (e.focalLength !== undefined) add("FocalLength", `${round(e.focalLength, 1)} mm`);
  add("LensModel", e.lensModel);
  if (e.gps) {
    if (e.gps.latitude !== undefined) add("GPS latitude", e.gps.latitude.toFixed(6));
    if (e.gps.longitude !== undefined) add("GPS longitude", e.gps.longitude.toFixed(6));
    if (e.gps.altitude !== undefined) add("GPS altitude", `${round(e.gps.altitude, 1)} m`);
  }
  for (const t of info.pngText) add(`Text ${t.keyword}`, t.text.replace(/\s+/g, " ").trim());
  if (info.hasXmp) add("XMP", "present");
  if (lines.length === before) lines.push("No metadata");
  return lines.join("\n") + "\n";
}
