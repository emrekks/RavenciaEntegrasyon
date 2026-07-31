import { expect, test } from '@playwright/test'

test('unauthenticated shell exposes login without application navigation', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Tekrar hoş geldiniz' })).toBeVisible()
  await expect(page.getByLabel('E-posta')).toBeVisible()
  await expect(page.getByLabel('Parola')).toBeVisible()
  await expect(page.getByText('Ürünler')).toHaveCount(0)
})

test('active F2 shell exposes only approved catalog and inventory navigation', async ({ page }) => {
  await page.route('**/api/v1/auth/me', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: '00000000-0000-0000-0000-000000000001', email: 'owner@example.invalid', displayName: 'Ravencia Admin', state: 'ACTIVE', tenantId: '00000000-0000-0000-0000-000000000002' }) }))
  await page.route('**/api/v1/products', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], nextCursor: null, hasMore: false }) }))
  await page.goto('/products')
  await expect(page.getByRole('heading', { name: 'Ürünler' })).toBeVisible()
  for (const label of ['Ürünler', 'Kategoriler', 'Markalar', 'Özellikler', 'İçe Aktarım', 'Stok']) await expect(page.getByRole('link', { name: label })).toBeVisible()
  await expect(page.getByText('Siparişler')).toHaveCount(0)
  await expect(page.getByText('Entegrasyonlar')).toHaveCount(0)
  await expect(page.getByText('Faturalar')).toHaveCount(0)
})
