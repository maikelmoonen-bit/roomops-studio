import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { fileURLToPath } from "node:url";

// The production bundle runs inside WebView2 (or the loopback dev host). The CSP forbids remote content entirely:
// every script, style and font ships with the application.
const csp = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: blob:",
  "font-src 'self' data:",
  "connect-src 'self' ws://127.0.0.1:* ws://localhost:*",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join("; ");

function contentSecurityPolicy(): Plugin {
  return {
    name: "roomops-csp",
    apply: "build",
    transformIndexHtml: (html) => html.replace("<!--CSP-->", `<meta http-equiv="Content-Security-Policy" content="${csp}" />`),
  };
}

export default defineConfig({
  plugins: [react(), tailwindcss(), contentSecurityPolicy()],
  base: "./",
  resolve: { alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) } },
  server: {
    port: 5173,
    strictPort: true,
    proxy: { "/bridge": { target: "ws://127.0.0.1:5199", ws: true } },
  },
  build: { outDir: "dist", sourcemap: false, chunkSizeWarningLimit: 2000, assetsInlineLimit: 0 },
});
