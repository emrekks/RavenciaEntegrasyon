import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => {
  const apiProxy = loadEnv(mode, '.', '').VITE_API_PROXY ?? 'http://localhost:5192'

  return {
    plugins: [react()],
    build: {
      rollupOptions: {
        output: {
          // Keep the application entry focused on the shell while allowing
          // browser-cached dependencies to be downloaded as stable vendor
          // chunks. The bundle budget intentionally measures the entry chunk.
          manualChunks(id) {
            if (id.includes('/node_modules/react/') || id.includes('/node_modules/react-dom/') || id.includes('/node_modules/react-router/')) return 'vendor-react'
            if (id.includes('/node_modules/@tanstack/react-query/')) return 'vendor-query'
            if (id.includes('/node_modules/@microsoft/signalr/') || id.includes('/node_modules/dompurify/')) return 'vendor-integration'
            return undefined
          },
        },
      },
    },
    server: { host: '127.0.0.1', port: 5173, proxy: { '/api': apiProxy, '/health': apiProxy, '/hubs': { target: apiProxy, ws: true } } },
  }
})
