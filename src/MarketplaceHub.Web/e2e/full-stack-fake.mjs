import { chromium } from '@playwright/test'

const ui = process.env.MARKETPLACEHUB_E2E_UI
const connectionId = process.env.MARKETPLACEHUB_E2E_CONNECTION_ID
if (!ui || !connectionId) throw new Error('Full-stack E2E runtime coordinates are missing.')

const browser = await chromium.launch({ headless: true })
try {
  const page = await browser.newPage()
  page.setDefaultTimeout(60_000)
  const csrfResponse = await page.request.get(`${ui}/api/v1/auth/csrf`)
  const csrfBody = await csrfResponse.json()
  if (csrfResponse.status() !== 200 || !csrfBody.token) {
    throw new Error(`CSRF bootstrap failed: ${csrfResponse.status()} ${JSON.stringify(csrfBody)}`)
  }
  const login = await page.request.post(`${ui}/api/v1/auth/login`, {
    headers: { 'X-CSRF-TOKEN': csrfBody.token },
    data: { email: 'owner@fake.invalid', password: 'Local-E2E-Only!9347' },
  })
  const loginBody = await login.text()
  if (login.status() !== 200) throw new Error(`Login failed: ${login.status()} ${loginBody}`)
  await page.goto(`${ui}/dashboard`, { waitUntil: 'networkidle', timeout: 60_000 })
  await page.waitForURL('**/dashboard', { timeout: 10_000 })

  const enqueueCsrfResponse = await page.request.get(`${ui}/api/v1/auth/csrf`)
  const enqueueCsrfBody = await enqueueCsrfResponse.json()
  const enqueue = await page.request.post(`${ui}/api/v1/connections/${connectionId}/order-sync-jobs`, {
    headers: { 'Idempotency-Key': 'browser-fake-order-sync', 'X-CSRF-TOKEN': enqueueCsrfBody.token },
    data: { externalOrderId: null },
  })
  const enqueueBody = await enqueue.text()
  if (enqueue.status() !== 202) throw new Error(`Order sync enqueue failed: ${enqueue.status()} ${enqueueBody}`)

  const orderId = await page.evaluate(async () => {
    for (let attempt = 0; attempt < 40; attempt++) {
      const response = await fetch('/api/v1/orders', { credentials: 'same-origin' })
      const body = await response.json()
      if (body.items?.[0]?.id) return body.items[0].id
      await new Promise(resolve => setTimeout(resolve, 250))
    }
    return null
  })
  if (!orderId) throw new Error('The visible order did not expose a local detail identity.')
  await page.goto(`${ui}/orders`)
  await page.waitForFunction(() => document.body.innerText.includes('SYNTHETIC-ORDER'))
  await page.goto(`${ui}/orders/${orderId}`)
  await page.waitForFunction(() => document.body.innerText.includes('Synthetic Product'))
  process.stdout.write('FULL_STACK_FAKE_E2E_PASS\n')
} finally {
  await browser.close()
}
