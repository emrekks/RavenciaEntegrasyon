import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { queryKeys } from '../../shared/api/query-keys'
import { Button } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { formatIstanbul } from '../../shared/format-date'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { marketplacePlatformLabel } from '../marketplace/marketplace-platform-support'
import { ProductThumb } from './ProductThumb'
import {
  formatCount, isProductImportJob, jobChangeCountItems, jobChangeDifference, jobChangeKindLabel, jobChangeResult, jobChangeSourceLabel, jobChangeValues,
  jobChangeVariant, jobChangesOutcomeText, jobChangesQueryString, spawnedJobStatusLabel,
  type JobChangeLine, type JobChangesPage, type JobChangesSummary
} from './job-changes'
import type { ValueChange } from './price-stock-send-log'
import './job-changes.css'

// İş detayı › Gidenler ve Değişenler: the lines a price/stock job sent (old → new, the marketplace's answer) or the
// variants a product import changed in the panel. Display only: it sends nothing and changes no setting.
const searchDelayMs = 350
const timeOptions: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }

function ValueText({ value, difference }: { value: ValueChange; difference: string | null }) {
  if (value.omitted) return <span className="jc-value is-omitted">Gönderilmedi</span>
  return <span className={`jc-value${value.changed ? '' : ' is-same'}`}>{value.text}{difference && <b className={`jc-diff${difference.startsWith('+') ? ' is-up' : ' is-down'}`}>{difference}</b>}</span>
}

function LineRow({ line, orderNumber }: { line: JobChangeLine; orderNumber: string | null | undefined }) {
  const values = jobChangeValues(line)
  const result = jobChangeResult(line)
  const variant = jobChangeVariant(line)
  const codes = [line.modelCode && `Model: ${line.modelCode}`, line.barcode && `Barkod: ${line.barcode}`].filter(Boolean).join(' · ')
  const store = line.storeName || (line.platformCode ? marketplacePlatformLabel(line.platformCode) : null)
  return <li className={`jc-line is-${result.tone}`}>
    <div className="jc-product">
      <ProductThumb url={line.imageUrl} title={line.productTitle ?? 'Ürün'} />
      <span className="jc-product-text">
        <strong title={line.productTitle ?? undefined}>{line.productTitle ?? 'Ürün bilgisi yok'}</strong>
        {codes && <small title={codes}>{codes}</small>}
        {variant && <small>{variant}</small>}
      </span>
    </div>
    <div className="jc-kind" data-label="Tür">{jobChangeKindLabel(line.kind)}</div>
    <div className="jc-values" data-label="Eski → Yeni">
      {values.stock && <span><small>Stok</small><ValueText value={values.stock} difference={jobChangeDifference(line.previousQuantity, line.newQuantity)} /></span>}
      {values.price && <span><small>Fiyat</small><ValueText value={values.price} difference={jobChangeDifference(line.previousSalePrice, line.newSalePrice, true)} /></span>}
      {values.listPrice && !values.listPrice.omitted && <span><small>Liste</small><ValueText value={values.listPrice} difference={null} /></span>}
    </div>
    <div className="jc-source" data-label="Neden">
      <span>{jobChangeSourceLabel(line, orderNumber)}</span>
      {store && <small>{line.platformCode && <PlatformSquareMark code={line.platformCode} name={store} size="sm" />}{store}</small>}
    </div>
    <div className="jc-result" data-label="Sonuç">
      <span className={`jc-pill is-${result.tone}`}><i aria-hidden="true" />{result.label}</span>
      {result.detail && <small title={result.detail}>{result.detail}</small>}
    </div>
    <div className="jc-time" data-label="Zaman">{formatIstanbul(line.occurredAt, timeOptions)}</div>
  </li>
}

