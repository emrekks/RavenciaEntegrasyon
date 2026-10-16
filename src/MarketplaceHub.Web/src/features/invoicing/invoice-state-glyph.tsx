import type { ReactNode } from 'react'
import { invoiceStatusLabel } from '../../shared/status-labels'

// Status glyphs of the invoices page (docs/tasarim-onerileri/faturalar.html): order states are the order's own objects
// (cart when placed, open box while packed, truck once with the carrier, green check when delivered); invoice states
// keep one ring language (clock, moving arc, check, exclamation, bar). Colours come from the theme.
export type InvoiceGlyphKind = 'placed' | 'packing' | 'carrier' | 'done' | 'waiting' | 'sending' | 'failed' | 'cancelled' | 'idle'

const line = (d: string) => <path d={d} fill="none" stroke="currentColor" strokeWidth={1.4} strokeLinecap="round" strokeLinejoin="round" />
const ring = (dashed = false) => <circle cx="8" cy="8" r="6.6" fill="none" stroke="currentColor" strokeWidth={1.4} strokeDasharray={dashed ? '2.4 2.1' : undefined} strokeOpacity={dashed ? undefined : 1} />

const glyphs: Record<InvoiceGlyphKind, ReactNode> = {
  placed: <>{line('M1.4 2.4h2.1l1.7 7.6h7.3l1.6-5.6H4.1')}<circle cx="6" cy="12.7" r="1.15" fill="currentColor" /><circle cx="11.6" cy="12.7" r="1.15" fill="currentColor" /></>,
  packing: <>{line('M2.6 6.4 8 8.8l5.4-2.4V12L8 14.4 2.6 12z')}{line('M8 8.8v5.6')}{line('M2.6 6.4 1.4 4.1 6.8 1.8 8 4.1M13.4 6.4l1.2-2.3-5.4-2.3L8 4.1')}</>,
  carrier: <>{line('M1.4 4.2h8.2v7.2H1.4z')}{line('M9.6 6.6h2.7l2.3 2.5v2.3H9.6')}<circle cx="4.4" cy="11.9" r="1.35" fill="var(--rv-color-surface)" stroke="currentColor" strokeWidth={1.3} /><circle cx="11.7" cy="11.9" r="1.35" fill="var(--rv-color-surface)" stroke="currentColor" strokeWidth={1.3} /></>,
  done: <><circle cx="8" cy="8" r="7.3" fill="currentColor" /><path d="m4.9 8.2 2.1 2.1 4.1-4.4" fill="none" stroke="var(--rv-color-surface)" strokeWidth={1.7} strokeLinecap="round" strokeLinejoin="round" /></>,
  waiting: <>{ring()}{line('M8 4.9V8l2 1.3')}</>,
  sending: <><circle cx="8" cy="8" r="6.6" fill="none" stroke="currentColor" strokeOpacity={0.38} strokeWidth={1.4} />{line('M8 1.4a6.6 6.6 0 0 1 6.6 6.6')}{line('M5.6 8h4.6M8.4 6l2 2-2 2')}</>,
  failed: <>{ring()}<path d="M8 4.6v4.2" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" /><circle cx="8" cy="11.1" r=".95" fill="currentColor" /></>,
  cancelled: <>{ring()}<path d="M5.2 8h5.6" stroke="currentColor" strokeWidth={1.5} strokeLinecap="round" /></>,
  idle: ring(true),
}

export function InvoiceGlyph({ kind }: { kind: InvoiceGlyphKind }) {
  return <svg className={`inv-glyph is-${kind}`} viewBox="0 0 16 16" aria-hidden="true">{glyphs[kind]}</svg>
}

const normalized = (value: string | null | undefined) => (value ?? '').trim().toUpperCase().replace(/[\s-]+/g, '_')

// Order (shipment) states by their code; anything else falls back on the badge tone.
export function badgeGlyphKind(value: string | null | undefined, tone: 'good' | 'warn' | 'neutral'): InvoiceGlyphKind {
  const code = normalized(value)
  if (['NEW', 'CREATED', 'WAITING_FOR_SHIPMENT', 'AWAITING', 'APPROVED'].includes(code)) return 'placed'
  if (['PICKING', 'PREPARING', 'READY_TO_SHIP', 'INVOICED', 'PACKED'].includes(code)) return 'packing'
  if (['SHIPPING', 'SHIPPED', 'IN_TRANSIT', 'AT_COLLECTION_POINT', 'RETURN_IN_TRANSIT'].includes(code)) return 'carrier'
  if (['DELIVERED', 'DELİVERED', 'COMPLETED', 'CONFIRMED', 'SUCCESS', 'INVOICED_ON_PLATFORM'].includes(code)) return 'done'
  if (['CANCELLED', 'RETURNED', 'CANCELLED_LOCAL'].includes(code)) return 'cancelled'
  if (['UNDELIVERED', 'FAILED', 'MARKETPLACE_FAILED', 'VALIDATION_FAILED', 'REJECTED'].includes(code)) return 'failed'
  if (['NOT_SENT', 'NONE', 'UNKNOWN'].includes(code)) return 'idle'
  return tone === 'good' ? 'done' : tone === 'warn' ? 'waiting' : 'idle'
}

export function invoiceGlyphKind(status: string | null | undefined): InvoiceGlyphKind {
  const label = invoiceStatusLabel(status)
  if (label === 'Fatura Bekliyor') return 'waiting'
  if (label === 'Faturası Mevcut') return 'done'
  if (label === 'Fatura İletiliyor') return 'sending'
  if (label === 'Fatura İletilemedi' || label === 'Fatura Oluşturulamadı') return 'failed'
  if (label === 'Fatura İptal') return 'cancelled'
  return 'idle'
}
