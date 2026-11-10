import { useEffect, useState } from 'react'
import { UiIcon } from './UiIcon'

// The one pagination of the panel (the "rr" look of Ürünler, Siparişler and İadeler): numbered pages, previous and next
// arrows, and "Sayfaya Git" with a Git button. Pages are 1-based. hasNext lets a cursor-based list say that a page
// after the last known one exists.
export function pagerPages(page: number, totalPages: number): Array<number | null> {
  if (totalPages <= 7) return Array.from({ length: totalPages }, (_, index) => index + 1)
  const pages = new Set([1, totalPages, page - 1, page, page + 1].filter(value => value >= 1 && value <= totalPages))
  const sorted = [...pages].sort((left, right) => left - right)
  return sorted.flatMap((value, index) => index > 0 && value - sorted[index - 1] > 1 ? [null, value] : [value])
}

export function Pager({ page, totalPages, hasNext, onPageChange, onPrevious, onNext, disabled = false, className, ariaLabel = 'Sayfalama' }: { page: number; totalPages: number; hasNext?: boolean; onPageChange: (page: number) => void; onPrevious?: () => void; onNext?: () => void; disabled?: boolean; className?: string; ariaLabel?: string }) {
  const safeTotal = Math.max(1, totalPages)
  const safePage = Math.min(Math.max(1, page), safeTotal)
  const [draft, setDraft] = useState('')
  useEffect(() => setDraft(''), [safePage])
  const go = (next: number) => { if (Number.isFinite(next) && next >= 1) onPageChange(Math.min(safeTotal, Math.max(1, Math.floor(next)))) }
  const jump = () => { go(Number(draft)); setDraft('') }
  return <nav className={['rr', 'rr-pager', 'rr-pager-bare', className].filter(Boolean).join(' ')} aria-label={ariaLabel}>
    <div className="rr-pages">
      <button type="button" aria-label="Önceki" disabled={disabled || safePage <= 1} onClick={() => (onPrevious ? onPrevious() : go(safePage - 1))}><UiIcon name="chevronLeft" size={16} /></button>
      {pagerPages(safePage, safeTotal).map((value, index) => value === null
        ? <span key={`gap-${index}`} className="rr-pages-gap" aria-hidden="true">…</span>
        : <button key={value} type="button" className={value === safePage ? 'rr-on' : undefined} aria-current={value === safePage ? 'page' : undefined} disabled={disabled} onClick={() => go(value)}>{value}</button>)}
      <button type="button" aria-label="Sonraki" disabled={disabled || !(hasNext ?? safePage < safeTotal)} onClick={() => (onNext ? onNext() : go(safePage + 1))}><UiIcon name="chevronRight" size={16} /></button>
    </div>
    <label className="rr-jump"><span className="rr-jump-label">Sayfaya Git</span><input className="rr-in" type="number" inputMode="numeric" min={1} max={safeTotal} placeholder={`1–${safeTotal}`} aria-label="Sayfa numarası" value={draft} disabled={disabled} onChange={event => setDraft(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); jump() } }} /><button type="button" className="rr-btn" disabled={disabled} onClick={jump}>Git</button></label>
  </nav>
}
