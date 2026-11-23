import { useEffect, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import type { ApiAnswer } from '../../shared/api/api-answer'
import { hubPoll } from '../../shared/api'
import { queryKeys } from '../../shared/api/query-keys'
import { Busy } from '../../shared/components/AsyncState'
import { ExcelExportButton, JobListPager, jobListPageSize } from './job-list-paging'
import { isReadProblem, readHeadings, readOutcome, readStatusLabel, type JobReadSummary } from './job-read-summary'
import '../catalog/product-editor/publication-job-variants.css'
import './job-write-detail.css'

// "Çekilen Kayıtlar": the read-side counterpart of "Gönderilen Bilgiler". Each order or claim the read pulled, with its
// number, status and what the panel did with it. A job that did not record it says so plainly.
type ProductReadPage = ApiAnswer<'getJobsByIdReadRecords'>

// A product read job's records, 25 a page from the server (search, Sadece Sorunlular), with the Excel file of the same list.
// An old job stored only its first 100 records; the note says so with the real numbers instead of a "+N more" line.
function ProductReadRecords({ jobId, sentence }: { jobId: string; sentence: string }) {
  const [problemsOnly, setProblemsOnly] = useState(false)
  const [searchText, setSearchText] = useState('')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  useEffect(() => { const timeout = window.setTimeout(() => setSearch(searchText.trim()), 350); return () => window.clearTimeout(timeout) }, [searchText])
  useEffect(() => { setPage(1) }, [search, problemsOnly])
  const query = useQuery({
    queryKey: queryKeys.job(jobId, 'read-records', search, problemsOnly, page), placeholderData: keepPreviousData,
    queryFn: ({ signal }) => hubPoll<ProductReadPage>(`/jobs/${jobId}/read-records?${new URLSearchParams({ page: String(page), pageSize: String(jobListPageSize), ...(search ? { search } : {}), ...(problemsOnly ? { onlyProblems: 'true' } : {}) })}`, { signal })
  })
  const data = query.data
  return <section className="publication-job-variants publication-job-read" aria-label="Çekilen Kayıtlar">
    <div className="publication-job-variants-head">
      <h3>Çekilen Kayıtlar</h3>
      <div className="publication-job-variants-tools">
        <label><input type="checkbox" checked={problemsOnly} onChange={event => setProblemsOnly(event.target.checked)} />Sadece Sorunlular</label>
        <ExcelExportButton path={`/jobs/${jobId}/read-records/export.xlsx`} query={{ search, onlyProblems: problemsOnly ? 'true' : '' }} fallbackName={`is-${jobId}-cekilen-kayitlar.xlsx`} disabled={!data?.totalCount} />
      </div>
    </div>
    <p className="publication-job-variants-summary">{sentence}</p>
    <input className="publication-job-search" value={searchText} maxLength={100} placeholder="Model kodu veya barkod ara" aria-label="Çekilen kayıtlarda ara" onChange={event => setSearchText(event.target.value)} />
    {query.isPending ? <Busy text="Kayıtlar yükleniyor…" /> : query.isError && !data ? <p role="alert" className="publication-job-note">Kayıtlar alınamadı.</p> : !data?.items.length
      ? <p className="publication-job-note">{search || problemsOnly ? 'Aramayla ve süzgeçle eşleşen kayıt bulunamadı.' : 'Pazaryerinden bu çalışmada kayıt gelmedi.'}</p>
      : <>
        <div className="publication-job-variants-scroll"><table>
          <thead><tr><th>Model Kodu</th><th>Barkod</th><th>Varyant</th><th>Sonuç</th><th>Neden</th></tr></thead>
          <tbody>{data.items.map((record, index) => { const outcome = readOutcome(record.result); return <tr key={`${record.modelCode}-${index}`}>
            <td>{record.modelCode}</td>
            <td>{record.firstBarcode ?? '—'}{(record.barcodeCount ?? 0) > 1 ? <small className="publication-job-more"> +{((record.barcodeCount ?? 1) - 1).toLocaleString('tr-TR')}</small> : null}</td>
            <td>{record.variantCount != null ? `${record.variantCount.toLocaleString('tr-TR')} Varyant` : '—'}</td>
            <td><span className={`publication-job-variant-status is-${outcome.tone}`}>{outcome.label}</span></td>
            <td className="publication-job-variant-reason">{record.reason ?? '—'}</td>
          </tr> })}</tbody>
        </table></div>
        <JobListPager page={page} total={data.totalCount} shown={data.items.length} onPage={setPage} />
      </>}
    {data && data.omittedCount > 0 && <p className="publication-job-note">Bu işte {data.receivedCount.toLocaleString('tr-TR')} kayıt okundu; {data.omittedCount.toLocaleString('tr-TR')} kaydın ayrıntısı saklanmadı (eski iş).</p>}
  </section>
}

export function JobReadRecords({ jobId, read, recordsKept }: { jobId: string; read: JobReadSummary | null; recordsKept: boolean }) {
  const [problemsOnly, setProblemsOnly] = useState(false)
  if (read?.kind === 'PRODUCT') return <ProductReadRecords jobId={jobId} sentence={read.sentence} />
  if (!read) {
    if (!recordsKept) return null
    return <section className="publication-job-variants publication-job-read" aria-label="Çekilen Kayıtlar">
      <h3>Çekilen Kayıtlar</h3>
      <p className="publication-job-note">Bu iş kayıt bazında ayrıntı tutulmadan önce çalıştı; yalnızca toplam sayılar biliniyor.</p>
    </section>
  }
  const headings = readHeadings(read.kind)
  const problems = read.records.filter(isReadProblem).length
  const shown = problemsOnly ? read.records.filter(isReadProblem) : read.records
  return <section className="publication-job-variants publication-job-read" aria-label="Çekilen Kayıtlar">
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
