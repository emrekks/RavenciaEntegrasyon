import { useEffect, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { hubPoll } from '../../shared/api'
import { queryKeys } from '../../shared/api/query-keys'
import { Pager } from '../../shared/components/Pager'
import { formatDateTime } from '../../shared/format-date'
import { RrError, RrLoading, RrModal } from '../../shared/ui/rr-components'
import { jobTitle } from './job-presentation'
import { failureOutcome, failurePageSize, failureReason, failuresPath, queueWindowOptions, type JobTypeFailures, type QueueWindow } from './queue-quota'
import './monitor-detail.css'
import './queue-failures.css'

// İşlem Takibi › Kuyruk ve Kota › İş Türüne Göre Kuyruk › Hata: the failed attempts behind the number, for the same job
// type and window (GET /jobs/metrics/failures; its total is the cell's number). First the reasons with how often and
// when, then the attempts page by page, each with what became of its job and a link to its detail. Read-only.
const transientHint = 'Geçici hata; iş kendiliğinden yeniden denenir.'
const pollMs = 30_000

function OpenJob({ jobId, onOpenJob }: { jobId: string; onOpenJob?: (jobId: string) => void }) {
  return onOpenJob
    ? <button type="button" className="rr-btn rr-link" onClick={() => onOpenJob(jobId)}>İşi Aç</button>
    : <Link className="rr-btn rr-link" to={`/jobs?ara=${encodeURIComponent(jobId)}`}>İşi Aç</Link>
}

export function QueueFailuresDialog({ jobType, hours, onClose, onOpenJob }: { jobType: string; hours: QueueWindow; onClose: () => void; onOpenJob?: (jobId: string) => void }) {
  const [page, setPage] = useState(1)
  const query = useQuery({
    queryKey: queryKeys.queueQuota('failures', jobType, hours, page),
    queryFn: ({ signal }) => hubPoll<JobTypeFailures>(failuresPath(jobType, hours, page), { signal }),
    placeholderData: keepPreviousData, refetchInterval: pollMs
  })
  const data = query.data
  // The list shrank under the person (attempts left the window): a page past the last one goes back to the last real page.
  useEffect(() => { if (data && data.items.length === 0 && data.totalCount > 0 && page > 1) setPage(Math.max(1, Math.ceil(data.totalCount / failurePageSize))) }, [data, page])
  const windowLabel = queueWindowOptions.find(option => option.value === hours)?.label ?? ''
  const count = (value: number) => value.toLocaleString('tr-TR')
  const pages = data ? Math.max(1, Math.ceil(data.totalCount / failurePageSize)) : 1
  const first = data ? (data.page - 1) * data.pageSize + 1 : 0

  return <RrModal crumb="İşlem Takibi" title={`${jobTitle(jobType)} Hataları`} size="xl" modalClass="md-modal qf-modal" onClose={onClose} foot={<button type="button" className="rr-btn" onClick={onClose}>Kapat</button>}>
    {query.isPending ? <RrLoading /> : query.isError && !data ? <RrError error={query.error} onRetry={() => void query.refetch()} /> : data && <>
      <p className="rr-lead md-lead qf-lead"><strong>{windowLabel}: {count(data.totalCount)} başarısız deneme</strong><span>Tablodaki Hata sayısının denemeleri; pazaryerinin işlemesini bekleyen kontroller sayılmaz.</span></p>
      {data.totalCount === 0 ? <p className="rr-lead md-empty">Bu aralıkta başarısız deneme yok.</p> : <>
        <section className="qf-part" aria-labelledby="qf-groups">
          <h3 id="qf-groups">Sebeplere Göre</h3>
          <div className="md-wrap"><table className="md-tbl qf-tbl qf-groups"><thead><tr><th>Sebep</th><th className="qf-num">Adet</th><th>İlk Görülme</th><th>Son Görülme</th></tr></thead><tbody>
            {data.groups.map(group => <tr key={`${group.errorCode ?? ''}|${group.reason ?? ''}`}>
              <td className="qf-reason"><span>{failureReason(group.reason, group.errorCode)}</span>{group.errorCode ? <small className="qf-code">{group.errorCode}</small> : null}{group.transient ? <small className="qf-hint">{transientHint}</small> : null}</td>
              <td className="qf-num" data-label="Adet"><b>{count(group.count)}</b></td>
              <td className="qf-time" data-label="İlk Görülme">{formatDateTime(group.firstSeen)}</td>
              <td className="qf-time" data-label="Son Görülme">{formatDateTime(group.lastSeen)}</td>
            </tr>)}
          </tbody></table></div>
          {data.groupCount > data.groups.length && <p className="rr-lead qf-note">En sık {count(data.groups.length)} sebep gösteriliyor; toplam {count(data.groupCount)} farklı sebep var.</p>}
        </section>
        <section className="qf-part" aria-labelledby="qf-attempts">
          <h3 id="qf-attempts">Başarısız Denemeler</h3>
          <div className="md-wrap"><table className="md-tbl qf-tbl qf-attempts"><thead><tr><th>Zaman</th><th>Mağaza</th><th className="qf-num">Deneme</th><th>Sebep</th><th>Sonuç</th><th /></tr></thead><tbody>
            {data.items.map(item => {
              const outcome = failureOutcome(item.jobStatus)
              return <tr key={item.attemptId}>
                <td className="qf-time" data-label="Zaman">{formatDateTime(item.attemptedAt)}</td>
                <td className="qf-store" data-label="Mağaza">{item.storeName ?? '—'}</td>
                <td className="qf-num" data-label="Deneme">{count(item.attemptNumber)}.</td>
                <td className="qf-reason" data-label="Sebep"><span>{failureReason(item.reason, item.errorCode)}</span>{item.errorCode ? <small className="qf-code">{item.errorCode}</small> : null}</td>
                <td className="qf-outcome" data-label="Sonuç"><span className={`qf-badge is-${outcome.tone}`}>{outcome.label}</span></td>
                <td className="md-act"><OpenJob jobId={item.jobId} onOpenJob={onOpenJob} /></td>
              </tr>
            })}
          </tbody></table></div>
          <div className="md-foot"><span className="rr-lead">{count(data.totalCount)} denemeden {data.items.length ? `${count(first)}–${count(first + data.items.length - 1)}` : '0'} gösteriliyor</span>{pages > 1 ? <Pager page={data.page} totalPages={pages} onPageChange={setPage} /> : null}</div>
        </section>
      </>}
    </>}
  </RrModal>
}
