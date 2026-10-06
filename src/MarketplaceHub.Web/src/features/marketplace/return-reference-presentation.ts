export type ApprovedReturnSort = 'ORDERED_DESC' | 'ORDERED_ASC' | 'APPROVED_DESC' | 'APPROVED_ASC'

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
