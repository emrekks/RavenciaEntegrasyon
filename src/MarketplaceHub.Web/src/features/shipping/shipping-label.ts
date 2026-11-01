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
  fontSize?: number
  rotation?: number
  position?: ShippingLabelBlockPosition
}

export const shippingLabelBlockCatalog: Array<{ kind: ShippingLabelBlockKind; label: string; description: string; fields: ShippingLabelField[] }> = [
  { kind: 'trackingBarcode', label: 'Takip Barkodu', description: 'Takip numarası ve barkod', fields: ['trackingNumber'] },
  { kind: 'address', label: 'Teslimat Adresi', description: 'Alıcı ve teslimat adresi', fields: ['customerName', 'address'] },
  { kind: 'orderInfo', label: 'Paket / Sipariş Bilgileri', description: 'Paket, sipariş ve iletişim bilgileri', fields: ['packageNumber', 'orderNumber', 'customerEmail'] },
  { kind: 'packageBarcode', label: 'Paket Barkodu', description: 'Paket numarası barkodu', fields: ['packageNumber'] },
  { kind: 'sender', label: 'Gönderici ve Kargo Bilgileri', description: 'Gönderici, adres ve kargo firması', fields: ['cargoProvider', 'senderName', 'senderAddress'] },
  { kind: 'custom', label: 'Özel İçerik Bloğu', description: 'Elle yazılan başlık ve metin', fields: [] }
]

export const shippingLabelFields: Array<{ id: ShippingLabelField; label: string }> = [
  { id: 'trackingNumber', label: 'Takip Numarası' },
  { id: 'packageNumber', label: 'Paket Numarası' },
  { id: 'orderNumber', label: 'Sipariş Numarası' },
  { id: 'customerName', label: 'Alıcı Adı' },
  { id: 'address', label: 'Teslimat Adresi' },
  { id: 'cargoProvider', label: 'Kargo Firması' },
  { id: 'senderName', label: 'Gönderici Adı' },
  { id: 'senderAddress', label: 'Gönderici Adresi' },
  { id: 'customerEmail', label: 'Müşteri e-postası' }
]

// Code 128 patterns expressed as alternating bar/space widths. Keeping this
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

// Code 128 symbol values for a value: an all-digit value of four or more digits packs two digits a symbol (code set C,
// a last odd digit in code set B), which makes a 17-digit tracking number a third narrower and its bars wider at the
// same label width; anything else uses code set B.
export function code128Symbols(value: string): number[] {
  const source = Array.from(value || 'NO-TRACKING').map(character => {
    const code = character.charCodeAt(0)
    return code >= 32 && code <= 127 ? character : '?'
  }).join('').slice(0, 80) || 'NO-TRACKING'
  let symbols: number[]
  if (/^\d{4,}$/.test(source)) {
    const evenLength = source.length - source.length % 2
    symbols = [105]
    for (let index = 0; index < evenLength; index += 2) symbols.push(Number(source.slice(index, index + 2)))
    if (evenLength < source.length) symbols.push(100, source.charCodeAt(evenLength) - 32)
  } else {
    symbols = [104, ...Array.from(source, character => character.charCodeAt(0) - 32)]
  }
  const checksum = symbols.reduce((total, symbol, index) => total + symbol * Math.max(1, index), 0) % 103
  return [...symbols, checksum, 106]
}

// The barcode as modules, true for a bar, without its quiet zone.
export function code128Modules(value: string): boolean[] {
  return code128Symbols(value).flatMap(symbol => {
    let isBar = true
    return Array.from(code128BWidths[symbol], width => {
      const result = Array.from({ length: Number(width) }, () => isBar)
      isBar = !isBar
      return result
    }).flat()
  })
}

export const code128Bars = code128Modules

// The value sizes a label block can take (shippingLabelTextSizes); a saved size between the steps moves to the nearest.
export function shippingLabelTextSize(fontSize: number | undefined): 12 | 14 | 17 {
  const size = fontSize ?? 14
  if (size <= 12) return 12
  if (size >= 16) return 17
  return 14
}

const defaultLayout: ShippingLabelBlock[] = [
  { id: 'trackingBarcode', kind: 'trackingBarcode', title: 'Takip Barkodu', fields: ['trackingNumber'], align: 'center', text: '', position: { x: 4, y: 2, width: 92, height: 17 } },
  { id: 'address', kind: 'address', title: 'Teslimat Adresi', fields: ['customerName', 'address'], align: 'left', text: '', position: { x: 4, y: 21, width: 92, height: 30 } },
  { id: 'orderInfo', kind: 'orderInfo', title: 'Paket / Sipariş Bilgileri', fields: ['packageNumber', 'orderNumber', 'customerEmail'], align: 'left', text: '', position: { x: 4, y: 53, width: 45, height: 30 } },
  { id: 'packageBarcode', kind: 'packageBarcode', title: 'Paket Barkodu', fields: ['packageNumber'], align: 'center', text: '', position: { x: 4, y: 85, width: 92, height: 13 } },
  { id: 'sender', kind: 'sender', title: 'Gönderici ve Kargo Bilgileri', fields: ['cargoProvider', 'senderName', 'senderAddress'], align: 'left', text: '', position: { x: 51, y: 53, width: 45, height: 30 } }
]

