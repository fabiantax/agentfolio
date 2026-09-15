import { defineConfig } from 'vite';

export default defineConfig({
  root: 'src',
  build: {
    outDir: '../dist',
    emptyOutDir: true,
    rollupOptions: {
      // Local-LLM module is loaded at runtime from CDN only when WebGPU exists.
      external: [/@huggingface\//],
    },
  },
});
