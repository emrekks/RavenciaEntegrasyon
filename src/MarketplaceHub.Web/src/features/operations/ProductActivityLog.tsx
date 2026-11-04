import { useEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Link } from 'react-router'
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { Button, UiIcon } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { formatIstanbul } from '../../shared/format-date'
import { insideSelector, useDismissable } from '../../shared/hooks/useDismissable'
import { marketplacePlatformLabel } from '../marketplace/marketplace-platform-support'
import { relativeAgeText } from '../marketplace/integrations/integration-helpers'
import { jobTimeRanges } from './job-list-query'
import { FilterMenu, type FilterMenuGroup } from './log-filter-menu'
import {
  activityActor, activityChange, activityCountsQueryString, activityFilterCount, activityKindCounts, activityKinds, activityLink, activityPanelFilterCount,
  activityQueryString, activityRangeLabel, activitySourceText, activityTypeLabel, emptyActivityFilters, toggleActivityKind,
  type ActivityFilters, type ProductActivityEntry, type ProductActivityPage
} from './product-activity'
import './price-stock-send-log.css'
import './product-activity-log.css'

// İşlem Takibi › Loglar › Ürün Hareketleri: every stock, price, sale status and return change of the products, newest
// first. The first page is polled every 30 seconds while it is the only one loaded; older pages are read on demand.
const firstPagePollMs = 30_000
const searchDelayMs = 350
const exactTime: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }
const dayOptions: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }

function ProductCell({ entry }: { entry: ProductActivityEntry }) {
  const [broken, setBroken] = useState(false)
  const codes = [entry.optionText, entry.barcode ?? entry.sku, entry.modelCode].filter(Boolean).join(' · ')
  return <div className="psl-product">
    {entry.imageUrl && !broken
      ? <img src={entry.imageUrl} alt="" loading="lazy" onError={() => setBroken(true)} />
      : <span className="psl-product-placeholder" aria-hidden="true"><UiIcon name="image" size={16} /></span>}
    <span className="psl-product-text">
      {entry.productId && entry.productTitle
        ? <Link to={`/products/${encodeURIComponent(entry.productId)}`} className="pal-product-link" title={entry.productTitle}>{entry.productTitle}</Link>
        : <strong>{entry.productTitle ?? 'Silinmiş ürün'}</strong>}
      {codes && <small title={codes}>{codes}</small>}
    </span>
  </div>
}

function ChangeChip({ entry }: { entry: ProductActivityEntry }) {
  const change = activityChange(entry)
  return <span className="pal-change-cell">
    <span className={`pal-change is-${change.tone}`} title={change.title}>{change.text}</span>
    <small>{activityTypeLabel(entry)}</small>
  </span>
}

function SourceCell({ entry }: { entry: ProductActivityEntry }) {
  const link = activityLink(entry)
  const text = activitySourceText(entry)
  // The product link is already on the product's name; here only an order or a return is a link.
  const ownLink = link && !link.startsWith('/products/') ? link : null
  return <span className="pal-source">
    {ownLink ? <Link to={ownLink}>{text}<UiIcon name="arrowRight" size={12} /></Link> : <span>{text}</span>}
    {entry.note && entry.note !== text && <small title={entry.note}>{entry.note}</small>}
  </span>
}

const kindGroups: FilterMenuGroup[] = [{ options: activityKinds.map(kind => ({ value: kind.key, label: kind.label })) }]

