import { code128Modules, shippingLabelTextSize, type ShippingLabelBlock, type ShippingLabelBlockPosition, type ShippingLabelField, type ShippingLabelFormat, type ShippingLabelSettings } from './shipping-label'

// What one label block shows, in order. The printed label, its preview and the label designer all draw a block from
// these parts (ShippingLabelSurface): a barcode block draws its barcode then its other fields; the delivery address
// block shows its title, then the recipient's name, then the address lines; the other blocks show their text and
// fields. Address fields print as plain value lines without a caption, every other field as caption + value.
// The customer contact field shows only when the settings allow it (barcode blocks print every field they hold).
export type ShippingPreviewPart =
  | { type: 'barcode'; field: 'trackingNumber' | 'packageNumber' }
  | { type: 'title'; text: string }
  | { type: 'text'; text: string }
  | { type: 'field'; field: ShippingLabelField }
  | { type: 'lines'; field: 'address' | 'senderAddress' }

export const addressBlockTitle = 'Teslimat Adresi'

function fieldPart(field: ShippingLabelField): ShippingPreviewPart {
  return field === 'address' || field === 'senderAddress' ? { type: 'lines', field } : { type: 'field', field }
}

export function shippingLabelBlockParts(block: ShippingLabelBlock, options: { showCustomerPhone: boolean }): ShippingPreviewPart[] {
  const barcodeField = block.kind === 'trackingBarcode' ? 'trackingNumber' : block.kind === 'packageBarcode' ? 'packageNumber' : null
  if (barcodeField && block.fields.includes(barcodeField)) {
    return [{ type: 'barcode', field: barcodeField }, ...block.fields.filter(field => field !== barcodeField).map(fieldPart)]
  }
  const fields = block.fields.filter(field => options.showCustomerPhone || field !== 'customerEmail')
  const text: ShippingPreviewPart[] = block.text ? [{ type: 'text', text: block.text }] : []
  if (block.kind === 'address') {
    // Name first, then the address, then anything else the block holds.
    const ordered = [...fields.filter(field => field === 'customerName'), ...fields.filter(field => field === 'address'), ...fields.filter(field => field !== 'customerName' && field !== 'address')]
    return [{ type: 'title', text: addressBlockTitle }, ...ordered.map(fieldPart)]
  }
  if (block.kind === 'orderInfo' || block.kind === 'sender') return [...text, ...fields.map(fieldPart)]
  return [{ type: 'title', text: block.title }, ...text, ...fields.map(fieldPart)]
}

// The size of one label on paper. A sticker is the size the settings give; an A4 label shares the printable part of
// the page (A4 less the 8 mm print margin, 194 × 281 mm) with the others on it, half a millimetre short so rounding
// never pushes a label onto the next page.
export function shippingLabelSizeMm(format: ShippingLabelFormat, settings: Pick<ShippingLabelSettings, 'stickerWidthMm' | 'stickerHeightMm' | 'a4LabelsPerPage'>) {
  if (format === 'sticker') return { width: settings.stickerWidthMm, height: settings.stickerHeightMm }
  if (settings.a4LabelsPerPage === 4) return { width: 96.5, height: 140 }
  if (settings.a4LabelsPerPage === 2) return { width: 194, height: 140 }
  return { width: 194, height: 280 }
}

// The value sizes a block can take. Captions, titles and the barcode number print at one fixed caption size, so a
// label holds at most four sizes in one family; a saved size between the steps moves to the nearest one.
export const shippingLabelTextSizes = [
  { px: 12, label: 'Küçük' },
  { px: 14, label: 'Orta' },
  { px: 17, label: 'Büyük' }
] as const
export { shippingLabelTextSize }
export const shippingLabelCaptionPx = 10
export const shippingLabelBlockPaddingMm = 1.5

// Thermal printers print 8 dots a millimetre; a bar narrower than two dots blurs and scanners miss it.
export const shippingLabelMinimumModuleMm = 0.25
export const shippingLabelQuietZoneModules = 10

export function barcodeModuleWidthMm(value: string, blockWidthMm: number) {
  const modules = code128Modules(value).length + shippingLabelQuietZoneModules * 2
  return Math.max(0, blockWidthMm - shippingLabelBlockPaddingMm * 2) / modules
}

export type ShippingLabelLayoutIssue =
  | { kind: 'overlap'; blockIds: [string, string] }
  | { kind: 'narrowBarcode'; blockId: string; moduleMm: number }
  | { kind: 'overflow'; blockId: string }

function intersects(a: ShippingLabelBlockPosition, b: ShippingLabelBlockPosition) {
  // Touching edges and slivers under half a percent are not an overlap: a dragged block often lands a hair over.
  const width = Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x)
  const height = Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y)
  return width > 0.5 && height > 0.5
}

