import { useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { code128Modules, shippingLabelCaptionStyle, shippingLabelFields, shippingLabelFieldsPerRow, shippingLabelFitsText, shippingLabelShowsBarcodeText, shippingLabelTextSize, type ShippingLabelBlock, type ShippingLabelField, type ShippingLabelFormat, type ShippingLabelSettings } from './shipping-label'
import { shippingLabelBlockParts, shippingLabelQuietZoneModules, shippingLabelSizeMm } from './shipping-label-layout'
import './shipping-label.css'

// One drawing of the label for every place it shows: the print, its preview in the order modal and the label designer
// all render these components with shipping-label.css, so what the screen shows is what the printer gets, scaled.

export type ShippingLabelValues = Record<ShippingLabelField, string>
export type ShippingLabelBarcodeField = 'trackingNumber' | 'packageNumber'

const fieldLabels = new Map(shippingLabelFields.map(field => [field.id, field.label]))
// The text sizes a fitted block can take: never below what a thermal printer prints sharply, never a shout.
export const shippingLabelFitMinimumPx = 10
export const shippingLabelFitMaximumPx = 24
const mmToPx = 96 / 25.4

export function ShippingLabelBarcode({ value, showText = true }: { value: string; showText?: boolean }) {
  if (!value) return <div className="lbl-barcode is-empty"><span className="lbl-barcode-missing">Takip Numarası Bekleniyor</span></div>
  // The quiet zone (ten modules of white on each side) is part of the drawing, so no layout can crowd it out.
  const modules = [...Array<boolean>(shippingLabelQuietZoneModules).fill(false), ...code128Modules(value), ...Array<boolean>(shippingLabelQuietZoneModules).fill(false)]
  let path = ''
  for (let index = 0; index < modules.length; index++) {
    if (!modules[index] || modules[index - 1]) continue
    let end = index
    while (modules[end + 1]) end++
    path += `M${index} 0h${end - index + 1}v1h-${end - index + 1}z`
  }
  return <div className="lbl-barcode" aria-label={`Barkod: ${value}`}>
    <svg viewBox={`0 0 ${modules.length} 1`} preserveAspectRatio="none" shapeRendering="crispEdges" aria-hidden="true"><path d={path} /></svg>
    {showText && <span className="lbl-barcode-text">{value}</span>}
  </div>
}

function lines(value: string) {
  return value.split('\n').map(line => line.trim()).filter(Boolean)
}

// The inside of one block: its padding, its text size and its parts, laid out in a column. Its layout options: a field's
// caption above its value, beside it ("Kargo Firması: Trendyol Express") or left out; one to three fields a row (the
// fields that follow each other share a grid, the title and barcode stay full width); a frame; capitals.
export function ShippingLabelBlockBody({ block, values, barcodes, showCustomerPhone }: { block: ShippingLabelBlock; values: ShippingLabelValues; barcodes?: Partial<Record<ShippingLabelBarcodeField, string>>; showCustomerPhone: boolean }) {
  const parts = shippingLabelBlockParts(block, { showCustomerPhone })
  const captionStyle = shippingLabelCaptionStyle(block)
  const perRow = shippingLabelFieldsPerRow(block)
  // Capitals are made in the text itself, in Turkish casing (i to İ), so no page style can undo them on screen or paper.
  const cased = (text: string) => block.uppercase ? text.toLocaleUpperCase('tr-TR') : text
  const fits = shippingLabelFitsText(block)
  const ref = useRef<HTMLDivElement>(null)
  // Yazıyı Kutuya Sığdır: before paint, the largest text size (in whole pixels) that the block holds without cutting.
  useLayoutEffect(() => {
    const body = ref.current
    if (!fits || !body || !body.clientHeight) return
    const base = shippingLabelTextSize(block.fontSize)
    const holds = (size: number) => {
      body.style.setProperty('--lbl-size', `${size}px`)
      const last = body.lastElementChild as HTMLElement | null
      const paddingBottom = parseFloat(getComputedStyle(body).paddingBottom || '0')
      return !last || (last.offsetTop + last.offsetHeight <= body.clientHeight - paddingBottom + 0.5 && body.scrollWidth <= body.clientWidth + 1)
    }
    let low = shippingLabelFitMinimumPx
    let high = shippingLabelFitMaximumPx
    if (!holds(low)) { body.style.setProperty('--lbl-size', `${low}px`); return }
    while (high - low > 0) {
      const middle = Math.ceil((low + high) / 2)
      if (holds(middle)) low = middle
      else high = middle - 1
    }
    body.style.setProperty('--lbl-size', `${low || base}px`)
  })
  const drawn: Array<{ field: boolean; node: ReactNode }> = parts.map((part, index) => {
    if (part.type === 'barcode') return { field: false, node: <ShippingLabelBarcode key={index} value={barcodes?.[part.field] ?? values[part.field]} showText={shippingLabelShowsBarcodeText(block)} /> }
    if (part.type === 'title') return { field: false, node: <span key={index} className="lbl-title">{cased(part.text)}</span> }
    if (part.type === 'text') return { field: false, node: <span key={index} className="lbl-value lbl-text">{cased(part.text)}</span> }
    if (part.type === 'lines') {
      const own = lines(values[part.field])
      // The sender address is optional in the settings: left empty, it prints nothing rather than a dash.
      if (!own.length && part.field === 'senderAddress') return { field: true, node: null }
      return { field: true, node: <span key={index} className="lbl-value lbl-lines">{own.length ? own.map((line, lineIndex) => <span key={lineIndex}>{cased(line)}</span>) : '—'}</span> }
    }
    return { field: true, node: <span key={index} className="lbl-field">{captionStyle !== 'none' && <span className="lbl-caption">{cased(fieldLabels.get(part.field) ?? part.field)}</span>}<span className="lbl-value">{cased(values[part.field] || '—')}</span></span> }
  })
  const children: ReactNode[] = []
  if (perRow === 1) children.push(...drawn.map(item => item.node))
  else {
    // Fields that follow each other share one grid of perRow columns.
    for (let index = 0; index < drawn.length; index++) {
      if (!drawn[index].field) { children.push(drawn[index].node); continue }
      const row: ReactNode[] = []
      while (index < drawn.length && drawn[index].field) { if (drawn[index].node) row.push(drawn[index].node); index++ }
      index--
      if (row.length) children.push(<div key={`row-${index}`} className="lbl-row" style={{ '--lbl-cols': perRow } as CSSProperties}>{row}</div>)
    }
  }
  const className = `lbl-body${captionStyle === 'beside' ? ' is-inline' : ''}${block.boxed ? ' is-boxed' : ''}${block.uppercase ? ' is-upper' : ''}${fits ? ' is-fit' : ''}`
  return <div ref={ref} className={className} data-block-id={block.id} style={{ '--lbl-size': `${shippingLabelTextSize(block.fontSize)}px`, textAlign: block.align } as CSSProperties}>
    {children}
  </div>
}

export function shippingLabelBlockStyle(block: ShippingLabelBlock): CSSProperties {
  const position = block.position
  return {
    ...(position ? { left: `${position.x}%`, top: `${position.y}%`, width: `${position.width}%`, height: `${position.height}%` } : {}),
    transform: block.rotation ? `rotate(${block.rotation}deg)` : undefined
  }
}

// Blocks whose text is cut: it reaches past the block's edge (running into the block's own padding is not cut).
export function overflowingLabelBlocks(root: HTMLElement | null) {
  if (!root) return []
  return Array.from(root.querySelectorAll<HTMLElement>('.lbl-body')).filter(body => {
    const last = body.lastElementChild as HTMLElement | null
    return (last != null && last.offsetTop + last.offsetHeight > body.clientHeight + 0.5) || body.scrollWidth > body.clientWidth + 1
  }).map(body => body.dataset.blockId ?? '')
}

// The height a block's content needs, in layout pixels of the label (its padding included).
export function labelBlockContentHeight(body: HTMLElement) {
  const last = body.lastElementChild as HTMLElement | null
  if (!last) return 0
  return last.offsetTop + last.offsetHeight + parseFloat(getComputedStyle(body).paddingBottom || '0')
}

export function ShippingLabelSurface({ settings, format, values, barcodes, onOverflow, className = '' }: { settings: ShippingLabelSettings; format: ShippingLabelFormat; values: ShippingLabelValues; barcodes?: Partial<Record<ShippingLabelBarcodeField, string>>; onOverflow?: (blockIds: string[]) => void; className?: string }) {
  const size = shippingLabelSizeMm(format, settings)
  const ref = useRef<HTMLElement>(null)
  const overflowKey = useRef('')
  useLayoutEffect(() => {
    if (!onOverflow) return
    const ids = overflowingLabelBlocks(ref.current)
    const key = ids.join('|')
    if (key === overflowKey.current) return
    overflowKey.current = key
    onOverflow(ids)
  })
  return <article ref={ref} className={`shipping-label-print-surface lbl-sheet shipping-label-${format} a4-count-${settings.a4LabelsPerPage} ${className}`.trim()} style={{ width: `${size.width}mm`, height: `${size.height}mm` }}>
    {settings.layout[format].map(block => <div key={block.id} className="lbl-block" style={shippingLabelBlockStyle(block)}><ShippingLabelBlockBody block={block} values={values} barcodes={barcodes} showCustomerPhone={settings.showCustomerPhone} /></div>)}
  </article>
}

// Shows a label at its real size, or smaller when the space is narrower (or lower than maxHeight); prints at 1:1.
export function ShippingLabelFit({ widthMm, heightMm, maxHeight, children }: { widthMm: number; heightMm: number; maxHeight?: number; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null)
  const [scale, setScale] = useState(1)
  useLayoutEffect(() => {
    const element = ref.current
    if (!element) return
    const update = () => {
      const width = element.clientWidth
      if (!width) return
      const next = Math.min(1, width / (widthMm * mmToPx), maxHeight ? maxHeight / (heightMm * mmToPx) : 1)
      setScale(Math.floor(next * 100) / 100)
    }
    update()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(update)
    observer.observe(element)
    return () => observer.disconnect()
  }, [widthMm, heightMm, maxHeight])
  return <div ref={ref} className="lbl-fit"><div className="lbl-fit-inner" style={{ zoom: scale } as CSSProperties}>{children}</div></div>
}
