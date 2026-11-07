import { useState } from 'react'
import { isReadProblem, readHeadings, readOutcome, readStatusLabel, type JobReadSummary } from './job-read-summary'
import '../catalog/product-editor/publication-job-variants.css'
import './job-write-detail.css'

// "Çekilen Kayıtlar": the read-side counterpart of "Gönderilen Bilgiler". Each order or claim the read pulled, with its
// number, status and what the panel did with it. A job that did not record it says so plainly.
export function JobReadRecords({ read, recordsKept }: { read: JobReadSummary | null; recordsKept: boolean }) {
  const [problemsOnly, setProblemsOnly] = useState(false)
  if (!read) {
    if (!recordsKept) return null
    return <section className="publication-job-variants" aria-label="Çekilen Kayıtlar">
      <h3>Çekilen Kayıtlar</h3>
      <p className="publication-job-note">Bu iş kayıt bazında ayrıntı tutulmadan önce çalıştı; yalnızca toplam sayılar biliniyor.</p>
    </section>
  }
  const headings = readHeadings(read.kind)
  const problems = read.records.filter(isReadProblem).length
  const shown = problemsOnly ? read.records.filter(isReadProblem) : read.records
  return <section className="publication-job-variants" aria-label="Çekilen Kayıtlar">
    <div className="publication-job-variants-head">
      <h3>Çekilen Kayıtlar</h3>
      {read.records.length > 0 && <div className="publication-job-variants-tools">
        <label><input type="checkbox" checked={problemsOnly} disabled={problems === 0} onChange={event => setProblemsOnly(event.target.checked)} />Sadece Sorunlular</label>
      </div>}
    </div>
    <p className="publication-job-variants-summary">{read.sentence}</p>
    {read.records.length === 0
      ? <p className="publication-job-note">Pazaryerinden bu çalışmada kayıt gelmedi.</p>
      : <div className="publication-job-variants-scroll">
        <table>
          <thead><tr><th>{headings.reference}</th><th>{headings.secondary}</th><th>Durum</th><th>Sonuç</th><th>Neden</th></tr></thead>
          <tbody>{shown.map((record, index) => {
            const outcome = readOutcome(record.o)
            return <tr key={`${record.r}-${index}`}>
              <td>{record.r}</td>
              <td>{record.s ?? '—'}</td>
              <td>{readStatusLabel(record.st)}</td>
              <td><span className={`publication-job-variant-status is-${outcome.tone}`}>{outcome.label}</span></td>
              <td className="publication-job-variant-reason">{record.why ?? '—'}</td>
            </tr>
          })}</tbody>
        </table>
      </div>}
    {read.omitted > 0 && <p className="publication-job-note">+{read.omitted.toLocaleString('tr-TR')} kayıt daha (sayılara dahil; listede ilk 100 kayıt, önce sorunlular gösterilir).</p>}
  </section>
}
