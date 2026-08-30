export type ShippingLabelBlock = 'trackingBarcode' | 'address' | 'orderInfo' | 'packageBarcode' | 'sender'

export const shippingLabelBlocks: Array<{ id: ShippingLabelBlock; label: string }> = [
  { id: 'trackingBarcode', label: 'Takip barkodu' },
  { id: 'address', label: 'Teslimat adresi' },
  { id: 'orderInfo', label: 'Paket / sipariş bilgileri' },
  { id: 'packageBarcode', label: 'Paket barkodu' },
  { id: 'sender', label: 'Gönderici ve kargo bilgileri' }
]

const defaultLayout: ShippingLabelBlock[] = ['trackingBarcode', 'address', 'orderInfo', 'packageBarcode', 'sender']

export type ShippingLabelSettings = {
  senderName: string
  senderAddress: string
  a4LabelsPerPage: 1 | 2 | 4
  stickerWidthMm: number
  stickerHeightMm: number
  showCustomerPhone: boolean
  sectionGapMm: number
  layout: {
    a4: ShippingLabelBlock[]
    sticker: ShippingLabelBlock[]
  }
}

export const defaultShippingLabelSettings: ShippingLabelSettings = {
  senderName: 'Ravencia MarketplaceHub',
  senderAddress: '',
  a4LabelsPerPage: 1,
  stickerWidthMm: 100,
  stickerHeightMm: 150,
  showCustomerPhone: true,
  sectionGapMm: 4,
  layout: {
    a4: [...defaultLayout],
    sticker: [...defaultLayout]
  }
}

const storageKey = 'ravencia.shippingLabelSettings'

function boundedNumber(value: unknown, fallback: number, min: number, max: number) {
  const number = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(number) ? Math.min(max, Math.max(min, number)) : fallback
}

function normalizeLayout(value: unknown, fallback: ShippingLabelBlock[]) {
  const valid = new Set<ShippingLabelBlock>(shippingLabelBlocks.map(block => block.id))
  const selected = Array.isArray(value) ? value.filter((block, index): block is ShippingLabelBlock => valid.has(block) && value.indexOf(block) === index) : []
  return [...selected, ...fallback.filter(block => !selected.includes(block))]
}

export function loadShippingLabelSettings(): ShippingLabelSettings {
  try {
    const value = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as Partial<ShippingLabelSettings> | null
    if (!value || typeof value !== 'object') return { ...defaultShippingLabelSettings }
    const a4LabelsPerPage = value.a4LabelsPerPage === 2 || value.a4LabelsPerPage === 4 ? value.a4LabelsPerPage : 1
    return {
      senderName: typeof value.senderName === 'string' ? value.senderName.slice(0, 120) : defaultShippingLabelSettings.senderName,
      senderAddress: typeof value.senderAddress === 'string' ? value.senderAddress.slice(0, 500) : defaultShippingLabelSettings.senderAddress,
      a4LabelsPerPage,
      stickerWidthMm: boundedNumber(value.stickerWidthMm, defaultShippingLabelSettings.stickerWidthMm, 40, 300),
      stickerHeightMm: boundedNumber(value.stickerHeightMm, defaultShippingLabelSettings.stickerHeightMm, 40, 300),
      showCustomerPhone: value.showCustomerPhone !== false,
      sectionGapMm: boundedNumber(value.sectionGapMm, defaultShippingLabelSettings.sectionGapMm, 0, 20),
      layout: {
        a4: normalizeLayout(value.layout?.a4, defaultShippingLabelSettings.layout.a4),
        sticker: normalizeLayout(value.layout?.sticker, defaultShippingLabelSettings.layout.sticker)
      }
    }
  } catch {
    return { ...defaultShippingLabelSettings }
  }
}

export function saveShippingLabelSettings(settings: ShippingLabelSettings) {
  try { localStorage.setItem(storageKey, JSON.stringify(settings)) } catch { /* Private browsing may disallow local storage. */ }
}
