type VariantGenerationDefaultsInput = {
  baseSku: string
  modelCode: string
  sequence: number
  automaticBarcodes: boolean
  fallbackSalePrice: number
  fallbackListPrice: number
}

// Generated codes use only A-Z, 0-9, '.', '-' and '_' (Turkish letters folded, spaces as '-'), which every
// marketplace takes as a barcode and a stock code; barcodes stay within Trendyol's and Shopify's 40 characters and
// stock codes within the 100 the panel and most marketplaces allow. A barcode built from the model code can never
// be the EAN-13 Hepsiburada requires; the editor's platform rules show that.
export const GeneratedBarcodeMaxLength = 40
export const GeneratedStockCodeMaxLength = 100

function codeText(value: string) {
  return value
    .trim()
    .replace(/\s+/g, '-')
    .toLocaleUpperCase('tr-TR')
    .normalize('NFKD')
    .replace(/\p{M}/gu, '')
    .replace(/[^A-Z0-9._-]/g, '')
    .replace(/-{2,}/g, '-')
}

function codeWithSuffix(base: string, suffix: string, maximum: number) {
  return `${(codeText(base) || 'URUN').slice(0, Math.max(1, maximum - suffix.length))}${suffix}`
}

export function buildSequentialVariantCode(modelCode: string, sequence: number): string {
  const safeSequence = Math.max(1, Math.floor(sequence))
  return codeWithSuffix(modelCode || 'URUN', `-${String(safeSequence).padStart(2, '0')}`, GeneratedBarcodeMaxLength)
}

export function buildSequentialVariantIdentifiers(modelCode: string, variantCount: number) {
  return buildSequentialVariantCodes(modelCode, variantCount).map(code => ({ barcode: code, sku: code }))
}

export function buildSequentialVariantCodes(modelCode: string, variantCount: number) {
  const count = Math.floor(variantCount)
  if (!modelCode.trim() || count < 1 || count > 1000) return []

  return Array.from({ length: count }, (_, index) => buildSequentialVariantCode(modelCode, index + 1))
}

export function applyGeneratedVariantCodes<T extends { barcode: string; sku: string }>(rows: T[], target: 'barcode' | 'sku', codes: string[]) {
  if (rows.length !== codes.length) return rows
  return rows.map((row, index) => ({ ...row, [target]: codes[index] }))
}

export function buildVariantGenerationDefaults({ baseSku, modelCode, sequence, automaticBarcodes, fallbackSalePrice, fallbackListPrice }: VariantGenerationDefaultsInput) {
  const safeSequence = Math.max(1, Math.floor(sequence))
  if (!automaticBarcodes) {
    return {
      sku: codeWithSuffix(baseSku || 'URUN', `-${safeSequence}`, GeneratedStockCodeMaxLength),
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
