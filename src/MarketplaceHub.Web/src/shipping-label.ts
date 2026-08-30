export type ShippingLabelBlockKind = 'trackingBarcode' | 'address' | 'orderInfo' | 'packageBarcode' | 'sender' | 'custom'
export type ShippingLabelField = 'trackingNumber' | 'packageNumber' | 'orderNumber' | 'customerName' | 'address' | 'cargoProvider' | 'senderName' | 'senderAddress' | 'customerEmail'
export type ShippingLabelAlignment = 'left' | 'center' | 'right'
export type ShippingLabelBlockPosition = { x: number; y: number; width: number; height: number }

export type ShippingLabelBlock = {
  id: string
  kind: ShippingLabelBlockKind
  title: string
  fields: ShippingLabelField[]
  align: ShippingLabelAlignment
  text: string
  position?: ShippingLabelBlockPosition
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

// Code 128B patterns expressed as alternating bar/space widths. Keeping this
// in one place makes the settings preview and the printed label use the same
// scanner-readable symbol instead of a decorative repeating gradient.
const code128BWidths = [
  '212222', '222122', '222221', '121223', '121322', '131222', '122213', '122312', '132212', '221213',
  '221312', '231212', '112232', '122132', '122231', '113222', '123122', '123221', '223211', '221132',
  '221231', '213212', '223112', '312131', '311222', '321122', '321221', '312212', '322112', '322211',
  '212123', '212321', '232121', '111323', '131123', '131321', '112313', '132113', '132311', '211313',
  '231113', '231311', '112133', '112331', '132131', '113123', '113321', '133121', '313121', '211331',
  '231131', '213113', '213311', '213131', '311123', '311321', '331121', '312113', '312311', '332111',
  '314111', '221411', '431111', '111224', '111422', '121124', '121421', '141122', '141221', '112214',
  '112412', '122114', '122411', '142112', '142211', '241211', '221114', '413111', '241112', '134111',
  '111242', '121142', '121241', '114212', '124112', '124211', '411212', '421112', '421211', '212141',
  '214121', '412121', '111143', '111341', '131141', '114113', '114311', '411113', '411311', '113141',
  '114131', '311141', '411131', '211412', '211214', '211232', '2331112'
]

export function code128Bars(value: string): boolean[] {
  const source = Array.from(value || 'NO-TRACKING').map(character => {
    const code = character.charCodeAt(0)
    return code >= 32 && code <= 127 ? character : '?'
  }).join('').slice(0, 80) || 'NO-TRACKING'
  const symbols = Array.from(source, character => character.charCodeAt(0) - 32)
  const checksum = (104 + symbols.reduce((total, symbol, index) => total + symbol * (index + 1), 0)) % 103
  return [104, ...symbols, checksum, 106].flatMap(symbol => {
    let isBar = true
    return Array.from(code128BWidths[symbol], width => {
      const modules = Number(width)
      const result = Array.from({ length: modules }, () => isBar)
      isBar = !isBar
      return result
    }).flat()
  })
}

const defaultLayout: ShippingLabelBlock[] = [
  { id: 'trackingBarcode', kind: 'trackingBarcode', title: 'Takip barkodu', fields: ['trackingNumber'], align: 'center', text: '', position: { x: 8, y: 5, width: 84, height: 18 } },
  { id: 'address', kind: 'address', title: 'Teslimat adresi', fields: ['customerName', 'address'], align: 'left', text: '', position: { x: 8, y: 26, width: 84, height: 27 } },
  { id: 'orderInfo', kind: 'orderInfo', title: 'Paket / sipariş bilgileri', fields: ['packageNumber', 'orderNumber', 'customerEmail'], align: 'left', text: '', position: { x: 8, y: 57, width: 84, height: 14 } },
  { id: 'packageBarcode', kind: 'packageBarcode', title: 'Paket barkodu', fields: ['packageNumber'], align: 'center', text: '', position: { x: 8, y: 74, width: 84, height: 13 } },
  { id: 'sender', kind: 'sender', title: 'Gönderici ve kargo bilgileri', fields: ['cargoProvider', 'senderName', 'senderAddress'], align: 'left', text: '', position: { x: 8, y: 90, width: 84, height: 8 } }
]

export type ShippingLabelSettings = {
  senderName: string
  senderAddress: string
  defaultFormat: 'a4' | 'sticker'
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
  return layout.map(block => ({ ...block, fields: [...block.fields], position: block.position ? { ...block.position } : undefined }))
}

export function defaultShippingLabelBlockPosition(kind: ShippingLabelBlockKind, index: number): ShippingLabelBlockPosition {
  const positions: Partial<Record<ShippingLabelBlockKind, ShippingLabelBlockPosition>> = {
    trackingBarcode: { x: 8, y: 5, width: 84, height: 18 },
    address: { x: 8, y: 26, width: 84, height: 27 },
    orderInfo: { x: 8, y: 57, width: 84, height: 14 },
    packageBarcode: { x: 8, y: 74, width: 84, height: 13 },
    sender: { x: 8, y: 90, width: 84, height: 8 },
    custom: { x: 10, y: Math.min(84, 8 + index * 16), width: 80, height: 12 }
  }
  return { ...(positions[kind] ?? positions.custom)! }
}

export const defaultShippingLabelSettings: ShippingLabelSettings = {
  senderName: 'Ravencia MarketplaceHub',
  senderAddress: '',
  defaultFormat: 'a4',
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
    return catalog && catalog.kind !== 'custom' ? { id: catalog.kind, kind: catalog.kind, title: catalog.label, fields: [...catalog.fields], align: catalog.kind === 'trackingBarcode' || catalog.kind === 'packageBarcode' ? 'center' : 'left', text: '', position: defaultShippingLabelBlockPosition(catalog.kind, index) } : null
  }
  if (!raw || typeof raw !== 'object') return null
  const value = raw as Partial<ShippingLabelBlock>
  const kind = catalogByKind.has(value.kind as ShippingLabelBlockKind) ? value.kind as ShippingLabelBlockKind : 'custom'
  const catalog = catalogByKind.get(kind)!
  const fields = Array.isArray(value.fields) ? value.fields.filter((field, fieldIndex): field is ShippingLabelField => validFields.has(field as ShippingLabelField) && value.fields!.indexOf(field) === fieldIndex) : [...catalog.fields]
  const align = value.align === 'center' || value.align === 'right' ? value.align : 'left'
  const id = typeof value.id === 'string' && value.id.trim() ? value.id.trim().slice(0, 80) : `${kind}-${index + 1}`
  const rawPosition = value.position as Partial<ShippingLabelBlockPosition> | undefined
  const fallbackPosition = defaultShippingLabelBlockPosition(kind, index)
  const position = rawPosition && typeof rawPosition === 'object' ? {
    x: boundedNumber(rawPosition.x, fallbackPosition.x, 0, 92),
    y: boundedNumber(rawPosition.y, fallbackPosition.y, 0, 92),
    width: boundedNumber(rawPosition.width, fallbackPosition.width, 8, 100),
    height: boundedNumber(rawPosition.height, fallbackPosition.height, 5, 100)
  } : fallbackPosition
  position.width = Math.min(position.width, 100 - position.x)
  position.height = Math.min(position.height, 100 - position.y)
  return { id, kind, title: typeof value.title === 'string' && value.title.trim() ? value.title.trim().slice(0, 120) : catalog.label, fields, align, text: typeof value.text === 'string' ? value.text.slice(0, 500) : '', position }
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
      defaultFormat: value.defaultFormat === 'sticker' ? 'sticker' : 'a4',
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
