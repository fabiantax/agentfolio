import { defineConfig } from 'vite';

export default defineConfig({
  build: {
    outDir: 'dist',
    lib: {
      entry: {
        // Main widget bundle (loaded by every host page).
        index: 'src/index.ts',
        // Vendor chunk: @huggingface/transformers + onnxruntime-web, built
        // same-origin per browser-local-ml skill (CDN dynamic imports die
        // silently on iOS Safari behind content blockers). Loaded ONLY when
        // the user explicitly picks the browser model.
        'transformers-vendor': 'src/transformers-vendor.ts',
      },
      formats: ['es'],
    },
    rollupOptions: {
      output: {
        // Standalone ESM files with stable names (no hash) — wwwroot copies
        // are committed, and local.ts resolves the vendor by relative path.
        entryFileNames: '[name].js',
        chunkFileNames: '[name].js',
      },
    },
    target: 'es2022',
    sourcemap: true,
    minify: true,
  },
});
