import { resolve } from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
  build: {
    lib: {
      entry: resolve(import.meta.dirname, "src/main.ts"),
      name: "IntroSkipperDashboard",
      formats: ["iife"],
      fileName: () => "index.js",
      cssFileName: "index",
    },
    outDir: resolve(import.meta.dirname, "../IntroSkipper/Configuration"),
    emptyOutDir: false,
  },
});
