import { expect, test } from '@playwright/test'

test('category-scoped attribute and value mappings complete in one workspace', async ({ page }) => {
  let attributeSaved = false
  let valueSaved = false
  let attributeBody: unknown
  let valueBody: unknown

  await page.route('**/api/v1/**', async route => {
    const request = route.request()
    const url = new URL(request.url())
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

    if (url.pathname === '/api/v1/auth/me') return json({ id: '00000000-0000-0000-0000-000000000001', email: 'owner@example.invalid', displayName: 'Ravencia Admin', role: 'OWNER', state: 'ACTIVE', tenantId: '00000000-0000-0000-0000-000000000002' })
    if (url.pathname === '/api/v1/auth/csrf') return json({ token: 'csrf-e2e-mapping' })
    if (url.pathname === '/api/v1/connections') return json({ items: [{ id: 'connection-1', platformCode: 'TRENDYOL', displayName: 'Trendyol Stage', externalStoreId: 'seller-1', status: 'ACTIVE' }], nextCursor: null, hasMore: false })
    if (url.pathname === '/api/v1/catalog/categories') return json({ items: [{ id: 'local-category-1', path: 'Giyim / Elbise', isLeaf: true, isActive: true }], nextCursor: null, hasMore: false })
    if (url.pathname === '/api/v1/catalog/attributes') return json({ items: [{ id: 'local-attribute-1', code: 'SIZE', name: 'Beden', dataType: 'SINGLE_SELECT', isActive: true, values: [{ id: 'local-value-1', value: 'M', isActive: true }] }], nextCursor: null, hasMore: false })
    if (url.pathname === '/api/v1/mappings/categories/local-category-1') return json({ id: 'category-mapping-1', connectionId: 'connection-1', snapshotId: 'category-snapshot-1', localId: 'local-category-1', scopeExternalId: '', externalId: '14609', status: 'VERIFIED', version: 1 })
    if (url.pathname === '/api/v1/reference-data/categories/14609/attributes') return json({ snapshotId: 'attribute-snapshot-1', resourceType: 'CATEGORY_ATTRIBUTES', fetchedAt: '2026-08-05T00:00:00Z', items: [{ externalId: '293', parentExternalId: '14609', name: 'Beden', path: 'Beden', depth: 0, isLeaf: true, isActive: true, isRequired: true, allowsCustomValue: false, allowsMultipleValues: false }] })
    if (url.pathname === '/api/v1/mappings/attributes/local-attribute-1') {
      if (request.method() === 'PUT') {
        attributeBody = request.postDataJSON()
        attributeSaved = true
      }
      return json(attributeSaved ? { id: 'attribute-mapping-1', connectionId: 'connection-1', snapshotId: 'attribute-snapshot-1', localId: 'local-attribute-1', scopeExternalId: '14609', externalId: '293', status: 'VERIFIED', version: 1 } : null)
    }
    if (url.pathname === '/api/v1/reference-data/categories/14609/attributes/293/values') return json({ snapshotId: 'value-snapshot-1', resourceType: 'ATTRIBUTE_VALUES', fetchedAt: '2026-08-05T00:00:00Z', items: [{ externalId: 'value-2', parentExternalId: '293', name: 'M', path: 'M', depth: 0, isLeaf: true, isActive: true }] })
    if (url.pathname === '/api/v1/mappings/attribute-values') return json([])
    if (url.pathname === '/api/v1/mappings/attribute-values/local-value-1') {
      if (request.method() === 'PUT') {
        valueBody = request.postDataJSON()
        valueSaved = true
      }
      return json(valueSaved ? { id: 'value-mapping-1', connectionId: 'connection-1', snapshotId: 'value-snapshot-1', localId: 'local-value-1', scopeExternalId: '14609/293', externalId: 'value-2', status: 'VERIFIED', version: 1 } : null)
    }

    return json({ title: `Unexpected test request: ${url.pathname}` }, 404)
  })

  await page.goto('/mappings/attributes')
  await expect(page.getByRole('heading', { name: 'Özellik eşlemeleri', level: 1 })).toBeVisible()

  await page.getByLabel('Özellik için aktif Trendyol bağlantısı').selectOption('connection-1')
  await page.getByLabel('Özellik kapsamı panel kategorisi').selectOption('local-category-1')
  await page.getByLabel('Panel özelliği').selectOption('local-attribute-1')
  await page.getByLabel('Trendyol kategori özelliği').selectOption('293')
  await page.getByRole('button', { name: 'Eşlemeyi doğrula ve kaydet' }).click()

  await expect(page.getByText('Özellik eşlemesi doğrulandı ve kategori kapsamında kaydedildi.')).toBeVisible()
  expect(attributeBody).toEqual({ connectionId: 'connection-1', snapshotId: 'attribute-snapshot-1', externalId: '293', status: 'VERIFIED' })

  await expect(page.getByRole('heading', { name: 'Değer eşleştirmeleri' })).toBeVisible()
  await page.getByLabel('M Trendyol değeri').selectOption('value-2')
  await page.getByRole('button', { name: 'Tüm eşlemeleri kaydet' }).click()

  await expect(page.getByText('1 değer eşlemesi kaydedildi.')).toBeVisible()
  expect(valueBody).toEqual({ connectionId: 'connection-1', snapshotId: 'value-snapshot-1', externalId: 'value-2', status: 'VERIFIED' })
})
