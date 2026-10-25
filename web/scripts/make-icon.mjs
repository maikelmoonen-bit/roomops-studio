// Renders the RoomOps mark at standard Windows icon sizes and packs them into a multi-resolution .ico (PNG entries).
// Usage: node scripts/make-icon.mjs <out.ico> [out-256.png]
import { chromium } from "@playwright/test";
import { existsSync, globSync, writeFileSync } from "node:fs";

const [out = "roomops.ico", png256] = process.argv.slice(2);
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

// At small sizes the outline strokes are thickened so the four "rooms" stay legible.
const svg = (size) => {
  const stroke = size <= 24 ? 2.4 : size <= 48 ? 2 : 1.6;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 32 32">
  <rect width="32" height="32" rx="7.5" fill="#16191e"/>
  <rect x="0.5" y="0.5" width="31" height="31" rx="7" fill="none" stroke="#2c323b"/>
  <rect x="7" y="7" width="8" height="8" rx="2" fill="none" stroke="#a3abbb" stroke-width="${stroke}"/>
  <rect x="17" y="7" width="8" height="8" rx="2" fill="#36b5f2"/>
  <rect x="7" y="17" width="8" height="8" rx="2" fill="none" stroke="#a3abbb" stroke-width="${stroke}"/>
  <rect x="17" y="17" width="8" height="8" rx="2" fill="none" stroke="#a3abbb" stroke-width="${stroke}"/>
</svg>`;
};

const executablePath = existsSync(chromium.executablePath()) ? undefined : globSync(`${process.env.HOME}/.cache/ms-playwright/chromium-*/chrome-linux*/chrome`).sort().at(-1);
const browser = await chromium.launch({ executablePath });
const page = await browser.newPage();
const images = [];
for (const size of sizes) {
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(`<html><body style="margin:0;background:transparent">${svg(size)}</body></html>`);
  images.push({ size, data: await page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } }) });
}
await browser.close();

const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(images.length, 4);
const entries = [];
let offset = 6 + 16 * images.length;
for (const { size, data } of images) {
  const entry = Buffer.alloc(16);
  entry.writeUInt8(size >= 256 ? 0 : size, 0);
  entry.writeUInt8(size >= 256 ? 0 : size, 1);
  entry.writeUInt8(0, 2);
  entry.writeUInt8(0, 3);
  entry.writeUInt16LE(1, 4);
  entry.writeUInt16LE(32, 6);
  entry.writeUInt32LE(data.length, 8);
  entry.writeUInt32LE(offset, 12);
  offset += data.length;
  entries.push(entry);
}
writeFileSync(out, Buffer.concat([header, ...entries, ...images.map((i) => i.data)]));
if (png256) writeFileSync(png256, images.at(-1).data);
console.log(`wrote ${out} (${images.length} sizes)`);
