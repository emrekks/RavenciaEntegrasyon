export type ShippingLabelBlockKind = 'trackingBarcode' | 'address' | 'orderInfo' | 'packageBarcode' | 'sender' | 'custom'
export type ShippingLabelField = 'trackingNumber' | 'packageNumber' | 'orderNumber' | 'customerName' | 'address' | 'cargoProvider' | 'senderName' | 'senderAddress' | 'customerEmail'
export type ShippingLabelAlignment = 'left' | 'center' | 'right'

export type ShippingLabelBlock = {
  id: string
  kind: ShippingLabelBlockKind
  title: string
  fields: ShippingLabelField[]
  align: ShippingLabelAlignment
  text: string
}

export const shippingLabelBlockCatalog: Array<{ kind: ShippingLabelBlockKind; label: string; description: string; fields: ShippingLabelField[] }> = [
  { kind: 'trackingBarcode', label: 'Takip barkodu', description: 'Takip numarası ve barkod', fields: ['trackingNumber'] },
  { kind: 'address', label: 'Teslimat adresi', description: 'Alıcı ve teslimat adresi', fields: ['customerName', 'address'] },
  { kind: 'orderInfo', label: 'Paket / sipariş bilgileri', description: 'Paket, sipariş ve iletişim bilgileri', fields: ['packageNumber', 'orderNumber', 'customerEmail'] },
  { kind: 'packageBarcode', label: 'Paket barkodu', description: 'Paket numarası barkodu', fields: ['packageNumber'] },
  { kind: 'sender', label: 'Gönderici ve kargo bilgileri', description: 'Gönderici, adres ve kargo firması', fields: ['cargoProvider', 'senderName', 'senderAddress'] },
  { kind: 'custom', label: 'Özel içerik bloğu', description: 'Elle yazılan başlık ve metin', fields: [] }
]

export const shippingLabelFields: Array<{ id: ShippingLabelField; label: string }> = [
  { id: 'trackingNumber', label: 'Takip numarası' },
  { id: 'packageNumber', label: 'Paket numarası' },
  { id: 'orderNumber', label: 'Sipariş numarası' },
  { id: 'customerName', label: 'Alıcı adı' },
  { id: 'address', label: 'Teslimat adresi' },
  { id: 'cargoProvider', label: 'Kargo firması' },
  { id: 'senderName', label: 'Gönderici adı' },
  { id: 'senderAddress', label: 'Gönderici adresi' },
  { id: 'customerEmail', label: 'Müşteri e-postası' }
]

const defaultLayout: ShippingLabelBlock[] = [
  { id: 'trackingBarcode', kind: 'trackingBarcode', title: 'Takip barkodu', fields: ['trackingNumber'], align: 'center', text: '' },
  { id: 'address', kind: 'address', title: 'Teslimat adresi', fields: ['customerName', 'address'], align: 'left', text: '' },
  { id: 'orderInfo', kind: 'orderInfo', title: 'Paket / sipariş bilgileri', fields: ['packageNumber', 'orderNumber', 'customerEmail'], align: 'left', text: '' },
  { id: 'packageBarcode', kind: 'packageBarcode', title: 'Paket barkodu', fields: ['packageNumber'], align: 'center', text: '' },
  { id: 'sender', kind: 'sender', title: 'Gönderici ve kargo bilgileri', fields: ['cargoProvider', 'senderName', 'senderAddress'], align: 'left', text: '' }
]

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

function cloneLayout(layout: ShippingLabelBlock[]) {
  return layout.map(block => ({ ...block, fields: [...block.fields] }))
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
    a4: cloneLayout(defaultLayout),
    sticker: cloneLayout(defaultLayout)
  }
}

const storageKey = 'ravencia.shippingLabelSettings'
const catalogByKind = new Map(shippingLabelBlockCatalog.map(block => [block.kind, block]))
const validFields = new Set<ShippingLabelField>(shippingLabelFields.map(field => field.id))

function boundedNumber(value: unknown, fallback: number, min: number, max: number) {
  const number = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(number) ? Math.min(max, Math.max(min, number)) : fallback
}

function safeBlock(raw: unknown, index: number): ShippingLabelBlock | null {
  if (typeof raw === 'string') {
    const catalog = catalogByKind.get(raw as ShippingLabelBlockKind)
    return catalog && catalog.kind !== 'custom' ? { id: catalog.kind, kind: catalog.kind, title: catalog.label, fields: [...catalog.fields], align: catalog.kind === 'trackingBarcode' || catalog.kind === 'packageBarcode' ? 'center' : 'left', text: '' } : null
  }
  if (!raw || typeof raw !== 'object') return null
  const value = raw as Partial<ShippingLabelBlock>
  const kind = catalogByKind.has(value.kind as ShippingLabelBlockKind) ? value.kind as ShippingLabelBlockKind : 'custom'
  const catalog = catalogByKind.get(kind)!
  const fields = Array.isArray(value.fields) ? value.fields.filter((field, fieldIndex): field is ShippingLabelField => validFields.has(field as ShippingLabelField) && value.fields!.indexOf(field) === fieldIndex) : [...catalog.fields]
  const align = value.align === 'center' || value.align === 'right' ? value.align : 'left'
  const id = typeof value.id === 'string' && value.id.trim() ? value.id.trim().slice(0, 80) : `${kind}-${index + 1}`
  return { id, kind, title: typeof value.title === 'string' && value.title.trim() ? value.title.trim().slice(0, 120) : catalog.label, fields, align, text: typeof value.text === 'string' ? value.text.slice(0, 500) : '' }
}

function normalizeLayout(value: unknown, fallback: ShippingLabelBlock[]) {
  const selected: ShippingLabelBlock[] = []
  if (Array.isArray(value)) {
    for (const [index, raw] of value.entries()) {
      const block = safeBlock(raw, index)
      if (block && !selected.some(existing => existing.id === block.id)) selected.push(block)
    }
  }
  return selected.length ? selected : cloneLayout(fallback)
}

export function loadShippingLabelSettings(): ShippingLabelSettings {
  try {
    const value = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as Partial<ShippingLabelSettings> | null
    if (!value || typeof value !== 'object') return { ...defaultShippingLabelSettings, layout: { a4: cloneLayout(defaultLayout), sticker: cloneLayout(defaultLayout) } }
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
        a4: normalizeLayout(value.layout?.a4, defaultLayout),
        sticker: normalizeLayout(value.layout?.sticker, defaultLayout)
      }
    }
  } catch {
    return { ...defaultShippingLabelSettings, layout: { a4: cloneLayout(defaultLayout), sticker: cloneLayout(defaultLayout) } }
  }
}

export function saveShippingLabelSettings(settings: ShippingLabelSettings) {
  try { localStorage.setItem(storageKey, JSON.stringify(settings)) } catch { /* Private browsing may disallow local storage. */ }
}
