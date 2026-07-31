import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  retries: 0,
  use: { baseURL: 'https://localhost', ignoreHTTPSErrors: true, trace: 'retain-on-failure' }
})
