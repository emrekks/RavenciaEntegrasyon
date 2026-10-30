import { useEffect, useMemo, useRef, useState } from 'react'
import { keepPreviousData, useInfiniteQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { Button, UiIcon } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { formatIstanbul } from '../../shared/format-date'
import { useDismissable } from '../../shared/hooks/useDismissable'
import { marketplacePlatformLabel } from '../marketplace/marketplace-platform-support'
import {
  emptySendLogFilters, hasSendLogFilters, listPriceChanged, priceChange, quantityChange, sendLogQueryString, sendLogResultLabel,
  sendLogResultOptions, sendLogResultTone, sendLogResultTooltip, sendLogTriggerLabel, sendLogTriggerOptions,
  type PriceStockSendLogEntry, type PriceStockSendLogPage, type SendLogFilters, type ValueChange
} from './price-stock-send-log'
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
  const [broken, setBroken] = useState(false)
  const codes = [entry.barcode, entry.stockCode && entry.stockCode !== entry.barcode ? entry.stockCode : null].filter(Boolean).join(' · ')
  return <div className="psl-product">
    {entry.imageUrl && !broken
      ? <img src={entry.imageUrl} alt="" loading="lazy" onError={() => setBroken(true)} />
      : <span className="psl-product-placeholder" aria-hidden="true"><UiIcon name="image" size={16} /></span>}
    <span className="psl-product-text">
      <strong title={entry.productTitle ?? undefined}>{entry.productTitle ?? 'Ürün bilgisi yok'}</strong>
      <small>{codes}{entry.modelCode ? ` · ${entry.modelCode}` : ''}</small>
    </span>
  </div>
}

