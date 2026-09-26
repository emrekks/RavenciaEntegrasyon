type VariantGenerationDefaultsInput = {
  baseSku: string
  modelCode: string
  sequence: number
  automaticBarcodes: boolean
  fallbackSalePrice: number
  fallbackListPrice: number
}

export function buildSequentialVariantCode(modelCode: string, sequence: number): string {
  const safeSequence = Math.max(1, Math.floor(sequence))
  const suffix = `-${String(safeSequence).padStart(2, '0')}`
  const barcodePrefix = (modelCode || 'URUN')
    .trim()
    .replace(/\s+/g, '-')
    .toLocaleUpperCase('tr-TR')
    .normalize('NFKD')
    .replace(/\p{M}/gu, '')
    .replace(/[^A-Z0-9._-]/g, '')
  const prefix = (barcodePrefix || 'URUN').slice(0, Math.max(1, 40 - suffix.length))

  return `${prefix}${suffix}`
}

export function buildSequentialVariantIdentifiers(modelCode: string, variantCount: number) {
  const count = Math.floor(variantCount)
  if (!modelCode.trim() || count < 1 || count > 1000) return []

  return Array.from({ length: count }, (_, index) => {
    const code = buildSequentialVariantCode(modelCode, index + 1)
    return { barcode: code, sku: code }
  })
}

export function buildVariantGenerationDefaults({ baseSku, modelCode, sequence, automaticBarcodes, fallbackSalePrice, fallbackListPrice }: VariantGenerationDefaultsInput) {
  const safeSequence = Math.max(1, Math.floor(sequence))
  const skuPrefix = (baseSku || 'URUN').trim().replace(/\s+/g, '-').toLocaleUpperCase('tr-TR')
  if (!automaticBarcodes) {
    return {
      sku: `${skuPrefix}-${safeSequence}`,
      barcode: '',
      salePrice: fallbackSalePrice,
      listPrice: fallbackListPrice || fallbackSalePrice,
    }
  }

  return { sku: '', barcode: buildSequentialVariantCode(modelCode, safeSequence), salePrice: 0, listPrice: 0 }
}

export function resolveVariantSyncAttributeIds(currentIds: string[], selectedIds: string[], allIds: string[]) {
  const requestedIds = selectedIds.length ? selectedIds : allIds
  return [...new Set([...currentIds, ...requestedIds])]
}
