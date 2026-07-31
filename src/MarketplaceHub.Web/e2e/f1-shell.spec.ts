import { expect, test } from '@playwright/test'

test('unauthenticated F1 shell exposes login without future-phase navigation', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Tekrar hoş geldiniz' })).toBeVisible()
  await expect(page.getByLabel('E-posta')).toBeVisible()
  await expect(page.getByLabel('Parola')).toBeVisible()
  await expect(page.getByText('Ürünler')).toHaveCount(0)
  await expect(page.getByText('Siparişler')).toHaveCount(0)
  await expect(page.getByText('Entegrasyonlar')).toHaveCount(0)
})
