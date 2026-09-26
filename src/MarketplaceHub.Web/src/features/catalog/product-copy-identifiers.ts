type ProductIdentifier = { sku: string; barcode: string | null }

function normalizeIdentifier(value: string | null | undefined) {
  return value?.trim().toLocaleUpperCase('tr-TR') ?? ''
}

export function productCopyIdentifierConflicts(source: readonly ProductIdentifier[], draft: readonly ProductIdentifier[]) {
  const sourceSkus = new Set(source.map(item => normalizeIdentifier(item.sku)).filter(Boolean))
  const sourceBarcodes = new Set(source.map(item => normalizeIdentifier(item.barcode)).filter(Boolean))

  return {
    skus: draft.some(item => sourceSkus.has(normalizeIdentifier(item.sku))),
    barcodes: draft.some(item => sourceBarcodes.has(normalizeIdentifier(item.barcode)))
  }
}
