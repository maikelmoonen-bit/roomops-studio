import { createElement, Fragment, useSyncExternalStore, type ReactNode } from "react";
import { nl } from "./locales/nl";

/**
 * Localization. English is the source language: components call t("English text", { name }) and each other language is a
 * catalog keyed by that English text. A string missing from a catalog falls back to English, and a unit test lists every
 * t() string in the source that the Dutch catalog doesn't translate yet.
 *
 * Strings defined at module level (tables of labels, options) are marked with tx() and translated where they are rendered
 * with t(value), so they follow the language when it changes.
 */
export type Language = "en" | "nl";

export const languages: { value: Language; label: string; short: string }[] = [
  { value: "en", label: "English", short: "EN" },
  { value: "nl", label: "Nederlands", short: "NL" },
];

const catalogs: Record<Language, Record<string, string>> = { en: {}, nl };

let language: Language = "en";
const listeners = new Set<() => void>();

export function setLanguage(lang: string) {
  const next: Language = lang === "nl" ? "nl" : "en";
  if (next === language) return;
  language = next;
  if (typeof document !== "undefined") document.documentElement.lang = next;
  for (const listener of listeners) listener();
}

export function getLanguage(): Language {
  return language;
}

/** Re-renders the calling component when the language changes. */
export function useLanguage(): Language {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => language,
  );
}

type Vars = Record<string, string | number | null | undefined>;

function fill(template: string, vars?: Vars): string {
  if (!vars) return template;
  return template.replace(/\{(\w+)\}/g, (match, name: string) => (name in vars ? String(vars[name] ?? "") : match));
}

/** Translates English source text into the current language; {name} placeholders are filled from vars. */
export function t(text: string, vars?: Vars): string {
  return fill(catalogs[language][text] ?? text, vars);
}

/** Like t(), for sentences that embed elements: {name} placeholders are filled with React nodes. */
export function tr(text: string, nodes: Record<string, ReactNode>): ReactNode {
  const parts = (catalogs[language][text] ?? text).split(/(\{\w+\})/);
  return createElement(Fragment, null, ...parts.map((part) => {
    const name = /^\{(\w+)\}$/.exec(part)?.[1];
    return name !== undefined && name in nodes ? nodes[name] : part;
  }));
}

/** Chooses the singular or plural source text by count; {n} is the count. */
export function tn(count: number, one: string, many: string, vars?: Vars): string {
  return t(count === 1 ? one : many, { n: count.toLocaleString(locale()), ...vars });
}

/** Marks text for translation without translating it. Use for module-level constants and translate at render with t(). */
export function tx<T extends string>(text: T): T {
  return text;
}

/** BCP 47 locale for number and date formatting in the current language. */
export function locale(): string {
  return language === "nl" ? "nl-NL" : "en-GB";
}
