// "İçe Aktar" of a category / attribute mapping backup (A-7, scan 25 Nov 2026). It used to send every mapping as a bare
// PUT: the server refuses one over an existing mapping without If-Match (428/409), the first refusal stopped the whole
// import half way, the values it had just added were never mapped (their new ids were read from the old list), and the
// result was green. Now each mapping is read first: an identical one is skipped, the same pair on an older list is
// written at its version (If-Match), a different one already there is listed as a conflict and left as it is (an import
// never replaces a mapping the panel holds). Every refusal is collected and the import goes on.
import type { ApiAnswer } from '../../../shared/api/api-answer'
import { hubApi } from '../../../shared/api'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { isOptionAttribute, type CatalogMapping, type LocalAttribute, type LocalCategory, type ReferenceData } from './mapping-pages/mapping-shared'

export type MappingTransferBundle = { format: 'RAVENCIA_MAPPING_BUNDLE'; version: 1; exportedAt: string; categories: LocalCategory[]; attributes: LocalAttribute[]; categoryMappings: CatalogMapping[]; attributeMappings: CatalogMapping[]; attributeValueMappings: CatalogMapping[] }
export type MappingTransferScope = 'categories' | 'options' | 'attributes' | 'mappings'
export type MappingImportResult = { written: number; skipped: number; conflicts: string[]; failures: string[] }

type Existing = Pick<CatalogMapping, 'localId' | 'externalId' | 'snapshotId' | 'status' | 'version'>
// What the import does with one mapping, given the one the panel already holds for that panel item there.
export function mappingImportStep(current: Existing | undefined, externalId: string, snapshotId: string): { write: true; ifMatch: string | null } | { write: false; reason: 'same' | 'conflict' } {
  if (!current) return { write: true, ifMatch: null }
  if (current.externalId !== externalId) return { write: false, reason: 'conflict' }
  if (current.snapshotId === snapshotId && current.status === 'VERIFIED') return { write: false, reason: 'same' }
  return { write: true, ifMatch: `"v${current.version}"` }
}

const failureText = (reason: unknown, fallback: string) => reason instanceof Error ? reason.message : fallback
const lower = (value: string) => value.toLocaleLowerCase('tr-TR')

