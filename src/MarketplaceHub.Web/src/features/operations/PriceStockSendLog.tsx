import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { Button, UiIcon } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { formatIstanbul } from '../../shared/format-date'
import { RrIcon, RrLogo } from '../marketplace/returns-ui'
import { marketplacePlatformLabel } from '../marketplace/marketplace-platform-support'
import { jobTimeRanges } from './job-list-query'
import { JobStateGlyph } from './job-state-glyph'
import { ChoiceField, FilterMenu, OpsFilterCard, SearchField, type FilterMenuGroup } from './filter-fields'
import {
  emptySendLogFilters, hasSendLogFilters, listPriceChanged, priceChange, quantityChange, sendLogCountsQueryString, sendLogFilterCount,
  sendLogQueryString, sendLogResultGroups, sendLogResultLabel, sendLogResultTone, sendLogResultTooltip, sendLogSummary, sendLogSummaryCounts,
  sendLogSummaryOn, sendLogTriggerGroups, sendLogTriggerLabel, toggleSendLogSummary,
  type PriceStockSendLogEntry, type PriceStockSendLogPage, type SendLogFilters, type ValueChange
} from './price-stock-send-log'
import { ProductThumb } from './ProductThumb'
import './price-stock-send-log.css'

// İşlem Takibi › Loglar: every price/stock line sent to a marketplace, newest first, like an API log. The first page is
// polled every 30 seconds while it is the only one loaded; older pages are read on demand.
const firstPagePollMs = 30_000
const searchDelayMs = 350

const timeOptions: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }
const dayOptions: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric' }

function ChangeText({ value, kind }: { value: ValueChange; kind: 'Stok' | 'Fiyat' }) {
  if (value.omitted) return <span className="psl-change is-omitted" title={`Bu gönderimde ${kind.toLocaleLowerCase('tr-TR')} yoktu.`}>Gönderilmedi</span>
  return <span className={`psl-change${value.changed ? '' : ' is-same'}`}>{value.text}</span>
}

function ResultPill({ entry }: { entry: PriceStockSendLogEntry }) {
  const tooltip = sendLogResultTooltip(entry)
  const tone = sendLogResultTone(entry.result)
  return <span className={`psl-result is-${tone}`} title={tooltip}>
    <i aria-hidden="true" />{sendLogResultLabel(entry.result)}
    <span className="sr-only">{` — ${tooltip}`}</span>
  </span>
}

function ProductCell({ entry }: { entry: PriceStockSendLogEntry }) {
  const codes = [entry.barcode, entry.stockCode && entry.stockCode !== entry.barcode ? entry.stockCode : null].filter(Boolean).join(' · ')
  return <div className="psl-product">
    <ProductThumb url={entry.imageUrl} title={entry.productTitle ?? 'Ürün'} />
    <span className="psl-product-text">
      <strong title={entry.productTitle ?? undefined}>{entry.productTitle ?? 'Ürün bilgisi yok'}</strong>
      <small>{codes}{entry.modelCode ? ` · ${entry.modelCode}` : ''}</small>
    </span>
  </div>
}

const triggerGroups: FilterMenuGroup[] = sendLogTriggerGroups.map(group => ({ label: group.label, options: group.values.map(value => ({ value, label: sendLogTriggerLabel(value) })) }))

// The filter card of the log: the search, the range with the job list's presets (chosen days override it), the days,
// Platform, Sonuç (its group headings pick a whole group, each value shows its count) and Tetikleyen. The log keeps its
// own range and filters; the job list's stay as they were.
function SendLogFilterCard({ filters, searchText, onSearch, platformGroups, resultCounts, onChange, onClear, onApply, clearCount }: { filters: SendLogFilters; searchText: string; onSearch: (value: string) => void; platformGroups: FilterMenuGroup[]; resultCounts: ReadonlyArray<{ result: string; count: number }> | undefined; onChange: (patch: Partial<SendLogFilters>) => void; onClear: () => void; onApply: () => void; clearCount: number }) {
  const days = Boolean(filters.from || filters.to)
  const resultGroups: FilterMenuGroup[] = sendLogResultGroups.map(group => ({ label: group.label, options: group.values.map(value => ({ value, label: sendLogResultLabel(value), count: resultCounts ? resultCounts.filter(row => row.result === value).reduce((total, row) => total + row.count, 0) : null })) }))
  return <OpsFilterCard wide label="Gönderim kaydı filtreleri" active={sendLogFilterCount({ ...filters, search: '' })} clearCount={clearCount} onClear={onClear} onApply={onApply}>
    <SearchField label="Ürün Ara" ariaLabel="Gönderim kayıtlarında ara" placeholder="Barkod, stok kodu, model kodu veya ürün adı" value={searchText} onChange={value => onSearch(value.slice(0, 100))} onEnter={onApply} />
    <ChoiceField name="Zaman Aralığı" value={days ? '' : filters.range} label={days ? 'Seçili Tarihler' : undefined} options={jobTimeRanges.map(([value, label]) => ({ value, label }))} onChange={value => onChange({ range: value as SendLogFilters['range'], from: '', to: '' })} />
    <DateRangePicker className="rr-fld rr-date" label="Gönderim Tarihi" placeholder="Başlangıç – Bitiş" from={filters.from} to={filters.to} onChange={range => onChange({ from: range.from, to: range.to })} icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />
    <FilterMenu name="Platform" allLabel="Tüm Platformlar" unit="Platform" groups={platformGroups} selected={filters.platforms} onChange={platforms => onChange({ platforms })} emptyText="Bağlı pazaryeri yok." />
    <FilterMenu name="Sonuç" allLabel="Tüm Sonuçlar" unit="Sonuç" width={280} groups={resultGroups} selected={filters.results} onChange={results => onChange({ results })} />
    <FilterMenu name="Tetikleyen" allLabel="Tümü" unit="Tetikleyen" width={280} groups={triggerGroups} selected={filters.triggers} onChange={triggers => onChange({ triggers })} />
  </OpsFilterCard>
}

