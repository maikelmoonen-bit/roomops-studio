import { expect, test } from "@playwright/test";
import { applyPlan, freshDemo, go, open, openRoom } from "./fixtures";

test.describe("session and navigation", () => {
  test("welcome screen offers connection and demo mode; demo is clearly labeled", async ({ page }) => {
    await open(page);
    const band = page.getByTestId("demo-band");
    if (await band.isVisible()) await band.getByRole("button", { name: "Exit demo" }).click();
    await expect(page.getByRole("heading", { name: /Meeting room administration/ })).toBeVisible();
    await page.getByTestId("start-demo").click();
    await expect(band).toContainText("Nothing is sent to Microsoft 365");
    await expect(page.getByTestId("tenant-switcher")).toContainText("Contoso (Demo)");
    await expect(page.getByText("26 room mailboxes, 5 room lists, 3 buildings")).toBeVisible();
  });

  test("every navigation entry opens a populated page", async ({ page }) => {
    await freshDemo(page);
    const pages: [string, string][] = [
      ["Rooms", "Rooms"], ["Room lists", "Room lists"], ["Buildings & floors", "Buildings & floors"], ["Microsoft Places", "Microsoft Places"],
      ["Booking policies", "Booking policies"], ["Resource delegates", "Resource delegates"], ["Working hours", "Working hours"],
      ["Configuration templates", "Configuration templates"], ["Bulk operations", "Bulk operations"], ["Validation center", "Validation center"],
      ["Change history", "Change history"], ["Connections", "Connections"], ["Settings", "Settings"], ["Dashboard", "Contoso (Demo)"],
    ];
    const nav = page.getByRole("navigation", { name: "Primary" });
    for (const [link, heading] of pages) {
      await nav.getByRole("link", { name: link, exact: true }).click();
      await expect(page.getByRole("heading", { level: 1, name: heading })).toBeVisible();
    }
  });

  test("command palette searches rooms and navigates", async ({ page }) => {
    await freshDemo(page);
    await page.keyboard.press("Control+K");
    await page.getByPlaceholder("Search rooms, pages and actions…").fill("Boardroom");
    await expect(page.getByRole("option", { name: /EHV 3.12 Boardroom/ })).toBeVisible();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("tab", { name: "Booking rules" })).toBeVisible();
    await expect(page.getByText("ehv-3.12@contoso-demo.example").first()).toBeVisible();
  });

  test("theme can be switched between dark and light", async ({ page }) => {
    await freshDemo(page);
    await page.getByRole("button", { name: "Theme" }).click();
    await page.getByRole("menuitem", { name: "Light" }).click();
    await expect(page.locator("html")).not.toHaveClass(/dark/);
    await page.getByRole("button", { name: "Theme" }).click();
    await page.getByRole("menuitem", { name: "Dark" }).click();
    await expect(page.locator("html")).toHaveClass(/dark/);
  });
});

