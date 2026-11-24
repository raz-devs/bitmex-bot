import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// `npm run dev` proxies the API to the .NET server on :5000; `npm run build` writes into its wwwroot.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': 'http://localhost:5000',
      '/hub': { target: 'http://localhost:5000', ws: true },
    },
  },
  build: { outDir: '../src/BitmexBot.Web/wwwroot', emptyOutDir: true },
})
