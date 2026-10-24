/// <reference types="vitest/config" />
import path from 'node:path';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';

// During development the Vite dev server proxies API, auth and hub traffic to PcDiag.Web
// (fixed port 5180 in appsettings.Development.json). Open http://localhost:5173/auth/exchange?t=<token>
// using the token PcDiag.Web prints at startup.
const backend = process.env.PCDIAG_WEB_URL ?? 'http://127.0.0.1:5180';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, 'src') },
  },
  server: {
    port: 5173,
    strictPort: true,
    host: 'localhost',
    proxy: {
      '/api': { target: backend, changeOrigin: true },
      '/auth': { target: backend, changeOrigin: true },
      '/hubs': { target: backend, changeOrigin: true, ws: true },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    // Everything is local: no CDN, no inline scripts (keeps CSP script-src 'self').
    assetsInlineLimit: 0,
    // Served from this PC, never over the internet: the dashboard chunk (charts) may be large. It loads on demand.
    chunkSizeWarningLimit: 1000,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
