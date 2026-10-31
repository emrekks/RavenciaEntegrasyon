import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { keepPreviousData, useInfiniteQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { Button, UiIcon } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { formatIstanbul } from '../../shared/format-date'
import { useDismissable } from '../../shared/hooks/useDismissable'
import { marketplacePlatformLabel } from '../marketplace/marketplace-platform-support'
import {
  emptySendLogFilters, hasSendLogFilters, listPriceChanged, priceChange, quantityChange, sendLogFilterCount, sendLogQueryString, sendLogResultGroups,
  sendLogResultLabel, sendLogResultTone, sendLogResultTooltip, sendLogTriggerGroups, sendLogTriggerLabel,
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

type FilterMenuOption = { value: string; label: string; mark?: ReactNode }
type FilterMenuGroup = { label?: string; options: FilterMenuOption[] }

// One filter of the bar: a 36px button naming what is chosen ("Tüm Sonuçlar", the one value, or "2 Sonuç") and a menu
// of checkboxes under it. A group's heading picks or drops its whole group.
function FilterMenu({ name, allLabel, unit, groups, selected, onChange, emptyText }: { name: string; allLabel: string; unit: string; groups: FilterMenuGroup[]; selected: string[]; onChange: (values: string[]) => void; emptyText?: string }) {
  const [open, setOpen] = useState(false)
  const wrap = useRef<HTMLDivElement>(null)
  useDismissable(open, [wrap], () => setOpen(false))
  const options = groups.flatMap(group => group.options)
  const single = selected.length === 1 ? options.find(option => option.value === selected[0]) : undefined
  const summary = selected.length === 0 ? allLabel : single ? single.label : `${selected.length} ${unit}`
  const toggle = (value: string) => onChange(selected.includes(value) ? selected.filter(item => item !== value) : [...selected, value])
  const toggleGroup = (group: FilterMenuGroup) => {
    const values = group.options.map(option => option.value)
    onChange(values.every(value => selected.includes(value)) ? selected.filter(value => !values.includes(value)) : [...new Set([...selected, ...values])])
  }
  return <div className="psl-menu-wrap" ref={wrap}>
    <button type="button" className={`psl-control psl-menu-toggle${selected.length ? ' is-set' : ''}`} aria-label={`${name}: ${summary}`} aria-haspopup="true" aria-expanded={open} onClick={() => setOpen(current => !current)}>
      {single?.mark}<span className="psl-menu-summary">{summary}</span><UiIcon name="chevronDown" size={14} />
    </button>
    {open && <div className="psl-menu" role="group" aria-label={name}>
      {options.length === 0 && emptyText && <p className="psl-menu-empty">{emptyText}</p>}
      {groups.map((group, index) => {
        const picked = group.options.filter(option => selected.includes(option.value)).length
        return <div className="psl-menu-group" key={group.label ?? index}>
          {group.label && <label className="psl-menu-heading">
            <input type="checkbox" checked={picked === group.options.length} ref={input => { if (input) input.indeterminate = picked > 0 && picked < group.options.length }} onChange={() => toggleGroup(group)} />{group.label}
          </label>}
          {group.options.map(option => <label key={option.value} className="psl-menu-option">
            <input type="checkbox" checked={selected.includes(option.value)} onChange={() => toggle(option.value)} />{option.mark}{option.label}
          </label>)}
        </div>
      })}
    </div>}
  </div>
}

const resultGroups: FilterMenuGroup[] = sendLogResultGroups.map(group => ({ label: group.label, options: group.values.map(value => ({ value, label: sendLogResultLabel(value) })) }))
const triggerGroups: FilterMenuGroup[] = sendLogTriggerGroups.map(group => ({ label: group.label, options: group.values.map(value => ({ value, label: sendLogTriggerLabel(value) })) }))

export function PriceStockSendLog({ platformOptions, onOpenJob }: { platformOptions: string[]; onOpenJob: (jobId: string) => void }) {
  const [filters, setFilters] = useState<SendLogFilters>(emptySendLogFilters)
  const [searchText, setSearchText] = useState('')
  // Zaman is the one order the server pages by: newest first, or oldest first.
  const [oldestFirst, setOldestFirst] = useState(false)
  // The search goes to the server once typing pauses, not on every key.
  useEffect(() => {
    const timeout = window.setTimeout(() => setFilters(current => current.search === searchText ? current : { ...current, search: searchText }), searchDelayMs)
    return () => window.clearTimeout(timeout)
  }, [searchText])
  const update = (patch: Partial<SendLogFilters>) => setFilters(current => ({ ...current, ...patch }))
  const clear = () => { setSearchText(''); setFilters(emptySendLogFilters) }

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
    .map(code => ({ value: code, label: marketplacePlatformLabel(code), mark: <PlatformSquareMark code={code} name={marketplacePlatformLabel(code)} /> })) }], [platformOptions, filters.platforms])

  return <div className="psl">
    <div className="psl-filters" role="search" aria-label="Gönderim kaydı filtreleri">
      <label className="jobs-reference-search psl-search"><UiIcon name="search" size={15} /><input value={searchText} onChange={event => setSearchText(event.target.value.slice(0, 100))} placeholder="Barkod, stok kodu, model kodu veya ürün adı…" aria-label="Gönderim kayıtlarında ara" /></label>
      <FilterMenu name="Platform" allLabel="Tüm Platformlar" unit="Platform" groups={platformGroups} selected={filters.platforms} onChange={platforms => update({ platforms })} emptyText="Bağlı pazaryeri yok." />
      <FilterMenu name="Sonuç" allLabel="Tüm Sonuçlar" unit="Sonuç" groups={resultGroups} selected={filters.results} onChange={results => update({ results })} />
      <FilterMenu name="Tetikleyen" allLabel="Tüm Tetikleyenler" unit="Tetikleyen" groups={triggerGroups} selected={filters.triggers} onChange={triggers => update({ triggers })} />
      <DateRangePicker className="psl-dates" label="Gönderim Tarihi" placeholder="Tüm Tarihler" from={filters.from} to={filters.to} onChange={range => update({ from: range.from, to: range.to })} />
      <div className="psl-filter-actions">
        <button type="button" className="psl-control psl-clear" onClick={clear} disabled={filterCount === 0}>Temizle{filterCount > 0 && <span className="psl-clear-count" aria-label={`${filterCount} filtre`}>{filterCount}</span>}</button>
        <button type="button" className="jobs-reference-refresh" title="Yenile" aria-label="Gönderim kayıtlarını yenile" onClick={() => void list.refetch()}><UiIcon name="refresh" size={16} /></button>
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
