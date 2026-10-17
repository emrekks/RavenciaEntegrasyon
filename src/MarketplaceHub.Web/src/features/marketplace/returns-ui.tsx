import { useEffect, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { InvoiceGlyph, type InvoiceGlyphKind } from '../invoicing/invoice-state-glyph'
import './returns-sade.css'

// Building blocks of the returns page, as drawn in docs/tasarim-onerileri/iadeler-yeni.html. Class names carry the
// "rr-" prefix of returns-sade.css.

type IconName = 'search' | 'chev' | 'left' | 'right' | 'cal' | 'down' | 'up' | 'filter' | 'copy' | 'open' | 'close' | 'doc' | 'check' | 'box' | 'refresh' | 'upload' | 'alert' | 'info' | 'print' | 'arrowR' | 'image' | 'clock' | 'truck' | 'play' | 'edit' | 'ban' | 'export' | 'import' | 'plus' | 'trash' | 'dup' | 'price' | 'stock' | 'on' | 'off' | 'sortv'

const iconPaths: Record<IconName, [ReactNode, number]> = {
  search: [<><circle cx="7" cy="7" r="4.5" /><path d="m10.5 10.5 3 3" /></>, 16],
  chev: [<path d="m4.5 6 3.5 3.5L11.5 6" />, 16],
  left: [<path d="M10 3.5 5.5 8l4.5 4.5" />, 16],
  right: [<path d="m6 3.5 4.5 4.5L6 12.5" />, 16],
  cal: [<><rect x="2.5" y="3.5" width="11" height="10" rx="1.5" /><path d="M2.5 6.5h11M5.5 2v3M10.5 2v3" /></>, 16],
  down: [<path d="M8 3v10M4.5 9.5 8 13l3.5-3.5" />, 16],
  up: [<path d="M8 13V3M4.5 6.5 8 3l3.5 3.5" />, 16],
  filter: [<path d="M2.5 3.5h11l-4.3 5v4l-2.4 1v-5z" />, 14],
  copy: [<><rect x="5" y="5" width="8.5" height="8.5" rx="1.5" /><path d="M11 5V3.5a1 1 0 0 0-1-1H3.5a1 1 0 0 0-1 1V10a1 1 0 0 0 1 1H5" /></>, 14],
  open: [<path d="M9.5 2.5h4v4M13.5 2.5 8 8M11.5 9.5v3a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-7a1 1 0 0 1 1-1h3" />, 14],
  close: [<path d="m4 4 8 8M12 4l-8 8" />, 16],
  doc: [<><path d="M4 1.8h5.5L12.5 5v9.2H4z" /><path d="M9.5 1.8V5h3M6 8.5h4.5M6 11h4.5" /></>, 16],
  check: [<path d="m3.5 8.3 2.9 2.9 6.1-6.4" />, 16],
  box: [<><path d="M2.5 5 8 2.2 13.5 5v6L8 13.8 2.5 11z" /><path d="M2.5 5 8 7.8 13.5 5M8 7.8v6" /></>, 16],
  refresh: [<><path d="M13 8a5 5 0 1 1-1.5-3.5" /><path d="M13 2.5v3h-3" /></>, 16],
  upload: [<><path d="M8 10.5V2.5M4.8 5.5 8 2.5l3.2 3" /><path d="M2.5 10.5v2.5h11v-2.5" /></>, 16],
  alert: [<><path d="M8 2.5 14 13H2z" /><path d="M8 6.5v3M8 11.5h.01" /></>, 16],
  info: [<><circle cx="8" cy="8" r="6.2" /><path d="M8 7.2v3.6M8 5h.01" /></>, 16],
  print: [<><path d="M4.5 6V2.5h7V6" /><rect x="2" y="6" width="12" height="5.5" rx="1.2" /><path d="M4.5 9.5h7v4h-7z" /></>, 16],
  arrowR: [<path d="M3 8h10M9 4l4 4-4 4" />, 14],
  image: [<><rect x="2" y="3" width="12" height="10" rx="1.5" /><circle cx="6" cy="6.5" r="1.2" /><path d="m2.5 12 4-4 3 3 2-2 2.5 2.5" /></>, 28],
  clock: [<><circle cx="8" cy="8" r="6" /><path d="M8 4.8V8l2 1.3" /></>, 14],
  truck: [<><path d="M1.8 4h7.7v7H1.8zM9.5 6.5h2.8l2 2.3V11H9.5" /><circle cx="4.5" cy="11.8" r="1.2" /><circle cx="11.8" cy="11.8" r="1.2" /></>, 16],
  play: [<path d="M5 3.5v9l7-4.5z" />, 16],
  edit: [<path d="M10.5 2.8 13.2 5.5 6 12.7l-3.2.5.5-3.2z" />, 16],
  ban: [<><circle cx="8" cy="8" r="5.8" /><path d="m4 12 8-8" /></>, 16],
  export: [<><path d="M8 2.5v8M4.8 7.5 8 10.7l3.2-3.2" /><path d="M2.5 10.5v3h11v-3" /></>, 16],
  import: [<><path d="M8 10.5V2.5M4.8 5.5 8 2.5l3.2 3" /><path d="M2.5 10.5v3h11v-3" /></>, 16],
  plus: [<path d="M8 3v10M3 8h10" />, 16],
  trash: [<path d="M3 4.5h10M6.5 4.5V3h3v1.5M4.5 4.5l.6 9h5.8l.6-9" />, 16],
  dup: [<><rect x="5" y="5" width="8.5" height="8.5" rx="1.5" /><path d="M11 5V3.5a1 1 0 0 0-1-1H3.5a1 1 0 0 0-1 1V10a1 1 0 0 0 1 1H5" /></>, 16],
  price: [<><path d="M2.5 8.6V2.5h6.1l5 5-6.1 6.1z" /><circle cx="5.6" cy="5.6" r="1" /></>, 16],
  stock: [<><path d="M2.5 5 8 2.2 13.5 5v6L8 13.8 2.5 11z" /><path d="M2.5 5 8 7.8 13.5 5M8 7.8v6" /></>, 16],
  on: [<><circle cx="8" cy="8" r="5.8" /><path d="m5.5 8.2 1.7 1.7 3.4-3.6" /></>, 16],
  off: [<><circle cx="8" cy="8" r="5.8" /><path d="M6.6 5.8v4.4M9.4 5.8v4.4" /></>, 16],
  sortv: [<path d="M5 3v10M2.8 10.8 5 13l2.2-2.2M11 13V3M8.8 5.2 11 3l2.2 2.2" />, 14],
}

export function RrIcon({ name, size, className, style }: { name: IconName; size?: number; className?: string; style?: CSSProperties }) {
  const [paths, base] = iconPaths[name]
  const s = size ?? base
  return <svg className={['rr-ico', className].filter(Boolean).join(' ')} style={style} width={s} height={s} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths}</svg>
}

