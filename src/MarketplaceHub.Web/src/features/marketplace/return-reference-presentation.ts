export type ApprovedReturnSort = 'ORDERED_DESC' | 'ORDERED_ASC' | 'APPROVED_DESC' | 'APPROVED_ASC'

export function shouldShowReturnCountdown(actionRequired: boolean, value: string | null, now = Date.now()): boolean {
  if (!actionRequired || !value) return false
  const dueAt = new Date(value).getTime()
  return Number.isFinite(dueAt) && dueAt >= Date.UTC(2000, 0, 1) && dueAt > now
}

type ReturnSortDates = { id: string; orderedAt: string | null; approvedAt?: string | null }

function timestamp(value: string | null | undefined): number | null {
  if (!value) return null
  const parsed = Date.parse(value)
  return Number.isFinite(parsed) ? parsed : null
}

export function sortApprovedReturns<T extends ReturnSortDates>(items: readonly T[], sort: ApprovedReturnSort): T[] {
  const useApprovalDate = sort.startsWith('APPROVED')
  const descending = sort.endsWith('DESC')
  return [...items].sort((left, right) => {
    const leftDate = timestamp(useApprovalDate ? left.approvedAt : left.orderedAt)
    const rightDate = timestamp(useApprovalDate ? right.approvedAt : right.orderedAt)
    if (leftDate === null && rightDate !== null) return 1
    if (rightDate === null && leftDate !== null) return -1
    if (leftDate !== null && rightDate !== null && leftDate !== rightDate)
      return descending ? rightDate - leftDate : leftDate - rightDate
    return left.id.localeCompare(right.id)
  })
}

export function formatMarketplaceBarcode(platformCode: string | null | undefined, value: string | null | undefined): string | null {
  const barcode = value?.trim()
  if (!barcode) return null
  if (platformCode?.trim().toUpperCase() !== 'HEPSIBURADA') return barcode
  // Hepsiburada sometimes returns alphanumeric merchant codes with a zero
  // prefix. Keep numeric GTIN/EAN barcodes intact because leading zeroes are valid.
  return barcode.replace(/^0+(?=[A-Z])/iu, '')
}

const RETURN_REASON_LABELS: Record<string, string> = {
  poorquality: 'Kalitesiz buldum',
  changedmindorunlikedit: 'Ürünü almaktan vazgeçtim / beğenmedim',
  productisbroken: 'Ürünüm arızalı geldi',
  productisdamaged: 'Ürünüm hasarlı geldi',
  sizetoosmall: 'Bedeni / boyutu küçük geldi',
  sizetoolarge: 'Bedeni / boyutu büyük geldi',
  missingproduct: 'Ürünün parçası eksik',
  wrongproductorderedbycustomer: 'Yanlış sipariş verdim',
  wrongproductsentbymerchant: 'Siparişimden farklı ürün gönderildi',
  notarrivedinvoice: 'Faturam gelmedi',
  incorrectinvoice: 'Faturamda hata var',
  missinginvoice: 'Faturamı kaybettim',
  producthasexpired: 'Ürünün son kullanma tarihi geçmiş',
  productiswrongweight: 'Ürünüm farklı gramajda geldi',
  foundcheaper: 'Daha ucuzunu buldum',
  incompatiblyproduct: 'Bedeni / boyutu uymadı',
  productisbrokenordamaged: 'Bozuk / hasarlı geldi',
  wrongproduct: 'Yanlış ürün geldi',
  notarrivedwhenneeded: 'İhtiyacım olan zamanda gelmedi',
  other: 'Diğer',
}

function normalizeReason(value: string): string {
  return value.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase().replace(/[^a-z0-9]/g, '')
}

export function returnReasonPresentation(item: { reasonCode?: string | null; reasonText?: string | null }): { label: string; explanation: string | null } {
  const code = item.reasonCode?.trim() ?? ''
  const text = item.reasonText?.trim() ?? ''
  const normalizedCode = normalizeReason(code)
  const mappedLabel = RETURN_REASON_LABELS[normalizedCode]

  if (mappedLabel === RETURN_REASON_LABELS.other || (!mappedLabel && text))
    return { label: text || mappedLabel || code.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ') || 'Belirtilmedi', explanation: null }

  const label = mappedLabel || code.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ') || text || 'Belirtilmedi'
  const explanation = text && normalizeReason(text) !== normalizeReason(label) ? text : null
  return { label, explanation }
}
