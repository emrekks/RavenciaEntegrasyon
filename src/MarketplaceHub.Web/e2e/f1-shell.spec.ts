import { expect, test } from '@playwright/test'

test('unauthenticated shell exposes login without application navigation', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Tekrar hoş geldiniz' })).toBeVisible()
  await expect(page.getByLabel('E-posta')).toBeVisible()
  await expect(page.getByLabel('Parola')).toBeVisible()
  await expect(page.getByText('Ürünler')).toHaveCount(0)
})

test('active shell exposes current operation and settings navigation with the live-safe dashboard', async ({ page }) => {
  await page.route('**/api/v1/auth/me', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: '00000000-0000-0000-0000-000000000001', email: 'owner@example.invalid', displayName: 'Ravencia Admin', role: 'OWNER', state: 'ACTIVE', tenantId: '00000000-0000-0000-0000-000000000002' }) }))
  await page.route('**/api/v1/connections', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [{ status: 'ACTIVE' }, { status: 'VERIFIED' }], nextCursor: null, hasMore: false }) }))
  await page.route('**/api/v1/orders', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], nextCursor: null, hasMore: false }) }))
  await page.route('**/api/v1/invoices', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [{ status: 'DRAFT' }], nextCursor: null, hasMore: false }) }))
  await page.goto('/dashboard')

  await expect(page.getByRole('heading', { name: 'Genel Bakış' })).toBeVisible()
  const navigation = page.getByRole('navigation', { name: 'Ana menü' })
  for (const label of ['Dashboard', 'Ürünler', 'Siparişler', 'İadeler', 'Faturalar', 'İşlem Takibi']) await expect(navigation.getByRole('link', { name: label, exact: true })).toBeVisible()

  const settings = page.getByRole('button', { name: 'Ayarlar' })
  await settings.click()
  for (const label of ['Platformlar', 'Eşleştirme Ayarları', 'Sistem Ayarları']) await expect(page.getByRole('link', { name: label, exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Faturalama', exact: true })).toHaveCount(0)

  await expect(page.getByText('Dış yazmalar bağlantı bazında korunur')).toBeVisible()
  await expect(page.getByText('Kontrollü entegrasyon modu')).toBeVisible()
})