export function JobChanges({ jobId, jobType, onOpenJob }: { jobId: string; jobType: string; onOpenJob?: (jobId: string) => void }) {
  const productImport = isProductImportJob(jobType)
  const [searchText, setSearchText] = useState('')
  const [search, setSearch] = useState('')
  useEffect(() => {
    const timeout = window.setTimeout(() => setSearch(searchText.trim()), searchDelayMs)
    return () => window.clearTimeout(timeout)
  }, [searchText])

  const summary = useQuery({
    queryKey: queryKeys.job(jobId, 'changes', 'summary'),
    queryFn: ({ signal }) => hubPoll<JobChangesSummary>(`/jobs/${jobId}/changes/summary`, { signal })
  })
  const list = useInfiniteQuery({
    queryKey: queryKeys.job(jobId, 'changes', search),
    queryFn: context => hubPoll<JobChangesPage>(`/jobs/${jobId}/changes${jobChangesQueryString(search, context.pageParam)}`, { signal: context.signal }),
    initialPageParam: null as string | null,
    getNextPageParam: lastPage => lastPage.hasMore && lastPage.nextCursor ? lastPage.nextCursor : undefined,
    placeholderData: keepPreviousData
  })
  const lines = useMemo(() => {
    const seen = new Set<string>()
    return (list.data?.pages ?? []).flatMap(page => page.items).filter(line => !seen.has(line.id) && Boolean(seen.add(line.id)))
  }, [list.data])

  const head = summary.data
  const counts = head ? jobChangeCountItems(head) : []
  const outcome = jobChangesOutcomeText(head?.outcome, productImport)
  const spawned = head?.spawnedJobs ?? []
  const searching = Boolean(search)
  return <section className="jobs-reference-change-summary jc" aria-labelledby="job-changes-title">
    <div className="jc-heading">
      <div><span className="jobs-reference-section-kicker">{productImport ? 'Panelde Değişenler' : 'Pazaryerine Gidenler'}</span><h3 id="job-changes-title">Gidenler ve Değişenler</h3></div>
      {head?.sourceOrderNumber && <span className="jc-origin">Kaynak: Sipariş #{head.sourceOrderNumber}</span>}
    </div>
    {summary.isLoading ? <Busy text="Özet yükleniyor…" /> : summary.isError ? <p role="alert" className="jc-note is-error">Özet alınamadı.</p> : <>
      <ul className="jc-counts" aria-label="Gidenler ve değişenler özeti">
        {counts.map(item => <li key={item.key}><small>{item.label}</small><strong>{formatCount(item.value)}</strong></li>)}
      </ul>
      {outcome && <p className="jc-note" role="status">{outcome}</p>}
      {productImport && <p className="jc-hint">Liste, bu aktarımın çalıştığı süre içinde aynı mağazadan panele işlenen stok ve mağaza fiyatı değişikliklerinden oluşur (Ürün Hareketleri).</p>}
      {spawned.length > 0 && <nav className="jc-spawned" aria-label="Doğan gönderim işleri">
        <small>Doğan Gönderim İşleri</small>
        {spawned.map(job => <button key={job.id} type="button" className="jc-spawned-link" onClick={() => onOpenJob?.(job.id)} disabled={!onOpenJob}>
          Fiyat-Stok Gönderimi · {spawnedJobStatusLabel(job.status)} · {formatIstanbul(job.createdAt, timeOptions)}
        </button>)}
      </nav>}
    </>}
    <label className="jc-search">
      <span className="sr-only">Satırlarda ara</span>
      <input value={searchText} maxLength={100} placeholder="Barkod, stok kodu veya model kodu ara" onChange={event => setSearchText(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') setSearch(searchText.trim()) }} />
    </label>
    {list.isLoading ? <Busy text="Satırlar yükleniyor…" /> : list.isError && !list.data ? <p role="alert" className="jc-note is-error">Satırlar alınamadı.</p> : <>
      {lines.length > 0 ? <>
        <div className="jc-columns" aria-hidden="true"><span>Ürün</span><span>Tür</span><span>Eski → Yeni</span><span>Neden</span><span>Sonuç</span><span>Zaman</span></div>
        <ul className="jc-lines">{lines.map(line => <LineRow key={line.id} line={line} orderNumber={head?.sourceOrderNumber} />)}</ul>
      </> : <p className="jc-empty">{searching ? 'Aramayla eşleşen satır bulunamadı.' : outcome ? 'Bu işte listelenecek satır yok.' : 'Bu iş için kayıtlı satır bulunamadı.'}</p>}
      {lines.length > 0 && <div className="jc-footer">
        <small>{list.hasNextPage ? `${lines.length.toLocaleString('tr-TR')} satır gösteriliyor, devamı var` : `Toplam ${lines.length.toLocaleString('tr-TR')} satır`}</small>
        {list.hasNextPage && <Button variant="secondary" className="jc-more" loading={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>Sonraki 50 Satır</Button>}
      </div>}
    </>}
  </section>
}

export default JobChanges