export async function importMappingBundle(input: { bundle: MappingTransferBundle; selection: Record<MappingTransferScope, boolean>; connectionId: string; referenceSnapshotId: string | null; categories: readonly LocalCategory[]; attributes: readonly LocalAttribute[] }): Promise<MappingImportResult> {
  const { bundle, selection, connectionId } = input
  const result: MappingImportResult = { written: 0, skipped: 0, conflicts: [], failures: [] }
  const categoryIdMap = new Map<string, string>()
  const attributeIdMap = new Map<string, string>()
  const valueIdMap = new Map<string, string>()
  const targetCategories = [...input.categories]
  const targetAttributes = [...input.attributes]
  if (selection.categories) {
    for (const source of [...bundle.categories].sort((left, right) => left.depth - right.depth)) {
      try {
        const parentPath = source.path.split(' / ').slice(0, -1).join(' / ')
        const existing = targetCategories.find(item => item.id === source.id || item.path === source.path || (item.depth === source.depth && item.name === source.name && !parentPath))
        const target = existing ?? await hubApi<ApiAnswer<'postCatalogCategories'>>('/catalog/categories', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ name: source.name, parentId: parentPath ? targetCategories.find(item => item.path === parentPath)?.id ?? null : null }) })
        categoryIdMap.set(source.id, target.id); if (!targetCategories.some(item => item.id === target.id)) targetCategories.push(target)
      } catch (reason) { result.failures.push(`${source.name}: ${failureText(reason, 'Kategori oluşturulamadı.')}`) }
    }
  }
  const importAttributes = bundle.attributes.filter(attribute => selection.options && isOptionAttribute(attribute) || selection.attributes && !isOptionAttribute(attribute))
  for (const source of importAttributes) {
    try {
      const existing = targetAttributes.find(item => item.id === source.id || lower(item.code) === lower(source.code) || lower(item.name) === lower(source.name))
      let target = existing ?? await hubApi<ApiAnswer<'postCatalogAttributes'>>('/catalog/attributes', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify({ code: source.code, name: source.name, dataType: source.dataType, selectionMode: source.values.length ? 'SINGLE' : null, unit: null, values: source.values.map((value, index) => ({ value: value.value, sortOrder: index })) }) })
      attributeIdMap.set(source.id, target.id)
      const missingValues = source.values.filter(value => !target.values.some(existingValue => lower(existingValue.value) === lower(value.value)))
      // The answer holds the attribute with its new values: their ids are what the value mappings are written to.
      if (missingValues.length) target = await hubApi<ApiAnswer<'postCatalogAttributesByIdValues'>>(`/catalog/attributes/${target.id}/values`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: JSON.stringify(missingValues.map((value, index) => ({ value: value.value, sortOrder: target.values.length + index }))) })
      const index = targetAttributes.findIndex(item => item.id === target.id)
      if (index >= 0) targetAttributes[index] = target; else targetAttributes.push(target)
      for (const sourceValue of source.values) { const targetValue = target.values.find(value => lower(value.value) === lower(sourceValue.value)); if (targetValue) valueIdMap.set(sourceValue.id, targetValue.id) }
    } catch (reason) { result.failures.push(`${source.name}: ${failureText(reason, 'Özellik hazırlanamadı.')}`) }
  }
  if (!selection.mappings) return result

  // Each mapping: read what the panel holds there (once per list), then skip, write at its version, or name the conflict.
  async function writeMapping(name: string, current: Existing | undefined, externalId: string, snapshotId: string, put: (ifMatch: string | null) => Promise<unknown>) {
    const step = mappingImportStep(current, externalId, snapshotId)
    if (!step.write) { if (step.reason === 'same') result.skipped++; else result.conflicts.push(name); return }
    try { await put(step.ifMatch); result.written++ } catch (reason) { result.failures.push(`${name}: ${failureText(reason, 'Eşleme kaydedilemedi.')}`) }
  }
  const lists = new Map<string, Promise<CatalogMapping[]>>()
  const listOf = (path: string) => { let list = lists.get(path); if (!list) { list = hubApi<CatalogMapping[]>(path).then(items => items ?? []); lists.set(path, list) } return list }
  const snapshots = new Map<string, Promise<ReferenceData>>()
  const snapshotOf = (path: string) => { let snapshot = snapshots.get(path); if (!snapshot) { snapshot = hubApi<ReferenceData>(path); snapshots.set(path, snapshot) } return snapshot }
  const headers = (ifMatch: string | null): Record<string, string> => ifMatch ? { 'If-Match': ifMatch } : {}
  const categoryName = (id: string) => bundle.categories.find(item => item.id === id)?.name ?? 'Kategori'
  const attributeName = (id: string) => bundle.attributes.find(item => item.id === id)?.name ?? 'Özellik'
  const valueName = (id: string) => { for (const attribute of bundle.attributes) { const value = attribute.values.find(item => item.id === id); if (value) return `${attribute.name}: ${value.value}` } return 'Değer' }

  if (bundle.categoryMappings.length) {
    const snapshotId = input.referenceSnapshotId
    if (!snapshotId) result.failures.push('Kategori eşlemeleri yazılmadı: kategori referans verisi hazır değil; önce kategori eşitlemesini tamamlayın.')
    else {
      try {
        const existing = await listOf(`/mappings/categories?connectionId=${encodeURIComponent(connectionId)}`)
        for (const item of bundle.categoryMappings) {
          const targetLocalId = categoryIdMap.get(item.localId) ?? targetCategories.find(category => category.id === item.localId)?.id
          if (!targetLocalId) { result.skipped++; continue }
          await writeMapping(categoryName(item.localId), existing.find(mapping => mapping.localId === targetLocalId), item.externalId, snapshotId, ifMatch =>
            hubApi(`/mappings/categories/${targetLocalId}`, { method: 'PUT', headers: headers(ifMatch), body: JSON.stringify({ connectionId, snapshotId, externalId: item.externalId, status: 'VERIFIED' }) }))
        }
      } catch (reason) { result.failures.push(`Kategori eşlemeleri okunamadı: ${failureText(reason, 'liste alınamadı.')}`) }
    }
  }
  for (const item of bundle.attributeMappings) {
    const scope = item.scopeExternalId
    const name = attributeName(item.localId)
    try {
      const snapshot = await snapshotOf(`/reference-data/categories/${encodeURIComponent(scope)}/attributes?connectionId=${encodeURIComponent(connectionId)}`)
      const targetLocalId = attributeIdMap.get(item.localId) ?? targetAttributes.find(attribute => attribute.id === item.localId)?.id
      if (!targetLocalId || !snapshot.items.some(reference => reference.externalId === item.externalId)) { result.skipped++; continue }
      const existing = await listOf(`/mappings/attributes?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(scope)}`)
      const role = isOptionAttribute(bundle.attributes.find(attribute => attribute.id === item.localId) ?? { code: '', name: '', roles: [] }) ? 'OPTION' : 'ATTRIBUTE'
      await writeMapping(name, existing.find(mapping => mapping.localId === targetLocalId), item.externalId, snapshot.snapshotId, ifMatch =>
        hubApi(`/mappings/attributes/${targetLocalId}`, { method: 'PUT', headers: headers(ifMatch), body: JSON.stringify({ connectionId, snapshotId: snapshot.snapshotId, scopeExternalId: scope, externalId: item.externalId, status: 'VERIFIED', role }) }))
    } catch (reason) { result.failures.push(`${name}: ${failureText(reason, 'Eşleme kaydedilemedi.')}`) }
  }
  for (const item of bundle.attributeValueMappings) {
    const [categoryScope, remoteAttributeId] = item.scopeExternalId.split('/')
    const name = valueName(item.localId)
    try {
      const targetLocalId = valueIdMap.get(item.localId)
      if (!targetLocalId) { result.skipped++; continue }
      const snapshot = await snapshotOf(`/reference-data/categories/${encodeURIComponent(categoryScope)}/attributes/${encodeURIComponent(remoteAttributeId)}/values?connectionId=${encodeURIComponent(connectionId)}`)
      if (!snapshot.items.some(reference => reference.externalId === item.externalId)) { result.skipped++; continue }
      const existing = (await listOf(`/mappings/attribute-values?connectionId=${encodeURIComponent(connectionId)}&scopeExternalId=${encodeURIComponent(item.scopeExternalId)}`)).filter(mapping => mapping.localId === targetLocalId)
      await writeMapping(name, existing.find(mapping => mapping.externalId === item.externalId) ?? existing[0], item.externalId, snapshot.snapshotId, ifMatch =>
        hubApi(`/mappings/attribute-values/${targetLocalId}`, { method: 'PUT', headers: headers(ifMatch), body: JSON.stringify({ connectionId, snapshotId: snapshot.snapshotId, scopeExternalId: item.scopeExternalId, externalId: item.externalId, status: 'VERIFIED' }) }))
    } catch (reason) { result.failures.push(`${name}: ${failureText(reason, 'Eşleme kaydedilemedi.')}`) }
  }
  return result
}

const listed = (names: readonly string[]) => names.slice(0, 3).map(name => name.replace(/\.+$/u, '')).join('; ') + (names.length > 3 ? ` ve ${names.length - 3} tane daha` : '')

// "x yazıldı, y atlandı, z çakışma", then the conflicts and refusals by name; red when nothing was written and something
// failed, amber when only part went, green otherwise.
export function mappingImportSummary(result: MappingImportResult): { message: string; kind: 'success' | 'warning' | 'error' } {
  const message = [
    `${result.written} eşleme yazıldı, ${result.skipped} atlandı, ${result.conflicts.length} çakışma.`,
    result.conflicts.length ? `Panelde farklı eşlemesi olduğu için değiştirilmedi: ${listed(result.conflicts)}.` : '',
    result.failures.length ? `${result.failures.length} kayıt yazılamadı: ${listed(result.failures)}.` : ''
  ].filter(Boolean).join(' ')
  const kind = result.failures.length && !result.written ? 'error' : result.failures.length || result.conflicts.length ? 'warning' : 'success'
  return { message, kind }
}