export function PriceStockSendLog({ platformOptions, onOpenJob }: { platformOptions: string[]; onOpenJob: (jobId: string) => void }) {
  const [filters, setFilters] = useState<SendLogFilters>(emptySendLogFilters)
  const [searchText, setSearchText] = useState('')
  const [platformOpen, setPlatformOpen] = useState(false)
  const platformWrap = useRef<HTMLDivElement>(null)
  useDismissable(platformOpen, [platformWrap], () => setPlatformOpen(false))
  // The search goes to the server once typing pauses, not on every key.
  useEffect(() => {
    const timeout = window.setTimeout(() => setFilters(current => current.search === searchText ? current : { ...current, search: searchText }), searchDelayMs)
    return () => window.clearTimeout(timeout)
  }, [searchText])
  const update = (patch: Partial<SendLogFilters>) => setFilters(current => ({ ...current, ...patch }))
  const clear = () => { setSearchText(''); setFilters(emptySendLogFilters); setPlatformOpen(false) }
  const togglePlatform = (code: string) => update({ platforms: filters.platforms.includes(code) ? filters.platforms.filter(item => item !== code) : [...filters.platforms, code] })

  const list = useInfiniteQuery({
    queryKey: ['price-stock-send-log', filters],
    queryFn: context => hubPoll<PriceStockSendLogPage>(`/price-stock-send-log${sendLogQueryString(filters, context.pageParam)}`),
    initialPageParam: null as string | null,
    getNextPageParam: lastPage => lastPage.hasMore && lastPage.nextCursor ? lastPage.nextCursor : undefined,
    placeholderData: keepPreviousData,
    refetchInterval: query => (query.state.data?.pages.length ?? 0) <= 1 ? firstPagePollMs : false
  })
  const rows = useMemo(() => {
    const seen = new Set<string>()
    return (list.data?.pages ?? []).flatMap(page => page.items).filter(entry => !seen.has(entry.id) && Boolean(seen.add(entry.id)))
  }, [list.data])
  const filtered = hasSendLogFilters(filters)
  const options = useMemo(() => [...new Set([...platformOptions.filter(code => code !== 'TRENDYOL_EFATURAM'), ...filters.platforms])], [platformOptions, filters.platforms])
  const platformSummary = filters.platforms.length === 0 ? 'Tüm Platformlar' : filters.platforms.length === 1 ? marketplacePlatformLabel(filters.platforms[0]) : `${filters.platforms.length} Platform`

  return <div className="psl">
    <div className="psl-filters" role="search" aria-label="Gönderim kaydı filtreleri">
      <label className="jobs-reference-search psl-search"><UiIcon name="search" size={15} /><input value={searchText} onChange={event => setSearchText(event.target.value.slice(0, 100))} placeholder="Barkod, stok kodu, model kodu veya ürün adı…" aria-label="Gönderim kayıtlarında ara" /></label>
      <div className="psl-platform-wrap" ref={platformWrap}>
        <button type="button" className="psl-control psl-platform-toggle" aria-haspopup="true" aria-expanded={platformOpen} onClick={() => setPlatformOpen(open => !open)}>
          {filters.platforms.length === 1 && <PlatformSquareMark code={filters.platforms[0]} name={platformSummary} />}{platformSummary}<UiIcon name="chevronDown" size={14} />
        </button>
        {platformOpen && <div className="psl-platform-menu" role="group" aria-label="Platformlar">
          {options.length === 0 && <p className="psl-platform-empty">Bağlı pazaryeri yok.</p>}
          {options.map(code => <label key={code} className="psl-platform-option">
            <input type="checkbox" checked={filters.platforms.includes(code)} onChange={() => togglePlatform(code)} />
            <PlatformSquareMark code={code} name={marketplacePlatformLabel(code)} />{marketplacePlatformLabel(code)}
          </label>)}
        </div>}
      </div>
      <select className="psl-control" aria-label="Sonuç" value={filters.result} onChange={event => update({ result: event.target.value })}>
        <option value="">Tüm Sonuçlar</option>
        {sendLogResultOptions.map(result => <option key={result} value={result}>{sendLogResultLabel(result)}</option>)}
      </select>
      <select className="psl-control" aria-label="Tetikleyen" value={filters.trigger} onChange={event => update({ trigger: event.target.value })}>
        <option value="">Tüm Tetikleyenler</option>
        {sendLogTriggerOptions.map(trigger => <option key={trigger} value={trigger}>{sendLogTriggerLabel(trigger)}</option>)}
      </select>
      <label className="psl-date"><span>Başlangıç</span><input className="psl-control" type="date" value={filters.from} max={filters.to || undefined} onChange={event => update({ from: event.target.value })} /></label>
      <label className="psl-date"><span>Bitiş</span><input className="psl-control" type="date" value={filters.to} min={filters.from || undefined} onChange={event => update({ to: event.target.value })} /></label>
      <button type="button" className="psl-control psl-clear" onClick={clear} disabled={!filtered && !searchText}>Temizle</button>
      <button type="button" className="jobs-reference-refresh" title="Yenile" aria-label="Gönderim kayıtlarını yenile" onClick={() => void list.refetch()}><UiIcon name="refresh" size={16} /></button>
    </div>
    {list.isLoading ? <Busy text="Gönderim kayıtları yükleniyor…" /> : list.isError && !list.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">Gönderim kayıtları alınamadı.{list.error instanceof Error && list.error.message ? ` ${list.error.message}` : ''}</div> : <>
      <div className="psl-table-scroll"><table className="psl-table">
        <thead><tr><th>Zaman</th><th>Platform</th><th>Ürün</th><th>Stok</th><th>Fiyat</th><th>Tetikleyen</th><th>Sonuç</th><th><span className="sr-only">İş</span></th></tr></thead>
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
            : <><strong>Henüz gönderim kaydı yok.</strong><span>Pazaryerine fiyat veya stok gönderildiğinde her satır burada görünür. Otomatik fiyat-stok yazması şu an kapalı; panelden yapılan gönderimler ve yazma açıldıktan sonraki otomatik gönderimler burada listelenecek.</span></>}</td></tr>}
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