// The page heading's controls for Ürün Hareketleri, drawn into İşlem Takibi's heading like the send log's: the range
// menu with the job list's presets (chosen days override it) and Filtrele with Platform, Tür and Tarih.
function ActivityHeadingControls({ filters, platformGroups, onChange, onClear, clearCount }: { filters: ActivityFilters; platformGroups: FilterMenuGroup[]; onChange: (patch: Partial<ActivityFilters>) => void; onClear: () => void; clearCount: number }) {
  const [rangeOpen, setRangeOpen] = useState(false)
  const [panelOpen, setPanelOpen] = useState(false)
  const rangeWrap = useRef<HTMLDivElement>(null)
  const panelWrap = useRef<HTMLDivElement>(null)
  useDismissable(rangeOpen, [rangeWrap], () => setRangeOpen(false))
  useDismissable(panelOpen, [panelWrap], () => setPanelOpen(false), { isInside: insideSelector('.date-range-picker-popover') })
  const days = Boolean(filters.from || filters.to)
  const panelCount = activityPanelFilterCount(filters)
  return <>
    <div className="jobs-reference-range-wrap" ref={rangeWrap}>
      <button type="button" className="jobs-reference-range" aria-expanded={rangeOpen} aria-label={`Hareket zaman aralığı: ${activityRangeLabel(filters)}`} onClick={() => setRangeOpen(value => !value)}>
        <UiIcon name="calendar" className="jobs-reference-icon" size={18} />{activityRangeLabel(filters)}<UiIcon name="chevronDown" className="jobs-reference-icon" size={18} />
      </button>
      {rangeOpen && <div className="jobs-reference-range-menu" role="menu" aria-label="Zaman aralığı">
        {jobTimeRanges.map(([value, label]) => <button type="button" role="menuitem" className={!days && filters.range === value ? 'active' : ''} key={value} onClick={() => { onChange({ range: value, from: '', to: '' }); setRangeOpen(false) }}>{label}</button>)}
      </div>}
    </div>
    <div className="jobs-reference-filter-wrap" ref={panelWrap}>
      <button type="button" className="jobs-reference-filter-toggle" aria-expanded={panelOpen} onClick={() => setPanelOpen(value => !value)}>
        <UiIcon name="listSortAscending" className="jobs-reference-icon" size={18} />Filtrele{panelCount > 0 && <span className="psl-clear-count" aria-label={`${panelCount} filtre`}>{panelCount}</span>}
      </button>
      {panelOpen && <div className="jobs-reference-filter-panel psl-filter-panel" role="dialog" aria-label="Ürün hareketi filtreleri">
        <div className="psl-panel-field psl-panel-wide"><span>Platform</span><FilterMenu name="Platform" allLabel="Tüm Platformlar" unit="Platform" groups={platformGroups} selected={filters.platforms} onChange={platforms => onChange({ platforms })} emptyText="Bağlı pazaryeri yok." /></div>
        <div className="psl-panel-field psl-panel-wide"><span>Tür</span><FilterMenu name="Tür" allLabel="Tüm Türler" unit="Tür" groups={kindGroups} selected={filters.kinds} onChange={kinds => onChange({ kinds })} /></div>
        <DateRangePicker className="psl-dates psl-panel-wide" label="Hareket Tarihi" placeholder="Aralık Seçilmedi" from={filters.from} to={filters.to} onChange={range => onChange({ from: range.from, to: range.to })} />
        <button type="button" className="psl-control psl-clear psl-panel-wide" onClick={onClear} disabled={clearCount === 0}>Temizle{clearCount > 0 && <span className="psl-clear-count" aria-label={`${clearCount} filtre`}>{clearCount}</span>}</button>
      </div>}
    </div>
  </>
}

