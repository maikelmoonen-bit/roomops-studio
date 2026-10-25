import { bridge } from "@/bridge/client";

/** Saves text through the host's native dialog when available, otherwise as a browser download. */
export async function saveText(fileName: string, mimeType: string, content: string): Promise<boolean> {
  const result = await bridge.call("files.save", { fileName, mimeType, content });
  if (result.saved) return true;
  if (!result.useBrowserDownload) return false;
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  a.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  return true;
}

/** Opens a text file through the host's native dialog, or the browser file picker in the dev host. */
export async function openText(extensions: string[]): Promise<{ name: string; content: string } | null> {
  const result = await bridge.call("files.open", { extensions });
  if (result.opened && result.content != null) return { name: result.fileName ?? "file", content: result.content };
  if (!result.useBrowserPicker) return null;
  return new Promise((resolve) => {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = extensions.map((e) => `.${e}`).join(",");
    input.onchange = async () => {
      const file = input.files?.[0];
      if (!file) return resolve(null);
      if (file.size > 10 * 1024 * 1024) return resolve(null);
      resolve({ name: file.name, content: await file.text() });
    };
    input.click();
  });
}

/**
 * CSV with a UTF-8 BOM so Excel opens accented characters correctly. Text that starts with a formula character is
 * prefixed with an apostrophe, so tenant-controlled values (room names, addresses) can't execute as spreadsheet formulas.
 */
export function toCsv(headers: string[], rows: (string | number | null | undefined)[][]): string {
  const esc = (v: string | number | null | undefined) => {
    let s = v == null ? "" : String(v);
    if (typeof v === "string" && /^[=+\-@\t\r]/.test(s)) s = `'${s}`;
    return /[",\n;]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  return "﻿" + [headers, ...rows].map((r) => r.map(esc).join(",")).join("\r\n");
}
