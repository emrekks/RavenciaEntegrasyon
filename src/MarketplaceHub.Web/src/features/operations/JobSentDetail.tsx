import { useState } from 'react'
import { formatDateTime } from '../../shared/format-date'
import { sendLogTriggerLabel } from './price-stock-send-log'
import { isSentProblem, sentItemStatus, sentItemSummary, writeAttributeNames, writeFieldLabels, writePartCount, writePartLabel, writePartShared, writeRequestLabel, type JobSentItem, type JobWritePart, type JobWriteSummary } from './job-write-summary'
import '../catalog/product-editor/publication-job-variants.css'
import './job-write-detail.css'

// One row per marketplace (or E-Faturam) request: its field names, how many items it carried, and "istek yapılmadı" when
// there was nothing to send. Values are never listed: only which fields went.
export function JobWriteParts({ parts, showBarcodes }: { parts: readonly JobWritePart[]; showBarcodes: boolean }) {
  return <ul>{parts.map(part => {
    const fields = writeFieldLabels(part.fields, part.clearedFields ?? [])
    const attributes = writeAttributeNames(part.attributes)
    const shared = writePartShared(part)
    return <li key={part.key} className={part.itemCount === 0 ? 'is-empty' : undefined}>
      <div className="jobs-write-part-head"><strong>{writePartLabel(part.key)}</strong><span>{writePartCount(part)}</span></div>
      {shared && <p><small>Ortak İstek</small>{shared}</p>}
      {fields.length > 0 && <p><small>Alanlar</small>{fields.join(', ')}</p>}
      {attributes.length > 0 && <p><small>Özellikler ({attributes.length})</small>{attributes.join(', ')}</p>}
      {showBarcodes && part.barcodes.length > 0 && <p><small>Barkodlar</small>{part.barcodes.join(', ')}</p>}
    </li>
  })}</ul>
}

// What a write job that is not about one product card sent: price/stock, a package action, a label, a return decision,
// an invoice. Each request with its fields, the request ids the job kept, and each item's stored answer. What the job
// did not store is shown as not recorded, never guessed.
export function JobSentDetail({ write }: { write: JobWriteSummary }) {
  const items = write.items ?? []
  const sendsNothing = write.parts.length === 0
  return <>
    <section className="jobs-write-parts" aria-labelledby="job-sent-title">
      <h3 id="job-sent-title">Gönderilen Bilgiler</h3>
      {write.note && <p className="jobs-write-note">{write.note}</p>}
      {!sendsNothing && <>
        <p className="jobs-write-note">Her satır pazaryerine giden bir isteği gösterir; değerler değil, gönderilen alanlar listelenir.</p>
        <dl className="jobs-write-facts">
          {write.trigger && <div><dt>Gönderim Nedeni</dt><dd>{sendLogTriggerLabel(write.trigger)}</dd></div>}
          {write.requests.map(request => <div key={`${request.key}-${request.id}`}><dt>{writeRequestLabel(request.key)}</dt><dd className="is-code">{request.id}</dd></div>)}
          {write.requests.length === 0 && <div><dt>İstek No</dt><dd>Kaydedilmedi</dd></div>}
        </dl>
        <JobWriteParts parts={write.parts} showBarcodes />
      </>}
    </section>
    {!sendsNothing && <JobSentResults items={items} />}
  </>
}

function JobSentResults({ items }: { items: readonly JobSentItem[] }) {
  const [problemsOnly, setProblemsOnly] = useState(false)
  if (items.length === 0) return <section className="publication-job-variants" aria-label="Gönderim Sonuçları">
    <h3>Gönderim Sonuçları</h3>
    <p className="publication-job-note">Bu iş için kalem bazında sonuç kaydı yok.</p>
  </section>
  const problems = items.filter(isSentProblem).length
  const shown = problemsOnly ? items.filter(isSentProblem) : items
  return <section className="publication-job-variants" aria-label="Gönderim Sonuçları">
    <div className="publication-job-variants-head">
      <h3>Gönderim Sonuçları</h3>
      <div className="publication-job-variants-tools">
        <label><input type="checkbox" checked={problemsOnly} disabled={problems === 0} onChange={event => setProblemsOnly(event.target.checked)} />Sadece Sorunlular</label>
      </div>
    </div>
    <p className="publication-job-variants-summary">{sentItemSummary(items)}</p>
    <div className="publication-job-variants-scroll">
      <table>
        <thead><tr><th>Kalem</th><th>Gönderilen</th><th>Durum</th><th>Neden</th></tr></thead>
        <tbody>{shown.map((item, index) => {
          const status = sentItemStatus(item.status)
          return <tr key={`${item.reference}-${index}`}>
            <td>{item.reference}</td>
            <td>{item.detail ?? '—'}</td>
            <td><span className={`publication-job-variant-status is-${status.tone}`} title={item.at ? formatDateTime(item.at) : undefined}>{status.label}</span></td>
            <td className="publication-job-variant-reason">{item.reason ?? '—'}</td>
          </tr>
        })}</tbody>
      </table>
    </div>
  </section>
}
