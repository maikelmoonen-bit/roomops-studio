import { expect, test } from "@playwright/test";
import { freshDemo, go, openRoom } from "./fixtures";

// Visual quality assurance: every major screen in dark and light mode, at two common resolutions.
const screens: [string, string, string?][] = [
  ["dashboard", "/"],
  ["rooms", "/rooms"],
  ["room-detail", "room:Boardroom"],
  ["booking-designer", "room:Boardroom", "Booking rules"],
  ["delegates", "room:Auditorium", "Delegates & access"],
  ["working-hours", "room:Training Room", "Working hours"],
  ["location", "room:Teams Room", "Location & facilities"],
  ["create-room", "/rooms/new"],
  ["room-lists", "/room-lists"],
  ["buildings", "/buildings"],
  ["places", "/places"],
  ["templates", "/templates"],
  ["bulk", "/bulk"],
  ["validation", "/validation"],
  ["history", "/history"],
  ["connections", "/connections"],
  ["settings", "/settings"],
];

for (const [width, height] of [[1920, 1080], [1366, 768]] as const) {
  for (const scheme of ["dark", "light"] as const) {
    test(`screens ${scheme} ${width}x${height}`, async ({ page }) => {
      await page.setViewportSize({ width, height });
      await page.emulateMedia({ colorScheme: scheme });
      await freshDemo(page);
      for (const [name, route, tab] of screens) {
        if (route.startsWith("room:")) {
          await openRoom(page, route.slice(5));
          if (tab) await page.getByRole("tab", { name: tab }).click();
        } else {
          await go(page, route);
        }
        // The dashboard is the first screen after sign-in; capture it once the inventory has loaded.
        if (name === "dashboard") await expect(page.getByText(/\d+ room mailboxes/)).toBeVisible({ timeout: 30_000 });
        await page.waitForTimeout(900);
        await page.screenshot({ path: `e2e/screenshots/${width}x${height}/${scheme}/${name}.png` });
      }
      // The change preview, including the Technical Execution Preview.
      await openRoom(page, "Huddle");
      await page.getByRole("tab", { name: "Booking rules" }).click();
      await page.getByRole("textbox", { name: "Booking window in days" }).fill("60");
      await page.getByRole("button", { name: "Preview changes" }).click();
      await page.getByRole("dialog").getByText("Technical Execution Preview").click();
      await page.waitForTimeout(500);
      await page.screenshot({ path: `e2e/screenshots/${width}x${height}/${scheme}/change-preview.png` });
    });
  }
}
