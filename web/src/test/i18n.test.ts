import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import ts from "typescript";
import { afterEach, describe, expect, it } from "vitest";
import { setLanguage, t, tn } from "@/lib/i18n";
import { nl, nlParts } from "@/lib/locales/nl";

const root = join(__dirname, "..");

/** Every literal passed to t(), tx(), tr() and tn() in the frontend sources. */
function sourceStrings(): Map<string, string> {
  const found = new Map<string, string>();
  const walk = (dir: string) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const path = join(dir, entry.name);
      if (entry.isDirectory()) {
        if (entry.name !== "test" && entry.name !== "locales") walk(path);
      } else if (/\.tsx?$/.test(entry.name) && !entry.name.endsWith(".gen.ts")) {
        const file = ts.createSourceFile(path, readFileSync(path, "utf8"), ts.ScriptTarget.Latest, true, path.endsWith("x") ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
        const visit = (node: ts.Node) => {
          if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && ["t", "tx", "tr", "tn"].includes(node.expression.text)) {
            const args = node.expression.text === "tn" ? node.arguments.slice(1, 3) : node.arguments.slice(0, 1);
            for (const arg of args) {
              if (ts.isStringLiteral(arg) || ts.isNoSubstitutionTemplateLiteral(arg)) found.set(arg.text, relative(root, path));
            }
          }
          ts.forEachChild(node, visit);
        };
        visit(file);
      }
    }
  };
  walk(root);
  return found;
}

const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

describe("Dutch catalog", () => {
  const strings = sourceStrings();

  it("translates every string the interface uses", () => {
    const missing = [...strings].filter(([text]) => !(text in nl)).map(([text, file]) => `${file}: ${text}`);
    expect(missing).toEqual([]);
  });

  it("keeps the placeholders of the source text", () => {
    const broken = Object.entries(nl).filter(([en, dutch]) => placeholders(en).join() !== placeholders(dutch).join()).map(([en]) => en);
    expect(broken).toEqual([]);
  });

  it("has one translation per source text across its parts", () => {
    const seen = new Map<string, string>();
    const conflicts: string[] = [];
    for (const part of Object.values(nlParts)) {
      for (const [en, dutch] of Object.entries(part)) {
        if (seen.has(en) && seen.get(en) !== dutch) conflicts.push(en);
        seen.set(en, dutch);
      }
    }
    expect(conflicts).toEqual([]);
  });

  it("contains no entries the interface no longer uses", () => {
    expect(Object.keys(nl).filter((en) => !strings.has(en))).toEqual([]);
  });
});

describe("translation helpers", () => {
  afterEach(() => setLanguage("en"));

  it("falls back to English and fills placeholders", () => {
    expect(t("Remove {name}", { name: "EHV 3.12" })).toBe("Remove EHV 3.12");
    setLanguage("nl");
    expect(t("Remove {name}", { name: "EHV 3.12" })).toBe("EHV 3.12 verwijderen");
    expect(t("A sentence without a translation")).toBe("A sentence without a translation");
  });

  it("chooses singular and plural forms by count", () => {
    setLanguage("nl");
    expect(tn(1, "{n} room", "{n} rooms")).toBe("1 ruimte");
    expect(tn(3, "{n} room", "{n} rooms")).toBe("3 ruimtes");
  });
});