export function ProductActivityLog({ platformOptions, headingSlot }: { platformOptions: string[]; headingSlot?: HTMLElement | null }) {
  const [filters, setFilters] = useState<ActivityFilters>(emptyActivityFilters)
  const [searchText, setSearchText] = useState('')
  useEffect(() => {
    const timeout = window.setTimeout(() => setFilters(current => current.search === searchText ? current : { ...current, search: searchText }), searchDelayMs)
    return () => window.clearTimeout(timeout)
  }, [searchText])
  const update = (patch: Partial<ActivityFilters>) => setFilters(current => ({ ...current, ...patch }))
  const clear = () => { setSearchText(''); setFilters(emptyActivityFilters) }

  // The summary counts every kind under the other filters, so a picked kind keeps the others' counts in view.
  const countFilters = useMemo(() => ({ ...filters, kinds: [] }), [filters])
  const counts = useQuery({
    queryKey: ['product-activity', 'counts', countFilters],
    queryFn: () => hubPoll<Array<{ kind: string; count: number }>>(`/product-activity/counts${activityCountsQueryString(countFilters)}`),
    placeholderData: keepPreviousData,
    refetchInterval: firstPagePollMs
  })
  const summary = useMemo(() => counts.data ? activityKindCounts(counts.data) : null, [counts.data])

  const list = useInfiniteQuery({
    queryKey: ['product-activity', filters],
    queryFn: context => hubPoll<ProductActivityPage>(`/product-activity${activityQueryString(filters, context.pageParam)}`),
    initialPageParam: null as string | null,
    getNextPageParam: lastPage => lastPage.hasMore && lastPage.nextCursor ? lastPage.nextCursor : undefined,
    placeholderData: keepPreviousData,
    refetchInterval: query => (query.state.data?.pages.length ?? 0) <= 1 ? firstPagePollMs : false
  })
  const rows = useMemo(() => {
    const seen = new Set<string>()
    return (list.data?.pages ?? []).flatMap(page => page.items).filter(entry => !seen.has(entry.id) && Boolean(seen.add(entry.id)))
  }, [list.data])
  const filtered = activityFilterCount(filters) > 0
  const filterCount = activityFilterCount({ ...filters, search: searchText })
  const platformGroups = useMemo<FilterMenuGroup[]>(() => [{ options: [...new Set([...platformOptions.filter(code => code !== 'TRENDYOL_EFATURAM'), ...filters.platforms])]
    .map(code => ({ value: code, label: marketplacePlatformLabel(code), mark: <PlatformSquareMark code={code} name={marketplacePlatformLabel(code)} /> })) }], [platformOptions, filters.platforms])

  const kindChipProps = (kind: string) => {
    const on = filters.kinds.includes(kind)
    const toggle = () => update({ kinds: toggleActivityKind(filters.kinds, kind) })
    return { role: 'button', tabIndex: 0, 'aria-pressed': on, className: `pal-kind${on ? ' is-on' : ''}`, onClick: toggle, onKeyDown: (event: React.KeyboardEvent) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); toggle() } } } as const
  }
  const now = Date.now()

  return <div className="psl pal">
    {headingSlot && createPortal(<ActivityHeadingControls filters={filters} platformGroups={platformGroups} onChange={update} onClear={clear} clearCount={filterCount} />, headingSlot)}
    <div className="jobs-reference-toolbar psl-toolbar">
      <div className="jobs-reference-status-summary" aria-label="Ürün hareketi özeti">
        {activityKinds.map(kind => <span key={kind.key} {...kindChipProps(kind.key)}>
          <UiIcon name={kind.icon} size={14} />{kind.label} <strong>{summary ? summary[kind.key].toLocaleString('tr-TR') : '—'}</strong>
        </span>)}
      </div>
      <div className="jobs-reference-toolbar-actions" role="search" aria-label="Ürün hareketi arama">
        <label className="jobs-reference-search psl-search"><UiIcon name="search" size={15} /><input value={searchText} onChange={event => setSearchText(event.target.value.slice(0, 100))} placeholder="Barkod, stok kodu, model kodu veya ürün adı…" aria-label="Ürün hareketlerinde ara" /></label>
        <button type="button" className="jobs-reference-refresh" title="Yenile" aria-label="Ürün hareketlerini yenile" onClick={() => { void list.refetch(); void counts.refetch() }}><UiIcon name="refresh" size={16} /></button>
      </div>
    </div>
    {list.isLoading ? <Busy text="Ürün hareketleri yükleniyor…" /> : list.isError && !list.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">Ürün hareketleri alınamadı.{list.error instanceof Error && list.error.message ? ` ${list.error.message}` : ''}</div> : <>
      <div className="psl-table-scroll"><table className="psl-table pal-table">
        <thead><tr><th scope="col">Zaman</th><th scope="col">Ürün</th><th scope="col">Değişiklik</th><th scope="col">Kaynak</th><th scope="col">Platform</th><th scope="col">Kullanıcı</th></tr></thead>
        <tbody>
          {rows.map(entry => {
            const exact = formatIstanbul(entry.occurredAt, exactTime)
            const store = entry.platformCode ? entry.storeName || marketplacePlatformLabel(entry.platformCode) : null
            return <tr key={entry.id} className={`psl-row pal-row is-${entry.kind.toLowerCase()}`}>
              <td className="psl-time pal-time" title={exact}><strong>{relativeAgeText(entry.occurredAt, now) ?? '—'}</strong><small>{formatIstanbul(entry.occurredAt, dayOptions)}</small></td>
              <td className="psl-product-cell"><ProductCell entry={entry} /></td>
              <td className="pal-change-td" data-label="Değişiklik"><ChangeChip entry={entry} /></td>
              <td className="pal-source-td" data-label="Kaynak"><SourceCell entry={entry} /></td>
              <td className="psl-platform pal-platform">{store ? <><PlatformSquareMark code={entry.platformCode!} name={store} /><span title={store}>{store}</span></> : <span className="pal-muted">Panel</span>}</td>
              <td className="pal-actor" data-label="Kullanıcı">{activityActor(entry)}</td>
            </tr>
          })}
          {rows.length === 0 && <tr><td className="psl-empty" colSpan={6}>{filtered
            ? 'Seçili filtrelerle eşleşen ürün hareketi bulunamadı.'
            : <><strong>Henüz ürün hareketi yok.</strong><span>Siparişle düşen ya da iadeyle eklenen stok, elle veya toplu yapılan stok ve fiyat değişiklikleri, ürünün satış durumu burada listelenir.</span></>}</td></tr>}
        </tbody>
      </table></div>
      {/* Only the way to older rows; no count or note under the list (owner, 5 Nov 2026). */}
      {list.hasNextPage && <div className="jobs-reference-pagination">
        <div className="jobs-reference-pagination-actions"><Button variant="secondary" className="jobs-reference-load-more" loading={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>Daha Fazla Yükle</Button></div>
      </div>}
    </>}
  </div>
}

export default ProductActivityLog