export type ShippingLabelSettings = {
  senderName: string
  senderAddress: string
  defaultFormat: 'a4' | 'sticker'
  showA4Button: boolean
  showStickerButton: boolean
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
    trackingBarcode: { x: 4, y: 2, width: 92, height: 17 },
    address: { x: 4, y: 21, width: 92, height: 30 },
    orderInfo: { x: 4, y: 53, width: 45, height: 30 },
    packageBarcode: { x: 4, y: 85, width: 92, height: 13 },
    sender: { x: 51, y: 53, width: 45, height: 30 },
    custom: { x: 10, y: Math.min(84, 8 + index * 16), width: 80, height: 12 }
  }
  return { ...(positions[kind] ?? positions.custom)! }
}

export const defaultShippingLabelSettings: ShippingLabelSettings = {
  senderName: 'Ravencia MarketplaceHub',
  senderAddress: '',
  defaultFormat: 'a4',
  showA4Button: true,
  showStickerButton: true,
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

function normalizedRotation(value: unknown) {
  const number = boundedNumber(value, 0, -360, 360)
  return ((Math.round(number / 90) * 90) % 360 + 360) % 360
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
  return { id, kind, title: typeof value.title === 'string' && value.title.trim() ? value.title.trim().slice(0, 120) : catalog.label, fields, align, text: typeof value.text === 'string' ? value.text.slice(0, 500) : '', fontSize: shippingLabelTextSize(boundedNumber(value.fontSize, 14, 8, 72)), rotation: normalizedRotation(value.rotation), position }
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

export function normalizeShippingLabelSettings(raw: unknown): ShippingLabelSettings {
  const value = raw && typeof raw === 'object' ? raw as Partial<ShippingLabelSettings> : {}
  const a4LabelsPerPage = value.a4LabelsPerPage === 2 || value.a4LabelsPerPage === 4 ? value.a4LabelsPerPage : 1
  return {
    senderName: typeof value.senderName === 'string' ? value.senderName.slice(0, 120) : defaultShippingLabelSettings.senderName,
    senderAddress: typeof value.senderAddress === 'string' ? value.senderAddress.slice(0, 500) : defaultShippingLabelSettings.senderAddress,
    defaultFormat: value.defaultFormat === 'sticker' ? 'sticker' : 'a4',
    showA4Button: value.showA4Button !== false,
    showStickerButton: value.showStickerButton !== false,
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
}

export function loadShippingLabelSettings(): ShippingLabelSettings {
  try {
    return normalizeShippingLabelSettings(JSON.parse(localStorage.getItem(storageKey) ?? 'null'))
  } catch {
    return normalizeShippingLabelSettings(null)
  }
}

export function hasStoredShippingLabelSettings() {
  try {
    return localStorage.getItem(storageKey) !== null
  } catch {
    return false
  }
}

const printedLabelsStorageKey = 'ravencia.printedShippingLabels'

export type ShippingLabelFormat = 'a4' | 'sticker'

export function printedShippingLabelKey(orderId: string, format: ShippingLabelFormat) {
  return `${orderId}:${format}`
}

export function loadPrintedShippingLabels(): string[] {
  try {
    const value = JSON.parse(localStorage.getItem(printedLabelsStorageKey) ?? '[]')
    return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []
  } catch {
    return []
  }
}

export function markShippingLabelPrinted(orderId: string, format: ShippingLabelFormat) {
  try {
    const key = printedShippingLabelKey(orderId, format)
    const next = Array.from(new Set([...loadPrintedShippingLabels(), key]))
    localStorage.setItem(printedLabelsStorageKey, JSON.stringify(next))
    return key
  } catch {
    return printedShippingLabelKey(orderId, format)
  }
}

// The fields an address block prints, the recipient's name first; a block saved without fields
// prints the name and the address (audit 2026-10-20 S1: the label printed the address only).
export function addressBlockFields(fields: readonly ShippingLabelField[]): ShippingLabelField[] {
  const own = fields.length > 0 ? [...fields] : ['customerName', 'address'] as ShippingLabelField[]
  return [...own.filter(field => field === 'customerName'), ...own.filter(field => field !== 'customerName')]
}

// The printed page: a sticker prints on a page of its own size without margins, A4 on A4 (audit 2026-10-20 S25).
export function shippingLabelPageCss(format: ShippingLabelFormat, settings: Pick<ShippingLabelSettings, 'stickerWidthMm' | 'stickerHeightMm'>) {
  const size = format === 'sticker' ? `${settings.stickerWidthMm}mm ${settings.stickerHeightMm}mm` : 'A4 portrait'
  return `@media print { @page { size: ${size}; margin: ${format === 'sticker' ? '0' : '8mm'}; } }`
}