// Words in title case, Turkish rules (i → İ).
export function titleTr(value: string) {
  return value.split(/(\s+)/).map(part => part.trim() ? part.charAt(0).toLocaleUpperCase('tr-TR') + part.slice(1) : part).join('')
}

// Progress ring: how far the return (or its invoice) has come, coloured by its state.
export type RingTone = 'prog' | 'move' | 'wait' | 'ok' | 'bad' | 'off'
export function RrRing({ tone, progress, label, detail, detailClass }: { tone: RingTone; progress: number; label: string; detail?: ReactNode; detailClass?: string }) {
  const value = Math.round(Math.max(0, Math.min(1, progress)) * 100)
  return <span className={`rr-ring rr-tone-${tone}`}>
    <svg viewBox="0 0 20 20" aria-hidden="true"><circle className="rr-trk" cx="10" cy="10" r="7.5" />{value > 0 && <circle className="rr-val" cx="10" cy="10" r="7.5" pathLength={100} strokeDasharray={`${value} 100`} />}</svg>
    <span><b>{label}</b>{detail ? <small className={detailClass ? `rr-${detailClass}` : undefined}>{detail}</small> : null}</span>
  </span>
}

// Status tags carry the state glyph of the invoices page.
const stateGlyph: Record<string, InvoiceGlyphKind> = {
  'Talep Oluşturuldu': 'placed', 'Kargo Bekliyor': 'packing', 'Kargoya Verildi': 'carrier', 'İade Taşımada': 'carrier',
  'Aksiyon Bekliyor': 'waiting', 'Ön Onay Bekliyor': 'waiting', 'İnceleme Talebi Gönderildi': 'sending', 'İncelemede': 'sending',
  'Onaylandı': 'done', 'Tamamlandı': 'done', 'Reddedildi': 'failed', 'İhtilaflı': 'failed', 'İptal Edildi': 'cancelled', 'Askıya Alındı': 'cancelled',
  'Fatura Bekliyor': 'waiting', 'Faturası Mevcut': 'done', 'Fatura İletiliyor': 'sending', 'Fatura İletilemedi': 'failed', 'Fatura Oluşturulamadı': 'failed', 'Fatura İptal': 'cancelled',
  'Bekliyor': 'idle', 'Sırada': 'waiting', 'İşlem Tamamlanamadı': 'failed', 'Onay Bekliyor': 'waiting', 'Stok Eşlemesi Gerekli': 'failed',
}
export function RrBadge({ label, tone, glyph }: { label: string; tone?: 'bad' | 'ok' | 'warn' | 'info'; glyph?: InvoiceGlyphKind }) {
  const kind = glyph ?? stateGlyph[label] ?? (tone === 'ok' ? 'done' : tone === 'bad' ? 'failed' : tone === 'warn' ? 'waiting' : tone === 'info' ? 'sending' : 'idle')
  return <span className={`rr-badge${tone ? ` rr-${tone}` : ''}`} data-state={kind}><InvoiceGlyph kind={kind} />{label}</span>
}

