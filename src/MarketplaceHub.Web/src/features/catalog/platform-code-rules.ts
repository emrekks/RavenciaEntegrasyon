// The barcode and stock code each marketplace accepts for a variant, as the panel's publication composers
// enforce them (ProductPublicationComposer and the per-platform composers). A product meant for every connection
// is checked against all of them while it is edited, so one barcode and one stock code fit everywhere.
export type PlatformCodeField = 'barcode' | 'sku'
export type PlatformCodeIssue = { platformCode: string; field: PlatformCodeField; message: string }

export const platformCodeNames: Record<string, string> = {
  TRENDYOL: 'Trendyol',
  HEPSIBURADA: 'Hepsiburada',
  N11: 'N11',
  PAZARAMA: 'Pazarama',
  PTTAVM: 'PTT AVM',
  SHOPIFY: 'Shopify'
}

const simpleBarcodePattern = /^[A-Za-z0-9._-]+$/

// EAN-13 with its check digit, as Hepsiburada requires.
export function isValidEan13(value: string) {
  if (!/^\d{13}$/.test(value)) return false
  const sum = [...value.slice(0, 12)].reduce((total, digit, index) => total + Number(digit) * (index % 2 === 0 ? 1 : 3), 0)
  return (10 - sum % 10) % 10 === Number(value[12])
}

function barcodeIssue(platformCode: string, barcode: string): string | null {
  switch (platformCode) {
    case 'TRENDYOL':
      if (!barcode) return 'barkod zorunlu'
      return barcode.length > 40 || !simpleBarcodePattern.test(barcode) ? 'barkod en fazla 40 karakter; yalnız harf, rakam, nokta, tire, alt çizgi' : null
    case 'HEPSIBURADA':
      if (!barcode) return 'barkod zorunlu (EAN-13)'
      return isValidEan13(barcode) ? null : 'barkod geçerli 13 haneli EAN-13 olmalı'
    case 'PAZARAMA':
      if (!barcode) return 'barkod zorunlu'
      return barcode.length > 500 ? 'barkod en fazla 500 karakter' : null
    case 'PTTAVM':
      if (!barcode) return 'barkod zorunlu'
      return barcode.length > 250 ? 'barkod en fazla 250 karakter' : null
    case 'SHOPIFY':
      return barcode && (barcode.length > 40 || !simpleBarcodePattern.test(barcode)) ? 'barkod en fazla 40 karakter; yalnız harf, rakam, nokta, tire, alt çizgi' : null
    default:
      return null
  }
}

function skuIssue(platformCode: string, sku: string): string | null {
  const maximum = platformCode === 'N11' ? 255 : 100
  if (!(platformCode in platformCodeNames)) return null
  if (!sku) return 'stok kodu zorunlu'
  return sku.length > maximum ? `stok kodu en fazla ${maximum} karakter` : null
}

// The rules a variant's barcode and stock code break, one entry per platform and field.
export function platformCodeIssues(platformCodes: readonly string[], barcode: string, sku: string): PlatformCodeIssue[] {
  const trimmedBarcode = barcode.trim()
  const trimmedSku = sku.trim()
  const issues: PlatformCodeIssue[] = []
  for (const platformCode of new Set(platformCodes)) {
    const barcodeMessage = barcodeIssue(platformCode, trimmedBarcode)
    if (barcodeMessage) issues.push({ platformCode, field: 'barcode', message: barcodeMessage })
    const skuMessage = skuIssue(platformCode, trimmedSku)
    if (skuMessage) issues.push({ platformCode, field: 'sku', message: skuMessage })
  }
  return issues
}

// "Hepsiburada: barkod geçerli 13 haneli EAN-13 olmalı · Trendyol: …" for a field's issues.
export function describePlatformCodeIssues(issues: readonly PlatformCodeIssue[], field: PlatformCodeField) {
  return issues.filter(issue => issue.field === field).map(issue => `${platformCodeNames[issue.platformCode] ?? issue.platformCode}: ${issue.message}`).join(' · ')
}
