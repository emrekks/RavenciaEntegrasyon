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
  // The products list's quick edit: a price window's open colour group is a table of the panel price and every
  // marketplace's price (each with its "Özel Fiyat" box); a stock window sets its groups in two columns.
  {
    name: 'products-quick-price', path: '/products', widths: [375, 1280],
    act: async page => {
      await page.locator('.rr-qbtn[title="Fiyatı hızlı düzenle"]').first().click()
      await page.locator('.rr-modal.rr-qe-modal .rr-vg-t').first().click()
      await page.locator('.rr-modal.rr-qe-modal .rr-qtable tbody tr').first().waitFor()
    },
  },
  {
    name: 'products-quick-stock', path: '/products', widths: [375, 1280],
    act: async page => {
      await page.locator('.rr-qbtn[title="Stoğu hızlı düzenle"]').first().click()
      await page.locator('.rr-modal.rr-qe-modal .rr-vg-t').first().click()
      await page.locator('.rr-modal.rr-qe-modal .rr-qtable tbody tr').first().waitFor()
    },
  },
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
  // The same window in the light theme, scrolled to the end: the cards end above the footer bar, never under it.
  {
    name: 'product-platform-update-light', path: '/products/p1', widths: [375, 1280],
    act: async page => {
      await page.locator('.rv-topbar-theme').click()
      await page.locator('html[data-theme="light"]').waitFor()
      await page.locator('.platform-update-settings').click()
      await page.locator('.platform-update-modal > footer.publish-selection-footer').waitFor()
      await page.locator('.platform-update-modal .product-publish-popup-body').evaluate(body => { body.scrollTop = body.scrollHeight })
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
  // Promosyon Kârlılık Analizi: the commission tariff with its filter card (dark and light), Toplu Seçim open, and Plus.
  { name: 'promotions', path: '/promotions', widths: [375, 1280] },
  {
    name: 'promotions-light', path: '/promotions', widths: [375, 1280],
    act: async page => {
      await page.locator('.commission-filters').waitFor()
      await page.locator('.rv-topbar-theme').click()
      await page.locator('html[data-theme="light"]').waitFor()
    },
  },
  {
    name: 'promotions-bulk', path: '/promotions', widths: [375, 1280],
    act: async page => {
      await page.locator('.commission-bulk-trigger').click()
      await page.locator('.commission-bulk-popover select').first().selectOption('1')
      await page.locator('.commission-bulk-popover').waitFor()
    },
  },
  { name: 'promotions-plus', path: '/promotions?tab=plus', widths: [375, 1280] },
]
