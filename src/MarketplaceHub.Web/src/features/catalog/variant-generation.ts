type VariantGenerationDefaultsInput = {
  modelCode: string
  sequence: number
  automaticBarcodes: boolean
  fallbackSalePrice: number
  fallbackListPrice: number
}

// Generated codes use only A-Z, 0-9, '.', '-' and '_' (Turkish letters folded, spaces as '-'), which every
// marketplace takes as a barcode and a stock code; barcodes stay within Trendyol's and Shopify's 40 characters.
// Hepsiburada takes such a barcode too: it keeps the seller's own code, not only an EAN-13.
export const GeneratedBarcodeMaxLength = 40

export type VariantCodeTarget = 'barcode' | 'sku'

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

export function buildSequentialVariantCodes(modelCode: string, variantCount: number) {
  const count = Math.floor(variantCount)
  if (!modelCode.trim() || count < 1 || count > 1000) return []

  return Array.from({ length: count }, (_, index) => buildSequentialVariantCode(modelCode, index + 1))
}

export function applyGeneratedVariantCodes<T extends { barcode: string; sku: string }>(rows: T[], target: VariantCodeTarget, codes: string[]) {
  if (rows.length !== codes.length) return rows
  return rows.map((row, index) => ({ ...row, [target]: codes[index] }))
}

// Model-code codes for the rows whose chosen column is empty, keyed by row: a row takes the code of its own position
// ("MODEL-05" for the fifth row) when no other row holds it, otherwise the next free number after the last row.
export function buildMissingVariantCodes<T extends { key: string; barcode: string; sku: string }>(rows: readonly T[], target: VariantCodeTarget, modelCode: string): Record<string, string> {
  if (!modelCode.trim() || rows.length > 1000) return {}
  const used = new Set(rows.map(row => row[target].trim().toLocaleUpperCase('tr-TR')).filter(Boolean))
  const codes: Record<string, string> = {}
  let next = rows.length + 1
  rows.forEach((row, index) => {
    if (row[target].trim()) return
    let code = buildSequentialVariantCode(modelCode, index + 1)
    while (used.has(code.toLocaleUpperCase('tr-TR'))) code = buildSequentialVariantCode(modelCode, next++)
    used.add(code.toLocaleUpperCase('tr-TR'))
    codes[row.key] = code
  })
  return codes
}

// Writes the given codes into one column of the keyed rows; the other column and every other row stay as they are.
export function applyVariantCodesByKey<T extends { key: string; barcode: string; sku: string }>(rows: T[], target: VariantCodeTarget, codesByKey: Readonly<Record<string, string>>) {
  return rows.map(row => Object.prototype.hasOwnProperty.call(codesByKey, row.key) ? { ...row, [target]: codesByKey[row.key] } : row)
}

export type VariantCodeChange = { target: VariantCodeTarget; mode: 'all' | 'missing' | 'clear'; codesByKey: Record<string, string> }

// The confirmation dialog's words for a column-menu code change.
export function variantCodeChangeConfirmText({ target, mode, codesByKey }: VariantCodeChange) {
  const codes = Object.values(codesByKey)
  const count = codes.length.toLocaleString('tr-TR')
  const barcode = target === 'barcode'
  if (mode === 'clear') {
    return {
      title: barcode ? 'Tüm Barkodları Temizle' : 'Tüm Stok Kodlarını Temizle',
      description: `${count} varyantın ${barcode ? 'barkodu' : 'stok kodu'} boşaltılacak.`,
      range: '',
      action: barcode ? 'Barkodları Temizle' : 'Stok Kodlarını Temizle',
    }
  }
  return {
    title: barcode ? 'Barkodları Model Kodundan Üret' : 'Stok Kodlarını Model Kodundan Üret',
    description: mode === 'missing'
      ? `${barcode ? 'Barkodu' : 'Stok kodu'} boş ${count} varyanta kod üretilecek; dolu kodlara dokunulmaz.`
      : `Mevcut ${barcode ? 'barkod' : 'stok kodu'} alanı ${count} varyant için değiştirilecek.`,
    range: codes.length > 1 ? `${codes[0]} – ${codes.at(-1)}` : codes[0] ?? '',
    action: barcode ? 'Barkodları Üret' : 'Stok Kodlarını Üret',
  }
}

// New variant rows start with an empty barcode and stock code unless automatic barcodes are on (then only the barcode
// is filled): no stock code is made up for them. A code is generated only when the user picks it from a column menu.
export function buildVariantGenerationDefaults({ modelCode, sequence, automaticBarcodes, fallbackSalePrice, fallbackListPrice }: VariantGenerationDefaultsInput) {
  const safeSequence = Math.max(1, Math.floor(sequence))
  if (!automaticBarcodes) {
    return {
      sku: '',
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
