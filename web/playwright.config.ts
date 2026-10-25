import { defineConfig } from "@playwright/test";
import { existsSync, globSync } from "node:fs";
import { chromium } from "@playwright/test";
import { fileURLToPath } from "node:url";

export const E2E_TOKEN = "e2e-token-0123456789abcdef";
const root = fileURLToPath(new URL("..", import.meta.url));
const dist = fileURLToPath(new URL("./dist", import.meta.url));

/** Uses Playwright's bundled Chromium when installed, otherwise the newest cached Chromium build. */
function executablePath(): string | undefined {
  if (process.env.PLAYWRIGHT_CHROMIUM_PATH) return process.env.PLAYWRIGHT_CHROMIUM_PATH;
  if (existsSync(chromium.executablePath())) return undefined;
  return globSync(`${process.env.HOME}/.cache/ms-playwright/chromium-*/chrome-linux*/chrome`).sort().at(-1);
}

export default defineConfig({
  testDir: "./e2e",
  // The dev host holds one demo session; tests reset it and must run sequentially.
  workers: 1,
  fullyParallel: false,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: "http://127.0.0.1:5399",
    viewport: { width: 1600, height: 1000 },
    launchOptions: { executablePath: executablePath() },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: {
    command: `dotnet run --project "${root}src/RoomOps.DevHost" --no-build -- --port 5399 --static "${dist}"`,
    url: "http://127.0.0.1:5399/",
    reuseExistingServer: false,
    timeout: 120_000,
    env: { ROOMOPS_DEV_TOKEN: E2E_TOKEN, ROOMOPS_DATA_DIR: fileURLToPath(new URL("./e2e/.data", import.meta.url)) },
  },
});
