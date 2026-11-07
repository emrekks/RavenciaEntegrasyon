import { Link } from 'react-router'
import { PublicationJobVariants, type JobVariantResults } from '../catalog/product-editor/PublicationJobVariants'
import { writeModeLabel, writePhaseLabel, writeRequestLabel, type JobWriteSummary } from './job-write-summary'
import { JobWriteParts } from './JobSentDetail'
import './job-write-detail.css'

// A product job's detail: which product, what each marketplace request carried (fields and attributes), the request ids,
// and the marketplace's answer per variant. Nothing here is guessed: what the job did not store is shown as unknown.
export function JobWriteDetail({ write, variantResults, approvalCheck, running, links, note }: {
  write: JobWriteSummary | null
  variantResults: JobVariantResults | null
  approvalCheck: boolean
  running: boolean
  links: Array<{ href: string; label: string }>
  note: string | null
}) {
  const phase = running ? writePhaseLabel(write?.phase ?? null) : null
  const mode = writeModeLabel(write?.mode ?? null)
  // The barcodes a part carried are listed only when no per-variant answer shows them already.
  const showBarcodes = !variantResults || variantResults.source === 'NONE' || variantResults.items.length === 0
  return <>
    <section className="jobs-write-product" aria-labelledby="job-write-product-title">
      <div className="jobs-write-heading">
        <h3 id="job-write-product-title">{write?.productTitle ?? 'Ürün adı bilinmiyor'}</h3>
        {links.length > 0 && <nav className="jobs-reference-related-links" aria-label="İlgili Kayıtlar">{links.map(link => <Link key={link.href} to={link.href}>{link.label}</Link>)}</nav>}
      </div>
      {note && <p className="jobs-write-note">{note}</p>}
      <dl className="jobs-write-facts">
        <div><dt>Model Kodu</dt><dd>{write?.modelCodes.length ? write.modelCodes.join(', ') : '—'}</dd></div>
        {mode && <div><dt>Gönderim Şekli</dt><dd>{mode}</dd></div>}
        {phase && <div><dt>Şu Anki Adım</dt><dd>{phase}</dd></div>}
        {write?.requests.map(request => <div key={`${request.key}-${request.id}`}><dt>{writeRequestLabel(request.key)}</dt><dd className="is-code">{request.id}</dd></div>)}
        {write && write.requests.length === 0 && <div><dt>Pazaryeri İstek No</dt><dd>Kaydedilmedi</dd></div>}
      </dl>
    </section>
    {write && write.parts.length > 0 && <section className="jobs-write-parts" aria-labelledby="job-write-parts-title">
      <h3 id="job-write-parts-title">Gönderilen Bilgiler</h3>
      <p className="jobs-write-note">Her satır pazaryerine giden bir isteği gösterir; alanların güncel değerleri gönderilir.</p>
      <JobWriteParts parts={write.parts} showBarcodes={showBarcodes} />
    </section>}
    {variantResults
      ? <PublicationJobVariants results={variantResults} approvalCheck={approvalCheck} />
      : <section className="publication-job-variants" aria-label="Varyant Sonuçları"><h3>Varyant Sonuçları</h3><p className="publication-job-note">Bu iş için varyant ayrıntısı yok.</p></section>}
  </>
}
