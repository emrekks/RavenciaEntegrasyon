type VariantGenerationDefaultsInput = {
  baseSku: string
  modelCode: string
  sequence: number
  automaticBarcodes: boolean
  fallbackSalePrice: number
  fallbackListPrice: number
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

  const suffix = `-${String(safeSequence).padStart(2, '0')}`
  const barcodePrefix = (modelCode || 'URUN')
    .trim()
    .replace(/\s+/g, '-')
    .toLocaleUpperCase('tr-TR')
    .normalize('NFKD')
    .replace(/\p{M}/gu, '')
    .replace(/[^A-Z0-9._-]/g, '')
  const prefix = (barcodePrefix || 'URUN').slice(0, Math.max(1, 40 - suffix.length))

  return { sku: '', barcode: `${prefix}${suffix}`, salePrice: 0, listPrice: 0 }
}

export function resolveVariantSyncAttributeIds(currentIds: string[], selectedIds: string[], allIds: string[]) {
  const requestedIds = selectedIds.length ? selectedIds : allIds
  return [...new Set([...currentIds, ...requestedIds])]
}
