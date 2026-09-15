import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  build: {
    rollupOptions: {
      // The local-LLM module is loaded at runtime from CDN only when WebGPU
      // is available; never bundle or download it at build time.
      external: [/@huggingface\//],
    },
  },
})
