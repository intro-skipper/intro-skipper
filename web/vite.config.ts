import { resolve } from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
    build: {
        target: "es2018",
        lib: {
            entry: resolve(import.meta.dirname, "src/main.ts"),
            name: "IntroSkipperDashboard",
            formats: ["iife"],
            fileName: () => "introskipper.js",
            cssFileName: "introskipper",
        },
        outDir: resolve(import.meta.dirname, "../IntroSkipper/Configuration"),
        emptyOutDir: false,
    },
});
