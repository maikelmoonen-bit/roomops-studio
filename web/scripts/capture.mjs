// Captures screenshots of every major screen against a running dev host, in dark and light themes.
// Usage: node scripts/capture.mjs <baseUrl-with-devtoken> <outDir> [width] [height] [onlyName]
import { chromium } from "@playwright/test";
import { existsSync, globSync, mkdirSync } from "node:fs";

const [base, out = "screenshots", width = "1600", height = "1000", only] = process.argv.slice(2);
mkdirSync(out, { recursive: true });

export function browserPath() {
  if (process.env.PLAYWRIGHT_CHROMIUM_PATH) return process.env.PLAYWRIGHT_CHROMIUM_PATH;
  if (existsSync(chromium.executablePath())) return undefined;
  return globSync(`${process.env.HOME}/.cache/ms-playwright/chromium-*/chrome-linux*/chrome`).sort().at(-1);
}

const routes = [
  ["dashboard", "/"],
  ["rooms", "/rooms"],
  ["rooms-cards", "/rooms", "Cards"],
  ["room-detail", "/rooms/{room}"],
  ["room-location", "/rooms/{room}?tab=location"],
  ["booking-designer", "/booking/{room}"],
  ["delegates", "/delegates/{room}"],
  ["working-hours", "/working-hours/{room}"],
  ["create-room", "/rooms/new"],
  ["room-lists", "/room-lists"],
  ["room-lists-board", "/room-lists", "Board"],
  ["buildings", "/buildings"],
  ["places", "/places"],
  ["templates", "/templates"],
  ["bulk", "/bulk"],
  ["validation", "/validation"],
  ["history", "/history"],
  ["connections", "/connections"],
  ["settings", "/settings"],
];

const go = async (page, hash) => {
  await page.evaluate((h) => (window.location.hash = h), hash);
  await page.waitForTimeout(1100);
};

const browser = await chromium.launch({ executablePath: browserPath() });
for (const theme of ["dark", "light"]) {
  const context = await browser.newContext({ viewport: { width: Number(width), height: Number(height) }, colorScheme: theme });
  const page = await context.newPage();
  await page.goto(base);
  await page.waitForTimeout(1200);

  // Welcome screen requires no active session.
  if (await page.getByTestId("demo-band").count()) {
    await page.getByRole("button", { name: "Exit demo" }).first().click();
    await page.waitForTimeout(800);
  }
  if (!only || only === "welcome") {
    await page.screenshot({ path: `${out}/welcome-${theme}.png` });
    console.log(`captured welcome-${theme}`);
  }
  await page.getByTestId("start-demo").click();
  await page.waitForSelector("[data-testid=demo-band]", { timeout: 15000 });
  await page.waitForTimeout(1500);

  // Discover a real room id through the UI.
  await go(page, "/rooms");
  await page.getByText("Boardroom", { exact: true }).first().click();
  await page.waitForTimeout(800);
  const roomId = decodeURIComponent((await page.evaluate(() => window.location.hash)).split("/")[2]?.split("?")[0] ?? "");

  for (const [name, hash, click] of routes) {
    if (only && name !== only) continue;
    await go(page, hash.replace("{room}", encodeURIComponent(roomId)));
    if (click) {
      await page.getByRole("radio", { name: click }).first().click();
      await page.waitForTimeout(500);
    }
    await page.screenshot({ path: `${out}/${name}-${theme}.png` });
    console.log(`captured ${name}-${theme}`);
  }
  await context.close();
}
await browser.close();
