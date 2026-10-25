import { useState, type ReactNode } from "react";
import { Check, Copy } from "lucide-react";
import { cn } from "@/lib/format";
import { t } from "@/lib/i18n";

type Token = { text: string; kind: "cmdlet" | "param" | "string" | "variable" | "comment" | "number" | "operator" | "plain" };

/** A focused PowerShell tokenizer for the scripts RoomOps generates (cmdlets, parameters, quoted literals, variables). */
export function tokenize(line: string): Token[] {
  const tokens: Token[] = [];
  const pattern = /(#.*$)|('(?:[^'’‘]|'')*')|(\$(?:true|false|null)\b|\$[A-Za-z_][\w:]*)|(\s-[A-Za-z][\w]*(?::\$(?:true|false))?)|(\b[A-Z][a-zA-Z]+-[A-Z][A-Za-z0-9]+\b)|(\b\d+\b)|(@\(\)|,)/g;
  let last = 0;
  for (const match of line.matchAll(pattern)) {
    if (match.index > last) tokens.push({ text: line.slice(last, match.index), kind: "plain" });
    const kind: Token["kind"] = match[1] ? "comment" : match[2] ? "string" : match[3] ? "variable" : match[4] ? "param" : match[5] ? "cmdlet" : match[6] ? "number" : "operator";
    tokens.push({ text: match[0], kind });
    last = match.index + match[0].length;
  }
  if (last < line.length) tokens.push({ text: line.slice(last), kind: "plain" });
  return tokens;
}

const color: Record<Token["kind"], string> = {
  cmdlet: "text-[#6ccaf8] font-medium",
  param: "text-[#c7a1f5]",
  string: "text-[#9ed39a]",
  variable: "text-[#f0b862]",
  comment: "text-[#6f7682] italic",
  number: "text-[#f0b862]",
  operator: "text-[#a3a9b4]",
  plain: "text-[#d7dae0]",
};

export function CodeBlock({ code, className, maxHeight = 360, toolbar }: { code: string; className?: string; maxHeight?: number; toolbar?: ReactNode }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className={cn("overflow-hidden rounded-md border border-[#272c34] bg-[#0e1013]", className)}>
      <div className="flex items-center justify-between border-b border-[#272c34] px-3 py-1.5">
        <span className="font-mono text-[10.5px] uppercase tracking-wider text-[#6f7682]">PowerShell</span>
        <div className="flex items-center gap-1">
          {toolbar}
          <button
            type="button"
            className="inline-flex items-center gap-1 rounded-[4px] px-1.5 py-0.5 text-[11px] text-[#a3a9b4] hover:bg-white/5 hover:text-white"
            onClick={() => void navigator.clipboard?.writeText(code).then(() => { setCopied(true); window.setTimeout(() => setCopied(false), 1400); })}
          >
            {copied ? <Check className="size-3 text-[#4fb286]" /> : <Copy className="size-3" />}
            {copied ? t("Copied") : t("Copy script")}
          </button>
        </div>
      </div>
      <pre className="scroll-area px-3 py-2.5 font-mono text-[11.75px] leading-[1.65]" style={{ maxHeight }}>
        {code.split("\n").map((line, i) => (
          <div key={i} className="whitespace-pre-wrap break-all">
            {line.length === 0 ? " " : tokenize(line).map((tok, j) => <span key={j} className={color[tok.kind]}>{tok.text}</span>)}
          </div>
        ))}
      </pre>
    </div>
  );
}