test.describe("room inventory", () => {
  test("search, quick filters and card mode", async ({ page }) => {
    await freshDemo(page, "/rooms");
    await page.getByLabel("Search rooms").fill("Teams Room");
    await expect(page.getByRole("row").filter({ hasText: "Teams Room" })).toHaveCount(3);
    await page.getByLabel("Search rooms").fill("");
    await page.getByRole("button", { name: "Capacity missing" }).click();
    await expect(page.getByText("2 rooms of 26")).toBeVisible();
    await page.getByRole("radio", { name: "Cards" }).click();
    await expect(page.getByText("capacity not set").first()).toBeVisible();
  });

  test("room detail separates Exchange and Places objects", async ({ page }) => {
    await freshDemo(page);
    await openRoom(page, "Boardroom");
    await expect(page.getByRole("heading", { name: "Exchange Online room mailbox" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Microsoft Places room" })).toBeVisible();
    await page.getByRole("tab", { name: "Advanced" }).click();
    await expect(page.getByText("ExternalDirectoryObjectId")).toBeVisible();
    await expect(page.getByText("PlaceId", { exact: true })).toBeVisible();
  });

  test("synchronized mailboxes explain the authoritative location", async ({ page }) => {
    await freshDemo(page);
    await openRoom(page, "Legacy Conference Room B");
    await page.getByRole("tab", { name: "Location & facilities" }).click();
    await expect(page.getByText("Managed on-premises")).toBeVisible();
  });
});

test.describe("booking policy designer", () => {
  test("edit, preview, apply and verify a booking window change", async ({ page }) => {
    await freshDemo(page);
    await openRoom(page, "Project Room");
    await page.getByRole("tab", { name: "Booking rules" }).click();
    await expect(page.getByText("Policy in plain language")).toBeVisible();
    await page.getByRole("textbox", { name: "Booking window in days" }).fill("45");
    await expect(page.getByText("it starts within 45 days")).toBeVisible();
    await page.getByRole("button", { name: "Preview changes" }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Booking window", { exact: true })).toBeVisible();
    await dialog.getByText("Technical Execution Preview").click();
    await expect(dialog.getByText("-BookingWindowInDays 45")).toBeVisible();
    await applyPlan(page);
    await expect(dialog.getByText("All 1 changed value(s) were read back and match.")).toBeVisible();
    await dialog.getByRole("button", { name: "Done" }).click();
    await expect(page.getByText("The draft matches the room's current configuration.")).toBeVisible();
  });

  test("delegate approval without delegates is blocked", async ({ page }) => {
    await freshDemo(page);
    await openRoom(page, "Collaboration");
    await page.getByRole("tab", { name: "Booking rules" }).click();
    await page.getByRole("radio", { name: /Delegate approval required/ }).click();
    await expect(page.getByText("no booking delegates are assigned").first()).toBeVisible();
    await expect(page.getByText("Add at least one delegate to approve requests.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Preview changes" })).toBeDisabled();
  });

  test("only selected users can book: chosen with the access model, applied and recognized after reading back", async ({ page }) => {
    await freshDemo(page);
    await openRoom(page, "Collaboration");
    await page.getByRole("tab", { name: "Booking rules" }).click();
    await expect(page.getByRole("radio", { name: /Everyone – Automatic booking/ })).toHaveAttribute("aria-checked", "true");

    await page.getByRole("radio", { name: /Selected users only/ }).click();
    const panel = page.locator("#section-access");
    for (const name of ["Sanne", "Daan", "Emma", "Lucas", "Noah"]) {
      await panel.locator("#access-BookInPolicy").getByRole("button", { name: /Add/ }).click();
      await page.getByPlaceholder("Search by name or address…").fill(name);
      await page.getByRole("option", { name: new RegExp(name) }).first().click();
    }
    const summary = page.getByTestId("access-summary");
    await expect(summary).toContainText("5 selected users or groups");
    await expect(summary).toContainText("Booking denied");
    await expect(summary).toContainText("Approval required");
    await expect(page.getByText("Settings this changes")).toBeVisible();

    await page.getByRole("button", { name: "Preview changes" }).click();
    await applyPlan(page);
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("were read back and match", { exact: false })).toBeVisible();
    await dialog.getByRole("button", { name: "Done" }).click();

    // Read back: the room now matches the model, and nothing is pending.
    await expect(page.getByRole("radio", { name: /Selected users only/ })).toHaveAttribute("aria-checked", "true");
    await expect(page.getByRole("radio", { name: /Selected users only/ })).toContainText("Current");
    await expect(page.getByText("The draft matches the room's current configuration.")).toBeVisible();
  });

  test("the interface switches between English and Dutch", async ({ page }) => {
    await freshDemo(page);
    const toggle = page.getByTestId("language-toggle");
    await toggle.getByRole("radio", { name: "Nederlands" }).click();
    await expect(page.getByRole("navigation", { name: "Hoofdnavigatie" }).getByText("Ruimtes", { exact: true })).toBeVisible();
    await go(page, "/rooms");
    await page.getByRole("row").filter({ hasText: "Collaboration" }).first().click();
    await page.getByRole("tab", { name: "Boekingsregels" }).click();
    await expect(page.getByRole("radio", { name: /Iedereen – automatisch boeken/ })).toBeVisible();
    await expect(page.getByText("accepteert automatisch boekingsaanvragen", { exact: false })).toBeVisible();
    await toggle.getByRole("radio", { name: "English" }).click();
    await expect(page.getByRole("tab", { name: "Booking rules" })).toBeVisible();
  });

  test("scenario simulator analyzes a request without claiming a booking", async ({ page }) => {
    await freshDemo(page);
    await openRoom(page, "Boardroom");
    await page.getByRole("tab", { name: "Booking rules" }).click();
    await expect(page.getByText("not an Exchange booking transaction", { exact: false })).toBeVisible();
    await page.getByRole("button", { name: "Analyze request" }).click();
    await expect(page.getByText("Likely declined")).toBeVisible();
    await expect(page.getByText("Limitations")).toBeVisible();
  });
});

test.describe("change workflows", () => {
  test("create a room end to end with provisioning stages", async ({ page }) => {
    await freshDemo(page, "/rooms/new");
    await page.getByLabel("Display name").fill("EHV 2.30 Design Studio");
    await expect(page.getByText("Available")).toBeVisible();
    await page.getByLabel("Capacity").fill("8");
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Validate" }).click();
    await expect(page.getByText(/Ready: \d+ commands planned/)).toBeVisible();
    await page.getByRole("button", { name: "Preview changes" }).click();
    const dialog = page.getByRole("dialog");
    await dialog.getByText("Technical Execution Preview").click();
    await expect(dialog.getByText("New-Mailbox -Name 'ehv.2.30.design.studio' -Room").first()).toBeVisible();
    await applyPlan(page);
    await expect(dialog.getByText("Accepted by Microsoft 365")).toBeVisible();
    await expect(dialog.getByText("Object created")).toBeVisible();
  });

  test("dragging a room between room lists proposes a membership change", async ({ page }) => {
    await freshDemo(page, "/room-lists");
    await page.getByRole("radio", { name: "Board" }).click();
    const source = page.getByTestId("board-column-__unassigned").getByText("Quiet Room");
    const target = page.locator("[data-testid^=board-column-]").filter({ hasText: "Amsterdam Zuidas" }).first();
    await source.dragTo(target);
    await expect(page.getByText("1 proposed change")).toBeVisible();
    await page.getByRole("button", { name: "Preview changes" }).click();
    const dialog = await applyPlan(page);
    await expect(dialog.getByText("Applied and verified")).toBeVisible();
  });

  test("removals require explicit acknowledgement", async ({ page }) => {
    await freshDemo(page, "/room-lists");
    await page.getByRole("button", { name: /Executive Rooms/ }).first().click();
    await page.getByRole("button", { name: "Remove AMS 4.08 Client Suite" }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("button", { name: "Apply configuration" })).toBeDisabled();
    await applyPlan(page, { acknowledge: true });
  });

  test("calendar folder roles change in place; Full Access overlap is pointed out; people can be found", async ({ page }) => {
    await freshDemo(page);
    await openRoom(page, "ehv-1.15@");
    await page.getByRole("tab", { name: "Delegates & access" }).click();
    await expect(page.getByText("1 person in this list also has Full Access to the room mailbox")).toBeVisible();

    const role = page.getByRole("combobox", { name: "Access rights for Julia Meijer" });
    await expect(role).toHaveText("LimitedDetails");
    await role.click();
    await page.getByRole("option", { name: "Editor" }).click();
    const dialog = await applyPlan(page);
    await expect(dialog.getByText("Applied and verified")).toBeVisible();
    await dialog.getByRole("button", { name: "Done" }).click();
    await expect(role).toHaveText("Editor");

    await page.getByRole("button", { name: "Choose a person" }).click();
    await page.getByPlaceholder("Search by name or address…").fill("emma");
    await expect(page.getByRole("option", { name: /Emma Bakker/ })).toBeVisible();
  });

  test("validation finding is remediated through the plan pipeline", async ({ page }) => {
    await freshDemo(page);
    await openRoom(page, "Legacy Conference Room B");
    await expect(page.getByText("Unresolved recipient references").first()).toBeVisible();
    await page.getByRole("button", { name: "Remediate" }).first().click();
    await applyPlan(page);
    await page.getByRole("dialog").getByRole("button", { name: "Done" }).click();
    await page.getByRole("button", { name: "Reread" }).click();
    await expect(page.getByText("Unresolved recipient references")).toHaveCount(0);
  });

  test("change history shows executed commands and verification", async ({ page }) => {
    await freshDemo(page, "/history");
    await expect(page.getByText("application-local log")).toBeVisible();
    await page.getByRole("row").filter({ hasText: "Huddle" }).first().click();
    const sheet = page.getByRole("dialog");
    await expect(sheet.getByText("Executed commands")).toBeVisible();
    await expect(sheet.getByText("Set-CalendarProcessing").first()).toBeVisible();
  });

  test("bulk CSV import validates rows before planning", async ({ page }) => {
    await freshDemo(page, "/bulk");
    const chooser = page.waitForEvent("filechooser");
    await page.getByRole("button", { name: "Choose file" }).click();
    await (await chooser).setFiles({
      name: "rooms.csv",
      mimeType: "text/csv",
      buffer: Buffer.from("Room;BookingWindowInDays;AllowRecurringMeetings\nehv-1.02@contoso-demo.example;30;yes\nnot.a.room@contoso-demo.example;30;yes\n"),
    });
    await expect(page.getByText("2 rows · 1 ready · 1 with errors")).toBeVisible();
    await expect(page.getByText(/No room mailbox matches/)).toBeVisible();
    await page.getByRole("button", { name: "Preview 1 row" }).click();
    await applyPlan(page);
  });
});

test.describe("layout", () => {
  test("no horizontal page overflow at 1366×768", async ({ page }) => {
    await page.setViewportSize({ width: 1366, height: 768 });
    await freshDemo(page);
    for (const route of ["/", "/rooms", "/room-lists", "/buildings", "/validation", "/history", "/settings"]) {
      await go(page, route);
      await page.waitForTimeout(600);
      const overflow = await page.evaluate(() => {
        const main = document.getElementById("main")!;
        return Math.max(main.scrollWidth - main.clientWidth, document.documentElement.scrollWidth - document.documentElement.clientWidth);
      });
      expect(overflow, `horizontal overflow on ${route}`).toBeLessThanOrEqual(0);
    }
  });
});