export type KeptSendLogView = { filters: SendLogFilters; searchText: string; oldestFirst: boolean }

// kept holds the filters while another log view is shown, so switching views and back keeps them.
export function PriceStockSendLog({ platformOptions, onOpenJob, kept }: { platformOptions: string[]; onOpenJob: (jobId: string) => void; kept?: { current: KeptSendLogView | null } }) {
  const [filters, setFilters] = useState<SendLogFilters>(() => kept?.current?.filters ?? emptySendLogFilters)
  const [searchText, setSearchText] = useState(() => kept?.current?.searchText ?? '')
  // Zaman is the one order the server pages by: newest first, or oldest first.
  const [oldestFirst, setOldestFirst] = useState(() => kept?.current?.oldestFirst ?? false)
  useEffect(() => { if (kept) kept.current = { filters, searchText, oldestFirst } }, [kept, filters, searchText, oldestFirst])
  // The search goes to the server once typing pauses, not on every key.
  useEffect(() => {
    const timeout = window.setTimeout(() => setFilters(current => current.search === searchText ? current : { ...current, search: searchText }), searchDelayMs)
    return () => window.clearTimeout(timeout)
  }, [searchText])
  const update = (patch: Partial<SendLogFilters>) => setFilters(current => ({ ...current, ...patch }))
  const clear = () => { setSearchText(''); setFilters(emptySendLogFilters) }
  // Filtrele (and Enter in the search) sends the typed search at once instead of after the typing pause.
  const apply = () => setFilters(current => current.search === searchText ? current : { ...current, search: searchText })

  // The summary counts every result under the other filters, so a picked group keeps the rest of the counts in view.
  const countFilters = useMemo(() => ({ ...filters, results: [] }), [filters])
  const counts = useQuery({
    queryKey: ['price-stock-send-log', 'counts', countFilters],
    queryFn: () => hubPoll<Array<{ result: string; count: number }>>(`/price-stock-send-log/counts${sendLogCountsQueryString(countFilters)}`),
    placeholderData: keepPreviousData,
    refetchInterval: firstPagePollMs
  })
  const summary = useMemo(() => counts.data ? sendLogSummaryCounts(counts.data) : null, [counts.data])

  const list = useInfiniteQuery({
    queryKey: ['price-stock-send-log', filters, oldestFirst],
    queryFn: context => hubPoll<PriceStockSendLogPage>(`/price-stock-send-log${sendLogQueryString(filters, context.pageParam, undefined, oldestFirst)}`),
    initialPageParam: null as string | null,
    getNextPageParam: lastPage => lastPage.hasMore && lastPage.nextCursor ? lastPage.nextCursor : undefined,
    placeholderData: keepPreviousData,
    // Only the newest page can gain rows, so only it is polled.
    refetchInterval: query => !oldestFirst && (query.state.data?.pages.length ?? 0) <= 1 ? firstPagePollMs : false
  })
  const rows = useMemo(() => {
    const seen = new Set<string>()
    return (list.data?.pages ?? []).flatMap(page => page.items).filter(entry => !seen.has(entry.id) && Boolean(seen.add(entry.id)))
  }, [list.data])
  const filtered = hasSendLogFilters(filters)
  // The typed search counts as soon as it is typed, before it reaches the server.
  const filterCount = sendLogFilterCount({ ...filters, search: searchText })
  const platformGroups = useMemo<FilterMenuGroup[]>(() => [{ options: [...new Set([...platformOptions.filter(code => code !== 'TRENDYOL_EFATURAM'), ...filters.platforms])]
    .map(code => ({ value: code, label: marketplacePlatformLabel(code), mark: <RrLogo code={code} /> })) }], [platformOptions, filters.platforms])

  // A summary group toggles its results in the Sonuç filter; several can be on at once, like the job list's counts.
  const summaryProps = (values: string[], glyph: string) => {
    const on = sendLogSummaryOn(filters.results, values)
    const toggle = () => update({ results: toggleSendLogSummary(filters.results, values) })
    return { role: 'button', tabIndex: 0, 'aria-pressed': on, className: `${glyph}${on ? ' is-on' : ''}`, onClick: toggle, onKeyDown: (event: React.KeyboardEvent) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); toggle() } } } as const
  }

  return <div className="psl">
    <SendLogFilterCard filters={filters} searchText={searchText} onSearch={setSearchText} platformGroups={platformGroups} resultCounts={counts.data} onChange={update} onClear={clear} onApply={apply} clearCount={filterCount} />
    <div className="jobs-reference-toolbar psl-toolbar">
      <div className="jobs-reference-status-summary" aria-label="Gönderim sonuç özeti">
        {sendLogSummary.map(group => <span key={group.key} {...summaryProps(group.values, group.glyph)} title={group.values.map(sendLogResultLabel).join(', ')}>
          <JobStateGlyph tone={group.glyph} />{group.label} <strong>{summary ? summary[group.key].toLocaleString('tr-TR') : '—'}</strong>
        </span>)}
      </div>
      <div className="jobs-reference-toolbar-actions">
        <button type="button" className="jobs-reference-refresh" title="Yenile" aria-label="Gönderim kayıtlarını yenile" onClick={() => { void list.refetch(); void counts.refetch() }}><UiIcon name="refresh" size={16} /></button>
      </div>
    </div>
    {list.isLoading ? <Busy text="Gönderim kayıtları yükleniyor…" /> : list.isError && !list.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">Gönderim kayıtları alınamadı.{list.error instanceof Error && list.error.message ? ` ${list.error.message}` : ''}</div> : <>
      <div className="psl-table-scroll"><table className="psl-table">
        <thead><tr><th scope="col" aria-sort={oldestFirst ? 'ascending' : 'descending'}><button type="button" className="psl-sort is-on" onClick={() => setOldestFirst(current => !current)} title={oldestFirst ? 'Yeniden eskiye sırala' : 'Eskiden yeniye sırala'}>Zaman<span aria-hidden="true">{oldestFirst ? '↑' : '↓'}</span></button></th><th>Platform</th><th>Ürün</th><th>Stok</th><th>Fiyat</th><th>Tetikleyen</th><th>Sonuç</th><th><span className="sr-only">İş</span></th></tr></thead>
        <tbody>
          {rows.map(entry => {
            const sale = priceChange(entry.previousSalePrice, entry.newSalePrice, entry.currency)
            const listPrice = listPriceChanged(entry) ? priceChange(entry.previousListPrice, entry.newListPrice, entry.currency) : null
            const store = entry.storeName || marketplacePlatformLabel(entry.platformCode)
            return <tr key={entry.id} className={`psl-row is-${sendLogResultTone(entry.result)}`}>
              <td className="psl-time"><strong>{formatIstanbul(entry.createdAt, timeOptions)}</strong><small>{formatIstanbul(entry.createdAt, dayOptions)}</small></td>
              <td className="psl-platform"><PlatformSquareMark code={entry.platformCode} name={store} /><span title={store}>{store}</span></td>
              <td className="psl-product-cell"><ProductCell entry={entry} /></td>
              <td className="psl-stock" data-label="Stok"><ChangeText value={quantityChange(entry.previousQuantity, entry.newQuantity)} kind="Stok" /></td>
              <td className="psl-price" data-label="Fiyat"><ChangeText value={sale} kind="Fiyat" />{listPrice && !listPrice.omitted && <small className="psl-list-price">Liste: {listPrice.text}</small>}</td>
              <td className="psl-trigger" data-label="Tetikleyen">{sendLogTriggerLabel(entry.trigger)}</td>
              <td className="psl-result-cell"><ResultPill entry={entry} /></td>
              <td className="psl-job"><button type="button" className="psl-job-link" onClick={() => onOpenJob(entry.jobId)} aria-label={`${entry.barcode} gönderiminin işini aç`}>İşi Aç<UiIcon name="arrowRight" size={14} /></button></td>
            </tr>
          })}
          {rows.length === 0 && <tr><td className="psl-empty" colSpan={8}>{filtered
            ? 'Seçili filtrelerle eşleşen gönderim kaydı bulunamadı.'
            : <><strong>Henüz gönderim kaydı yok.</strong><span>Pazaryerine fiyat veya stok gönderildiğinde her satır burada görünür; panelden yapılan ve otomatik gönderimler burada listelenir. Otomatik yazmanın her bağlantıda açık olup olmadığını API Sağlık gösterir.</span></>}</td></tr>}
        </tbody>
      </table></div>
      {rows.length > 0 && <div className="jobs-reference-pagination">
        <strong>{list.hasNextPage ? `${rows.length.toLocaleString('tr-TR')} kayıt yüklendi, daha eskileri var` : `Toplam ${rows.length.toLocaleString('tr-TR')} kayıt`}</strong>
        {list.hasNextPage && <div className="jobs-reference-pagination-actions"><Button variant="secondary" className="jobs-reference-load-more" loading={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>Daha Fazla Yükle</Button></div>}
      </div>}
    </>}
  </div>
}

export default PriceStockSendLog
