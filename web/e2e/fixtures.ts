import { expect, type Page } from "@playwright/test";
import { E2E_TOKEN } from "../playwright.config";

/** Opens the app at a route with the dev-host token. */
export async function open(page: Page, route = "/") {
  await page.goto(`/?devtoken=${E2E_TOKEN}#${route}`);
}

/** Starts a fresh demo tenant so every test begins from the same synthetic state. */
export async function freshDemo(page: Page, route = "/") {
  await open(page, "/");
  const band = page.getByTestId("demo-band");
  const start = page.getByTestId("start-demo");
  await expect(band.or(start)).toBeVisible();
  if (await band.isVisible()) {
    await band.getByRole("button", { name: "Exit demo" }).click();
    await expect(start).toBeVisible();
  }
  await start.click();
  await expect(band).toBeVisible();
  if (route !== "/") await page.evaluate((r) => (window.location.hash = r), route);
}

export async function go(page: Page, route: string) {
  await page.evaluate((r) => (window.location.hash = r), route);
}

export async function openRoom(page: Page, name: string) {
  await go(page, "/rooms");
  await page.getByRole("row").filter({ hasText: name }).first().click();
  await expect(page.getByRole("tab", { name: "Overview" })).toBeVisible();
}

/** Applies the plan currently shown in the review dialog and waits for the execution to finish. */
export async function applyPlan(page: Page, options?: { acknowledge?: boolean }) {
  const dialog = page.getByRole("dialog");
  await expect(dialog.getByText("Target tenant")).toBeVisible();
  await expect(dialog.getByText("DEMO · no Microsoft 365 changes")).toBeVisible();
  if (options?.acknowledge) await dialog.getByRole("checkbox", { name: "Acknowledge removals" }).click();
  await dialog.getByRole("button", { name: "Apply configuration" }).click();
  await expect(dialog.getByRole("button", { name: "Done" })).toBeVisible({ timeout: 90_000 });
  return dialog;
}
