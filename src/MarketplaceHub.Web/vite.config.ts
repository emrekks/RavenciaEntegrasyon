import { defineConfig } from 'vitest/config'
import { loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig(({ mode }) => {
  const apiProxy = loadEnv(mode, '.', '').VITE_API_PROXY ?? 'https://localhost:7443'

  return {
    plugins: [react(), tailwindcss()],
    server: { host: '127.0.0.1', port: 5173, proxy: { '/api': apiProxy, '/health': apiProxy } },
    test: { environment: 'jsdom', setupFiles: './src/test-setup.ts', include: ['src/**/*.test.ts', 'src/**/*.test.tsx'] },
  }
})