// Platform wordmarks cropped to their ink and sized by the same visual area, so a wide mark (Pazarama) and a compact
// one (N11) look equally big. Crop boxes were measured on the 48px artwork in public/platforms/returns.
const marks: Record<string, { key: string; nw: number; nh: number; x0: number; y0: number; w: number; h: number; weight: number }> = {
  TRENDYOL: { key: 'trendyol', nw: 152, nh: 48, x0: 2, y0: 0, w: 144, h: 48, weight: 1.25 },
  HEPSIBURADA: { key: 'hepsiburada', nw: 145, nh: 48, x0: 0, y0: 9, w: 145, h: 26, weight: 1.08 },
  PAZARAMA: { key: 'pazarama', nw: 208, nh: 48, x0: 0, y0: 0, w: 208, h: 35, weight: 0.95 },
  N11: { key: 'n11', nw: 94, nh: 48, x0: 1, y0: 1, w: 92, h: 46, weight: 0.92 },
  SHOPIFY: { key: 'shopify', nw: 157, nh: 48, x0: 0, y0: 0, w: 157, h: 48, weight: 1 },
}
const MARK_AREA = 1350, MARK_MAX_H = 26, MARK_MAX_W = 104

export function RrWordmark({ code, name }: { code: string | null | undefined; name: string }) {
  const mark = marks[(code ?? '').toUpperCase()]
  if (!mark) return <span className="rr-wordmark-text">{name}</span>
  const ratio = mark.w / mark.h
  let height = Math.sqrt(MARK_AREA / ratio) * mark.weight
  height = Math.min(height, MARK_MAX_H, MARK_MAX_W / ratio)
  const box = { w: Math.round(height * ratio), h: Math.round(height) }
  const scale = Math.min(box.w / mark.w, box.h / mark.h)
  return <span className="rr-wordmark" role="img" aria-label={name} style={{ width: box.w, height: box.h }}>
    <img src={`/platforms/returns/mark-${mark.key}.png`} alt="" style={{ width: mark.nw * scale, height: mark.nh * scale, left: -mark.x0 * scale, top: (box.h - mark.h * scale) / 2 - mark.y0 * scale }} />
  </span>
}

// Square platform logos (popups, the platform filter).
const logoKeys: Record<string, string> = { TRENDYOL: 'trendyol', HEPSIBURADA: 'hepsiburada', PAZARAMA: 'pazarama', N11: 'n11', SHOPIFY: 'shopify' }
export function RrLogo({ code, large }: { code: string | null | undefined; large?: boolean }) {
  const key = logoKeys[(code ?? '').toUpperCase()]
  return key ? <img className={`rr-logo${large ? ' rr-lg' : ''}`} src={`/platforms/returns/icon-${key}.png`} alt="" /> : <span className={`rr-logo${large ? ' rr-lg' : ''}`} aria-hidden="true" />
}

