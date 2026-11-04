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

export type VariantCodePlan = { codesByKey: Record<string, string>; skipped: number; highest: number }

// Model-code codes for every row ('all') or the rows whose column is empty ('missing'), keyed by row. Each row first
// takes the number of its own position (the fifth row "MODEL-05"), but never one at or below the number the row before
// it took. A number another row of the form holds, or the catalog holds (`isTaken`, the codes the server reported),
// moves it on by one until a free one is found. Returns null when a row would need a number above `scannedUpTo`: the
// caller asks the server about the next numbers and plans again.
export function planVariantCodes<T extends { key: string; barcode: string; sku: string }>(rows: readonly T[], target: VariantCodeTarget, mode: 'all' | 'missing', modelCode: string, isTaken: (code: string) => boolean = () => false, scannedUpTo = Number.POSITIVE_INFINITY): VariantCodePlan | null {
  if (!modelCode.trim() || rows.length > 1000) return { codesByKey: {}, skipped: 0, highest: 0 }
  const key = (code: string) => code.toLocaleUpperCase('tr-TR')
  const used = new Set(mode === 'all' ? [] : rows.map(row => key(row[target].trim())).filter(Boolean))
  const codesByKey: Record<string, string> = {}
  let skipped = 0
  let last = 0
  for (const [index, row] of rows.entries()) {
    if (mode === 'missing' && row[target].trim()) continue
    let number = Math.max(index + 1, last + 1)
    while (true) {
      if (number > scannedUpTo) return null
      const code = buildSequentialVariantCode(modelCode, number)
      if (!used.has(key(code)) && !isTaken(code)) break
      if (isTaken(code)) skipped++
      number++
    }
    const code = buildSequentialVariantCode(modelCode, number)
    used.add(key(code))
    codesByKey[row.key] = code
    last = number
  }
  return { codesByKey, skipped, highest: last }
}

// The candidate codes for the numbers first..last, to ask the server about in one request.
export function variantCodeCandidates(modelCode: string, first: number, last: number) {
  return Array.from({ length: Math.max(0, last - first + 1) }, (_, index) => buildSequentialVariantCode(modelCode, first + index))
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
      ? `${barcode ? 'Barkodu' : 'Stok kodu'} eksik ${count} varyanta kod üretilecek; dolu kodlara dokunulmaz.`
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
