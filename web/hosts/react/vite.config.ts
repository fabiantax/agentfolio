import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    rollupOptions: {
      // The local-LLM module is loaded at runtime from CDN only when WebGPU
      // is available; never bundle or download it at build time.
      external: [/@huggingface\//],
    },
  },
})
