// The screens the visual check renders. Each scenario opens `path` (signed in unless `signedIn: false`), optionally
// acts on the page (`act`, e.g. opens a window), and is captured at every width in `widths`.
export const widths = [375, 768, 1280, 1920]

// Every capture sees the same clock, so relative dates ("3 gün önce") and "today" ranges do not move between runs.
export const frozenNow = new Date('2026-10-20T09:30:00+03:00')

export const scenarios = [
  { name: 'sign-in', path: '/dashboard', signedIn: false },
  { name: 'dashboard', path: '/dashboard' },
  { name: 'orders', path: '/orders' },
  { name: 'returns', path: '/returns' },
  { name: 'products', path: '/products' },
  { name: 'product-editor', path: '/products/p1' },
  { name: 'invoices', path: '/invoices' },
  { name: 'questions', path: '/questions' },
  { name: 'integrations', path: '/integrations' },
  { name: 'settings', path: '/settings' },
  { name: 'jobs', path: '/jobs' },
  // Windows and tools that only open on request.
  { name: 'label-designer', path: '/settings?tab=designer' },
  {
    name: 'product-image-zoom', path: '/products/p1', widths: [375, 1280],
    act: async page => {
      const tiles = page.locator('.media-preview-strip figure.image-preview-card.media-sortable')
      await tiles.first().waitFor()
      await tiles.nth(1).locator('img.clickable-thumb').click()
      await page.locator('.rr-modal.rr-img .rr-img-preview img').waitFor()
    },
  },
  {
    name: 'product-media-clear', path: '/products/p1', widths: [1280],
    act: async page => {
      await page.locator('.product-media-clear-all-button').click()
      await page.locator('.product-media-clear-all-button').waitFor({ state: 'detached' })
    },
  },
  {
    name: 'product-platform-update', path: '/products/p1', widths: [375, 1280],
    act: async page => {
      await page.locator('.platform-update-settings').click()
      await page.locator('.platform-update-modal .publish-selection-footer').waitFor()
    },
  },
  {
    name: 'orders-shopify-invoice-status', path: '/orders', widths: [375, 1280],
    act: async page => {
      await page.locator('tr', { hasText: '#1048' }).locator('.rr-inv-trigger').click()
      // A real click would scroll the menu item into view, and the menu closes when the page scrolls.
      await page.getByRole('menuitem', { name: 'Fatura Durumunu Değiştir' }).dispatchEvent('click')
      await page.getByRole('dialog').getByLabel('Yeni Fatura Durumu').waitFor()
    },
  },
  {
    name: 'invoices-shopify-invoice-status', path: '/invoices', widths: [375, 1280],
    act: async page => {
      await page.locator('article.invoice-reference-row').filter({ hasText: '#RV-1048' }).getByRole('button', { name: 'Detayları Aç' }).click()
      await page.locator('.invoice-detail-modal').getByRole('button', { name: 'Fatura Durumunu Değiştir' }).click()
      await page.locator('.shopify-invoice-status-modal').waitFor()
    },
  },
]
