import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  retries: 0,
  webServer: { command: 'node ./node_modules/vite/bin/vite.js --host 127.0.0.1 --port 4173', url: 'http://127.0.0.1:4173', reuseExistingServer: true },
  use: { baseURL: 'http://127.0.0.1:4173', trace: 'retain-on-failure' }
})
