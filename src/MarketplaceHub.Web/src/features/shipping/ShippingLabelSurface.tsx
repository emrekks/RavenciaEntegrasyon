import { useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { code128Modules, shippingLabelFields, shippingLabelTextSize, type ShippingLabelBlock, type ShippingLabelField, type ShippingLabelFormat, type ShippingLabelSettings } from './shipping-label'
import { shippingLabelBlockParts, shippingLabelQuietZoneModules, shippingLabelSizeMm } from './shipping-label-layout'
import './shipping-label.css'

// One drawing of the label for every place it shows: the print, its preview in the order modal and the label designer
// all render these components with shipping-label.css, so what the screen shows is what the printer gets, scaled.

export type ShippingLabelValues = Record<ShippingLabelField, string>
export type ShippingLabelBarcodeField = 'trackingNumber' | 'packageNumber'

const fieldLabels = new Map(shippingLabelFields.map(field => [field.id, field.label]))
const mmToPx = 96 / 25.4

export function ShippingLabelBarcode({ value }: { value: string }) {
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
    <span className="lbl-barcode-text">{value}</span>
  </div>
}

function lines(value: string) {
  return value.split('\n').map(line => line.trim()).filter(Boolean)
}

// The inside of one block: its padding, its text size and its parts, laid out in a column.
export function ShippingLabelBlockBody({ block, values, barcodes, showCustomerPhone }: { block: ShippingLabelBlock; values: ShippingLabelValues; barcodes?: Partial<Record<ShippingLabelBarcodeField, string>>; showCustomerPhone: boolean }) {
  const parts = shippingLabelBlockParts(block, { showCustomerPhone })
  return <div className="lbl-body" data-block-id={block.id} style={{ '--lbl-size': `${shippingLabelTextSize(block.fontSize)}px`, textAlign: block.align } as CSSProperties}>
    {parts.map((part, index) => {
      if (part.type === 'barcode') return <ShippingLabelBarcode key={index} value={barcodes?.[part.field] ?? values[part.field]} />
      if (part.type === 'title') return <span key={index} className="lbl-title">{part.text}</span>
      if (part.type === 'text') return <span key={index} className="lbl-value lbl-text">{part.text}</span>
      if (part.type === 'lines') {
        const own = lines(values[part.field])
        // The sender address is optional in the settings: left empty, it prints nothing rather than a dash.
        if (!own.length && part.field === 'senderAddress') return null
        return <span key={index} className="lbl-value lbl-lines">{own.length ? own.map((line, lineIndex) => <span key={lineIndex}>{line}</span>) : '—'}</span>
      }
      return <span key={index} className="lbl-field"><span className="lbl-caption">{fieldLabels.get(part.field) ?? part.field}</span><span className="lbl-value">{values[part.field] || '—'}</span></span>
    })}
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
