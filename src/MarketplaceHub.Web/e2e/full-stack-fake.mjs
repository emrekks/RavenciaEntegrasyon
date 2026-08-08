import { chromium } from '@playwright/test'

const ui = process.env.MARKETPLACEHUB_E2E_UI
const connectionId = process.env.MARKETPLACEHUB_E2E_CONNECTION_ID
if (!ui || !connectionId) throw new Error('Full-stack E2E runtime coordinates are missing.')

const browser = await chromium.launch({ headless: true })
try {
  const page = await browser.newPage()
  page.setDefaultTimeout(60_000)
  await page.goto(ui, { waitUntil: 'networkidle', timeout: 60_000 })
  await page.locator('input[name="email"]').fill('owner@fake.invalid')
  await page.locator('input[name="password"]').fill('Local-E2E-Only!9347')
  const loginResponse = page.waitForResponse(response => response.url().includes('/api/v1/auth/login'))
  await page.locator('button[type="submit"]').click()
  const login = await loginResponse
  const loginBody = await login.text()
  if (login.status() !== 200) throw new Error(`Login failed: ${login.status()} ${loginBody}`)
  await page.waitForURL('**/dashboard', { timeout: 10_000 }).catch(async () => {
    const alert = await page.locator('[role="alert"]').textContent().catch(() => null)
    throw new Error(`Login did not open the dashboard. url=${page.url()} alert=${alert ?? 'none'}`)
  })

  const enqueue = await page.evaluate(async id => {
    const csrfResponse = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin' })
    const { token } = await csrfResponse.json()
    const response = await fetch(`/api/v1/connections/${id}/order-sync-jobs`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': 'browser-fake-order-sync', 'X-CSRF-TOKEN': token },
      body: JSON.stringify({ externalOrderId: null }),
    })
    return { status: response.status, body: await response.text() }
  }, connectionId)
  if (enqueue.status !== 202) throw new Error(`Order sync enqueue failed: ${enqueue.status} ${enqueue.body}`)

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