// Closes a popover on an outside press or Escape.
export function useOutsideClose(open: boolean, ref: RefObject<HTMLElement | null>, onClose: () => void) {
  const close = useRef(onClose)
  close.current = onClose
  useEffect(() => {
    if (!open) return
    const press = (event: PointerEvent) => { if (ref.current && !ref.current.contains(event.target as Node)) close.current() }
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') close.current() }
    document.addEventListener('pointerdown', press)
    document.addEventListener('keydown', key)
    return () => { document.removeEventListener('pointerdown', press); document.removeEventListener('keydown', key) }
  }, [open, ref])
}

// One trigger with its popover beneath it.
export function RrAnchor({ trigger, children, className }: { trigger: (open: boolean, toggle: () => void) => ReactNode; children: (close: () => void) => ReactNode; className?: string }) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLSpanElement>(null)
  useOutsideClose(open, ref, () => setOpen(false))
  return <span ref={ref} className={['rr-anchor', className].filter(Boolean).join(' ')}>{trigger(open, () => setOpen(value => !value))}{open && children(() => setOpen(false))}</span>
}

// Single-choice filter popover used under table heads and filter fields.
export function RrColumnPop({ label, options, value, onPick, right, width = 240 }: { label: string; options: Array<{ value: string; label: string }>; value: string; onPick: (value: string) => void; right?: boolean; width?: number }) {
  const active = value !== 'ALL'
  return <div className={`rr-pop${right ? ' rr-right' : ''}`} role="dialog" style={{ minWidth: width }}><div className="rr-ph">{label}</div>{options.map((option, index) => { const on = active ? option.value === value : index === 0; return <button key={option.value} type="button" className={`rr-opt${on ? ' rr-on' : ''}`} role="radio" aria-checked={on} onClick={() => onPick(option.value)}><span className="rr-radio" aria-hidden="true" /><span className="rr-grow">{option.label}</span></button> })}{active && <div className="rr-foot"><span /><button type="button" className="rr-btn rr-quiet" style={{ height: 28 }} onClick={() => onPick('ALL')}>Filtreyi Temizle</button></div>}</div>
}

// Table head cell with an optional sort button and filter popover.
export function RrHeadCell({ label, active, extra, pop, sort, onSort }: { label: string; active?: boolean; extra?: ReactNode; pop?: (close: () => void) => ReactNode; sort?: 'asc' | 'desc' | null; onSort?: () => void }) {
  return <th className={active ? 'rr-has-filter' : undefined}><span className="rr-hf">{extra}{label}{onSort && <button type="button" className={`rr-sortbtn${sort ? ' rr-on' : ''}`} onClick={onSort} aria-pressed={Boolean(sort)} aria-label={`${label} sırala${sort === 'asc' ? ', düşükten yükseğe' : sort === 'desc' ? ', yüksekten düşüğe' : ''}`}><RrIcon name="sortv" /></button>}{pop && <RrAnchor trigger={(_open, toggle) => <button type="button" className="rr-fbtn" onClick={toggle} aria-label={`${label} filtresini aç`}><RrIcon name="filter" /></button>}>{pop}</RrAnchor>}</span></th>
}

