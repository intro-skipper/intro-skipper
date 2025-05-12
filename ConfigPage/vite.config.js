import { defineConfig } from "vite";
import viteReact from "@vitejs/plugin-react";
import { resolve } from "node:path";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [viteReact()],
  test: {
    globals: true,
    environment: "jsdom",
  },
  resolve: {
    alias: {
      '@': resolve(__dirname, './src'),
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
    minify: 'terser',
    // For Jellyfin Plugin environment
    rollupOptions: {
      input: {
        main: resolve(__dirname, 'index.html'),
      },
      output: {
        entryFileNames: 'assets/[name].js',
        chunkFileNames: 'assets/[name].js',
        assetFileNames: 'assets/[name].[ext]'
      }
    }
  }
});
