// Taslak Olarak Kaydet: a card kept before it is complete. Only a new card (a copy too) or a card that is still a draft
// is saved as one: a card on sale or closed may be live on a marketplace, and a draft is left out of every send and
// stock count. The server keeps the same rule (ProductDraftPolicy).
export function canSaveAsDraft(editing: boolean, savedStatus: string | null | undefined) {
  return !editing || savedStatus?.trim().toUpperCase() === 'DRAFT'
}

export function isDraftStatus(status: string | null | undefined) {
  return status?.trim().toUpperCase() === 'DRAFT'
}

// The status a draft's form starts with: the status the normal Kaydet gives it (Satışa Açık), as the draft's own status
// is set only by Taslak Olarak Kaydet.
export function editorStatusFor(savedStatus: string | null | undefined) {
  const status = savedStatus?.trim().toUpperCase()
  return !status || status === 'DRAFT' ? 'ACTIVE' : status
}

// A draft needs a name to be found in the Taslak list: the product name, else the model code, else "Taslak Ürün" with
// the date and time it was saved (Istanbul time, as the panel shows every time).
export function draftProductTitle(title: string, modelCode: string, now: Date) {
  const named = title.trim() || modelCode.trim()
  if (named) return named
  const stamp = new Intl.DateTimeFormat('tr-TR', { timeZone: 'Europe/Istanbul', day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(now)
  return `Taslak Ürün ${stamp}`
}

function codeBase(text: string) {
  return text.trim().replace(/\s+/g, '-').toLocaleUpperCase('tr-TR').slice(0, 60) || 'TASLAK'
}

// Every sale row keeps a stock code (the server stores one per variant and keeps them unique): a row left empty in a
// draft gets the card's base code with its row number, never a code another row of the form already has.
export function draftRowSkus<T extends { sku: string }>(rows: readonly T[], base: string): T[] {
  const prefix = codeBase(base)
  const taken = new Set(rows.map(row => row.sku.trim().toLocaleUpperCase('tr-TR')).filter(Boolean))
  return rows.map((row, index) => {
    if (row.sku.trim()) return row
    let counter = index + 1
    let sku = `${prefix}-${counter}`
    while (taken.has(sku.toLocaleUpperCase('tr-TR'))) sku = `${prefix}-${++counter}`
    taken.add(sku.toLocaleUpperCase('tr-TR'))
    return { ...row, sku }
  })
}

// What still stops a draft (malformed or clashing values the server refuses too). A missing field never does: the
// checks of the normal save come back as the list of what to complete before the card goes on sale.
export function draftBlockingIssues(rows: ReadonlyArray<{ sku: string; barcode: string; optionSignature: string; salePrice: number; listPrice: number }>, prices: { listPrice: string; salePrice: string }) {
  const issues: string[] = []
  const listPrice = Number(prices.listPrice || 0); const salePrice = Number(prices.salePrice || 0)
  if (!Number.isFinite(listPrice) || !Number.isFinite(salePrice) || listPrice < 0 || salePrice < 0 || listPrice < salePrice) issues.push('Liste fiyatı satış fiyatından küçük olamaz ve fiyatlar negatif olamaz.')
  if (rows.some(row => row.salePrice < 0 || row.listPrice < row.salePrice)) issues.push('Her varyantta liste fiyatı satış fiyatından küçük olamaz.')
  const skus = rows.map(row => row.sku.trim().toLocaleUpperCase('tr-TR'))
  if (new Set(skus).size !== skus.length) issues.push('Stok kodları benzersiz olmalıdır.')
  const barcodes = rows.map(row => row.barcode.trim().toLocaleUpperCase('tr-TR')).filter(Boolean)
  if (new Set(barcodes).size !== barcodes.length) issues.push('Barkodlar benzersiz olmalıdır.')
  const signatures = rows.map(row => row.optionSignature)
  if (new Set(signatures).size !== signatures.length) issues.push('Aynı varyant kombinasyonu iki kez oluşturulamaz.')
  return issues
}

// The list of what the draft still lacks, split into its sentences, at most `shown` of them with the rest counted.
export function draftMissingSummary(issueText: string, shown = 3) {
  const issues = issueText.split(/(?<=\.)\s+/u).map(item => item.trim()).filter(Boolean)
  if (!issues.length) return { count: 0, lines: [] as string[] }
  const lines = issues.slice(0, shown)
  if (issues.length > shown) lines.push(`ve ${issues.length - shown} eksik daha`)
  return { count: issues.length, lines }
}
