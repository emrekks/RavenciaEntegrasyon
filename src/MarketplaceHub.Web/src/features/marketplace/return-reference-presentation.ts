// The approved tab's order; the server sorts (GET /returns?sort=…).
export type ApprovedReturnSort = 'ORDERED_DESC' | 'ORDERED_ASC' | 'APPROVED_DESC' | 'APPROVED_ASC'

export function shouldShowReturnCountdown(actionRequired: boolean, value: string | null, now = Date.now()): boolean {
  if (!actionRequired || !value) return false
  const dueAt = new Date(value).getTime()
  return Number.isFinite(dueAt) && dueAt >= Date.UTC(2000, 0, 1) && dueAt > now
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
  // Trendyol's claim reason codes, named as Trendyol names them (customerClaimItemReason.name). Its claim carries one
  // text: the customer's note when there is one, otherwise this name, so the code is what names the reason.
  bigsize: 'Bedeni/Ebatı Büyük Geldi',
  smallsize: 'Bedeni/Ebatı Küçük Geldi',
  dislike: 'Beğenmedim',
  abandon: 'Vazgeçtim',
  wrongitem: 'Yanlış ürün gönderildi',
  wrongorder: 'Yanlış sipariş verdim',
  damageditem: 'Hasarlı / kusurlu ürün gönderildi',
  fakeproduct: 'Ürün orijinal değil',
  undelivered: 'Teslim edilemeyen gönderi',
  undeliveredint: 'Müşterinin teslim almadığı gönderi',
  lateintdelivery: 'Sipariş geç teslim edildi',
}

/** The customer's own words when they say more than the reason's name, else null. */
export function returnCustomerNote(label: string, text: string | null | undefined): string | null {
  const note = text?.trim() ?? ''
  return note && normalizeReason(note) !== normalizeReason(label) ? note : null
}

function normalizeReason(value: string): string {
  return value.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase().replace(/[^a-z0-9]/g, '')
}

export function returnReasonPresentation(item: { reasonCode?: string | null; reasonText?: string | null }): { label: string; explanation: string | null } {
  const code = item.reasonCode?.trim() ?? ''
  const text = item.reasonText?.trim() ?? ''
  const normalizedCode = normalizeReason(code)
  const mappedLabel = RETURN_REASON_LABELS[normalizedCode]

  // Hepsiburada sends the reason as a readable title ("Ürünü almaktan vazgeçtim / beğenmedim") and the
  // customer's own words separately: title in bold, the customer's words as the explanation.
  if (!mappedLabel && /\s/u.test(code))
    return { label: code, explanation: text && normalizeReason(text) !== normalizeReason(code) ? text : null }

  if (mappedLabel === RETURN_REASON_LABELS.other || (!mappedLabel && text))
    return { label: text || mappedLabel || code.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ') || 'Belirtilmedi', explanation: null }

  const label = mappedLabel || code.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ') || text || 'Belirtilmedi'
  const explanation = text && normalizeReason(text) !== normalizeReason(label) ? text : null
  return { label, explanation }
}