// Popups: overlay, framed window with head, body and footer. Only the top window answers Escape.
const modalStack: number[] = []
let modalSeed = 0
export function RrModal({ crumb, title, desc, size, flush, head, foot, onClose, children, label, layerClass, modalClass, locked = false }: { crumb?: ReactNode; title: ReactNode; desc?: ReactNode; size?: 'sm' | 'md' | 'lg' | 'xl' | 'img'; flush?: boolean; head?: ReactNode; foot?: ReactNode; onClose: () => void; children: ReactNode; label?: string; layerClass?: string; modalClass?: string; locked?: boolean }) {
  const id = useRef(0)
  const close = useRef(onClose)
  close.current = () => { if (!locked) onClose() }
  const dialog = useRef<HTMLDivElement>(null)
  useEffect(() => {
    id.current = ++modalSeed
    modalStack.push(id.current)
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape' && modalStack[modalStack.length - 1] === id.current) { event.stopPropagation(); close.current() } }
    document.addEventListener('keydown', key)
    dialog.current?.focus()
    return () => { document.removeEventListener('keydown', key); const index = modalStack.indexOf(id.current); if (index >= 0) modalStack.splice(index, 1) }
  }, [])
  return createPortal(<div className={['rr-layer', layerClass].filter(Boolean).join(' ')}>
    <div className="rr-overlay" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close.current() }}>
      <div ref={dialog} tabIndex={-1} className={['rr-modal', size ? `rr-${size}` : '', modalClass].filter(Boolean).join(' ')} role="dialog" aria-modal="true" aria-label={label ?? (typeof title === 'string' ? title : undefined)}>
        <header className="rr-m-head"><div>{head ?? <>{crumb && <div className="rr-crumb">{crumb}</div>}<h2>{title}</h2>{desc && <p>{desc}</p>}</>}</div><button type="button" className="rr-icon-btn" onClick={() => close.current()} aria-label="Pencereyi Kapat"><RrIcon name="close" /></button></header>
        <div className={`rr-m-body${flush ? ' rr-flush' : ''}`}>{children}</div>
        {foot && <footer className="rr-m-foot">{foot}</footer>}
      </div>
    </div>
  </div>, document.body)
}

// Loading and error states inside popups (same as the panel's).
export function RrLoading({ text = 'Veriler yükleniyor…' }: { text?: string }) {
  return <div className="rr-state rr-loading" role="status" aria-live="polite"><span className="rr-mark"><img src="/pack/brand/ravencia-symbol-transparent.png" alt="" style={{ width: 30, height: 30, objectFit: 'contain' }} /></span><strong>{text}</strong><p>Bu birkaç saniye sürebilir.</p><span className="rr-bar"><i /></span></div>
}
export function RrError({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const raw = error instanceof Error ? error.message : ''
  const message = !raw || /failed to fetch|networkerror/i.test(raw) ? 'Sunucuya ulaşılamadı. İnternet bağlantınızı kontrol edip tekrar deneyin.' : raw
  return <div className="rr-state rr-error" role="alert"><span className="rr-mark"><RrIcon name="alert" /></span><strong>Veriler alınamadı</strong><p>{message}</p>{onRetry && <button type="button" className="rr-btn" style={{ marginTop: 10 }} onClick={onRetry}><RrIcon name="refresh" />Tekrar Dene</button>}</div>
}

// Every 2:3 product image: a framed button that opens the preview, with the unit count bubble.
export function RrThumb({ src, quantity, large, small, onOpen, alt }: { src: string | null | undefined; quantity?: number; large?: boolean; small?: boolean; onOpen?: () => void; alt: string }) {
  const [failed, setFailed] = useState(false)
  useEffect(() => setFailed(false), [src])
  const content = <>{src && !failed ? <img src={src} alt="" loading="lazy" decoding="async" referrerPolicy="no-referrer" onError={() => setFailed(true)} /> : null}{quantity ? <span className="rr-qb" role="img" aria-label={`${quantity} adet`}>{quantity}</span> : null}</>
  const className = `rr-thumb${large ? ' rr-lg' : ''}${small ? ' rr-sm' : ''}`
  return onOpen ? <button type="button" className={className} onClick={event => { event.preventDefault(); event.stopPropagation(); onOpen() }} aria-label={`${alt} görselini büyüt`}>{content}</button> : <span className={className} role="img" aria-label={alt}>{content}</span>
}

// Product photo preview (2:3): the title first, its codes under it; the next source is tried when one fails to load.
export function RrImagePreview({ title, crumb, sources, onClose }: { title: string; crumb?: string; sources: string[]; onClose: () => void }) {
  const [index, setIndex] = useState(0)
  const source = sources[index]
  return <RrModal crumb={crumb || undefined} title={title} size="img" onClose={onClose} foot={<><span className="rr-note" /><button type="button" className="rr-btn" onClick={onClose}>Kapat</button></>}><div className="rr-img-preview">{source ? <img src={source} alt={title} referrerPolicy="no-referrer" onError={() => setIndex(value => value + 1)} /> : <RrIcon name="image" />}</div></RrModal>
}