function rotatedBox(block: ShippingLabelBlock, position: ShippingLabelBlockPosition, size: { width: number; height: number }): ShippingLabelBlockPosition {
  if (block.rotation !== 90 && block.rotation !== 270) return position
  // A quarter turn swaps the box around its centre; in percent of a non-square label the sides scale by the ratio.
  const widthMm = position.width / 100 * size.width
  const heightMm = position.height / 100 * size.height
  const centreX = position.x + position.width / 2
  const centreY = position.y + position.height / 2
  const width = heightMm / size.width * 100
  const height = widthMm / size.height * 100
  return { x: centreX - width / 2, y: centreY - height / 2, width, height }
}

// What the layout itself gets wrong, before any order fills it: blocks on top of each other, and barcodes too narrow
// for their value to scan. Text that does not fit its block is measured where the label is drawn (overflow).
export function shippingLabelLayoutIssues(layout: ShippingLabelBlock[], size: { width: number; height: number }, barcodeValues: Partial<Record<'trackingNumber' | 'packageNumber', string>>): ShippingLabelLayoutIssue[] {
  const issues: ShippingLabelLayoutIssue[] = []
  const boxes = layout.map(block => block.position ? rotatedBox(block, block.position, size) : null)
  layout.forEach((block, index) => {
    for (let other = index + 1; other < layout.length; other++) {
      const a = boxes[index]
      const b = boxes[other]
      if (a && b && intersects(a, b)) issues.push({ kind: 'overlap', blockIds: [block.id, layout[other].id] })
    }
  })
  for (const block of layout) {
    const field = block.kind === 'trackingBarcode' ? 'trackingNumber' : block.kind === 'packageBarcode' ? 'packageNumber' : null
    if (!field || !block.fields.includes(field) || !block.position) continue
    const value = barcodeValues[field]
    if (!value) continue
    const quarterTurn = block.rotation === 90 || block.rotation === 270
    const widthMm = quarterTurn ? block.position.height / 100 * size.height : block.position.width / 100 * size.width
    const moduleMm = barcodeModuleWidthMm(value, widthMm)
    if (moduleMm < shippingLabelMinimumModuleMm) issues.push({ kind: 'narrowBarcode', blockId: block.id, moduleMm })
  }
  return issues
}

// Tidies a layout: each block (other than barcodes and turned blocks) takes the height its content needs, and the rows
// of blocks stack from the first row's top with the settings' gap between them. Blocks side by side form one row and
// keep their offsets in it. Returns null when the stack would not fit on the label.
export function stackShippingLabelLayout(layout: ShippingLabelBlock[], options: { contentHeights: Record<string, number>; gapPercent: number }): ShippingLabelBlock[] | null {
  const placed = layout.map(block => {
    if (!block.position) return block
    const fitted = options.contentHeights[block.id]
    const resizable = !block.rotation && block.kind !== 'trackingBarcode' && block.kind !== 'packageBarcode' && fitted != null && fitted > 0
    return { ...block, position: { ...block.position, height: resizable ? Math.max(5, Math.min(100, Math.ceil(fitted * 2) / 2)) : block.position.height } }
  })
  const stackable = placed.filter(block => block.position && !block.rotation).sort((a, b) => a.position!.y - b.position!.y)
  if (!stackable.length) return placed
  // Rows are read from the layout as it was drawn, before the heights change.
  const drawn = new Map(layout.map(block => [block.id, block.position]))
  const rows: ShippingLabelBlock[][] = []
  for (const block of stackable) {
    const row = rows[rows.length - 1]
    const rowBottom = row ? Math.max(...row.map(item => drawn.get(item.id)!.y + drawn.get(item.id)!.height)) : -1
    if (row && block.position!.y < rowBottom - 0.5) row.push(block)
    else rows.push([block])
  }
  const moved = new Map<string, ShippingLabelBlockPosition>()
  let top = rows[0][0].position!.y
  for (const row of rows) {
    const rowTop = Math.min(...row.map(block => block.position!.y))
    for (const block of row) moved.set(block.id, { ...block.position!, y: Math.round((top + block.position!.y - rowTop) * 10) / 10 })
    top = Math.max(...row.map(block => moved.get(block.id)!.y + block.position!.height)) + options.gapPercent
  }
  if (top - options.gapPercent > 100.05) return null
  return placed.map(block => moved.has(block.id) ? { ...block, position: moved.get(block.id)! } : block)
}

// A batch print's pages: a sticker a page; on A4 as many labels as share a page, two across when four do.
export function shippingLabelPages<T>(items: T[], format: ShippingLabelFormat, settings: Pick<ShippingLabelSettings, 'a4LabelsPerPage'>) {
  const perPage = format === 'a4' ? settings.a4LabelsPerPage : 1
  const pages: T[][] = []
  for (let index = 0; index < items.length; index += perPage) pages.push(items.slice(index, index + perPage))
  return { pages, across: format === 'a4' && perPage === 4 ? 2 : 1 }
}
