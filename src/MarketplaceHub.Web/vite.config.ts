import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => {
  const apiProxy = loadEnv(mode, '.', '').VITE_API_PROXY ?? 'https://localhost:7443'

  return {
    plugins: [react()],
    server: { host: '127.0.0.1', port: 5173, proxy: { '/api': apiProxy, '/health': apiProxy } },
  }
})
