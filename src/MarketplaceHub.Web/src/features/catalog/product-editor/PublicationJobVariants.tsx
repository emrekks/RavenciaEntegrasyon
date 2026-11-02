import { useState } from 'react'
import { copyText } from '../../../shared/copy-text'

export type JobVariantResult = { barcode: string | null; stockCode: string | null; options: string | null; status: string; reason: string | null }
export type JobVariantResults = { source: 'RECORDED' | 'CARD_STATE' | 'NONE' | string; items: JobVariantResult[] }

const statusLabels: Record<string, string> = { SENT: 'Gönderildi', APPROVED: 'Onaylandı', REJECTED: 'Reddedildi', NOT_SENT: 'Gönderilmedi', PENDING: 'Bekliyor' }
const statusTones: Record<string, string> = { SENT: 'info', APPROVED: 'success', REJECTED: 'danger', NOT_SENT: 'neutral', PENDING: 'warning' }

export const variantResultLabel = (status: string) => statusLabels[status] ?? status
export const isProblemVariant = (item: JobVariantResult) => item.status === 'REJECTED' || item.status === 'NOT_SENT'

// The reason column: the marketplace's (or our) reason, and for a refused or unsent variant without one, that it is unknown.
export function variantResultReason(item: JobVariantResult) {
  if (item.reason) return item.reason
  return isProblemVariant(item) ? 'neden bilinmiyor' : null
}

// "18 varyant gönderildi · 18 onaylandı · 0 reddedildi · 0 bekliyor"; an approval check names all three outcomes, a
// send names its refusals and what else it met. Variants the job did not carry close the line.
export function variantResultSummary(items: JobVariantResult[], approvalCheck: boolean) {
  const count = (status: string) => items.filter(item => item.status === status).length
  const notSent = count('NOT_SENT')
  const parts = [`${items.length - notSent} varyant gönderildi`]
  const approved = count('APPROVED'); const rejected = count('REJECTED'); const pending = count('PENDING')
  if (approvalCheck || approved) parts.push(`${approved} onaylandı`)
  parts.push(`${rejected} reddedildi`)
  if (approvalCheck || pending) parts.push(`${pending} bekliyor`)
  if (notSent) parts.push(`${notSent} gönderilmedi`)
  return parts.join(' · ')
}

export const rejectedBarcodes = (items: JobVariantResult[]) =>
  items.filter(item => item.status === 'REJECTED').map(item => item.barcode ?? item.stockCode).filter((code): code is string => Boolean(code))

// The job's answer per variant: barcode, stock code, options, outcome and the reason for what did not go through.
export function PublicationJobVariants({ results, approvalCheck }: { results: JobVariantResults; approvalCheck: boolean }) {
  const [problemsOnly, setProblemsOnly] = useState(false)
  const [copied, setCopied] = useState<'ok' | 'failed' | null>(null)
  const items = results.items
  if (results.source === 'NONE' || items.length === 0) return <section className="publication-job-variants" aria-label="Varyant Sonuçları">
    <h3>Varyant Sonuçları</h3>
    <p className="publication-job-note">Bu iş için varyant ayrıntısı yok.</p>
  </section>
  const problems = items.filter(isProblemVariant).length
  const shown = problemsOnly ? items.filter(isProblemVariant) : items
  const rejected = rejectedBarcodes(items)
  const copy = async () => setCopied(await copyText(rejected.join('\n')) ? 'ok' : 'failed')
  return <section className="publication-job-variants" aria-label="Varyant Sonuçları">
    <div className="publication-job-variants-head">
      <h3>Varyant Sonuçları</h3>
      <div className="publication-job-variants-tools">
        <label><input type="checkbox" checked={problemsOnly} disabled={problems === 0} onChange={event => setProblemsOnly(event.target.checked)} />Sadece Sorunlular</label>
        {rejected.length > 0 && <button type="button" className="secondary" onClick={() => void copy()}>{copied === 'ok' ? 'Kopyalandı' : copied === 'failed' ? 'Kopyalanamadı' : 'Reddedilen Barkodları Kopyala'}</button>}
      </div>
    </div>
    <p className="publication-job-variants-summary">{variantResultSummary(items, approvalCheck)}</p>
    {results.source === 'CARD_STATE' && <p className="publication-job-note">Bu iş sonuçlarını kaydetmeden önce çalıştı; durumlar kartın bu yayına ait kayıtlı durumundan okundu.</p>}
    <div className="publication-job-variants-scroll">
      <table>
        <thead><tr><th>Barkod</th><th>Stok Kodu</th><th>Seçenek</th><th>Durum</th><th>Neden</th></tr></thead>
        <tbody>{shown.map((item, index) => <tr key={`${item.barcode ?? item.stockCode ?? ''}-${index}`}>
          <td>{item.barcode ?? '—'}</td>
          <td>{item.stockCode ?? '—'}</td>
          <td>{item.options ?? '—'}</td>
          <td><span className={`publication-job-variant-status is-${statusTones[item.status] ?? 'info'}`}>{variantResultLabel(item.status)}</span></td>
          <td className="publication-job-variant-reason">{variantResultReason(item) ?? '—'}</td>
        </tr>)}</tbody>
      </table>
    </div>
  </section>
}
