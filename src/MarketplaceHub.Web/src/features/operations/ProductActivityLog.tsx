import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router'
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { hubPoll } from '../../shared/api'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { Button, EmptyState, UiIcon } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { formatIstanbul } from '../../shared/format-date'
import { RrIcon, RrLogo } from '../marketplace/returns-ui'
import { marketplacePlatformLabel } from '../marketplace/marketplace-platform-support'
import { relativeAgeText } from '../marketplace/integrations/integration-helpers'
import { jobTimeRanges } from './job-list-query'
import { ChoiceField, FilterMenu, OpsFilterCard, SearchField, type FilterMenuGroup } from './filter-fields'
import {
  activityActor, activityChange, activityMovements, activityProductLines, activityCountsQueryString, activityFilterCount, activityKindCounts, activityKinds, activityLink,
  activityQueryString, activitySourceText, activityTypeLabel, emptyActivityFilters, toggleActivityKind,
  type ActivityFilters, type ProductActivityEntry, type ProductActivityPage
} from './product-activity'
import { ProductThumb } from './ProductThumb'
import './price-stock-send-log.css'
import './product-activity-log.css'

// İşlem Takibi › Loglar › Ürün Hareketleri: every stock, price, sale status and return change of the products, newest
// first. The first page is polled every 30 seconds while it is the only one loaded; older pages are read on demand.
const firstPagePollMs = 30_000
const searchDelayMs = 350
const exactTime: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }
const dayOptions: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }

function ProductCell({ entry }: { entry: ProductActivityEntry }) {
  const { codes, variant } = activityProductLines(entry)
  return <div className="psl-product">
    <ProductThumb url={entry.imageUrl} title={entry.productTitle ?? 'Ürün'} />
    <span className="psl-product-text">
      {entry.productId && entry.productTitle
        ? <Link to={`/products/${encodeURIComponent(entry.productId)}`} className="pal-product-link" title={entry.productTitle}>{entry.productTitle}</Link>
        : <strong>{entry.productTitle ?? 'Silinmiş ürün'}</strong>}
      {codes && <small className="pal-product-codes" title={codes}>{codes}</small>}
      {variant && <small className="pal-product-variant" title={variant}>{variant}</small>}
    </span>
  </div>
}

function ChangeChip({ entry }: { entry: ProductActivityEntry }) {
  const change = activityChange(entry)
  // Before → after in plain numbers, the new value strongest; how much it moved is a small coloured tag beside it,
  // and why it changed sits underneath. A change without numbers (a shortfall, a return kept out) is its text.
  return <span className="pal-change-cell" title={change.title ?? change.text}>
    {change.to === undefined
      ? <span className={`pal-change-text is-${change.tone}`}>{change.text}</span>
      : <span className="pal-change-line">
        <span className="pal-change-flow">
          {change.label && <span className="pal-change-label">{change.label}</span>}
          {change.from !== undefined && <><span className="pal-change-from">{change.from}</span><span className="pal-change-arrow" aria-label="iken"> → </span></>}
          <span className="pal-change-to">{change.to}</span>
        </span>
        {change.delta && <span className={`pal-change-delta is-${change.tone}`}>{change.delta}</span>}
      </span>}
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

const movementGroups: FilterMenuGroup[] = [{ options: activityMovements }]

// The filter card of Ürün Hareketleri: the search, the range with the job list's presets (chosen days override it), the
// days, Platform, Tür and Hareket Türü (with İlk Fiyatları Göster in its menu).
function ActivityFilterCard({ filters, searchText, onSearch, platformGroups, kindCounts, onChange, onClear, onApply, clearCount }: { filters: ActivityFilters; searchText: string; onSearch: (value: string) => void; platformGroups: FilterMenuGroup[]; kindCounts: Record<string, number> | null; onChange: (patch: Partial<ActivityFilters>) => void; onClear: () => void; onApply: () => void; clearCount: number }) {
  const days = Boolean(filters.from || filters.to)
  const kindGroups: FilterMenuGroup[] = [{ options: activityKinds.map(kind => ({ value: kind.key, label: kind.label, count: kindCounts ? kindCounts[kind.key] ?? 0 : null })) }]
  return <OpsFilterCard wide label="Ürün hareketi filtreleri" active={activityFilterCount({ ...filters, search: '' })} clearCount={clearCount} onClear={onClear} onApply={onApply}>
    <SearchField label="Ürün Ara" ariaLabel="Ürün hareketlerinde ara" placeholder="Barkod, stok kodu, model kodu veya ürün adı" value={searchText} onChange={value => onSearch(value.slice(0, 100))} onEnter={onApply} />
    <ChoiceField name="Zaman Aralığı" value={days ? '' : filters.range} label={days ? 'Seçili Tarihler' : undefined} options={jobTimeRanges.map(([value, label]) => ({ value, label }))} onChange={value => onChange({ range: value as ActivityFilters['range'], from: '', to: '' })} />
    <DateRangePicker className="rr-fld rr-date" label="Hareket Tarihi" placeholder="Başlangıç – Bitiş" from={filters.from} to={filters.to} onChange={range => onChange({ from: range.from, to: range.to })} icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />
    <FilterMenu name="Platform" allLabel="Tüm Platformlar" unit="Platform" groups={platformGroups} selected={filters.platforms} onChange={platforms => onChange({ platforms })} emptyText="Bağlı pazaryeri yok." />
    <FilterMenu name="Tür" allLabel="Tüm Türler" unit="Tür" groups={kindGroups} selected={filters.kinds} onChange={kinds => onChange({ kinds })} />
    <FilterMenu name="Hareket Türü" allLabel="Tüm Hareketler" unit="Hareket Türü" width={300} groups={movementGroups} selected={filters.movements} onChange={movements => onChange({ movements })} extra={{ label: 'İlk Fiyatları Göster', checked: filters.initialPrices, onChange: initialPrices => onChange({ initialPrices }) }} />
  </OpsFilterCard>
}

export type KeptActivityView = { filters: ActivityFilters; searchText: string }

// kept holds the filters while another log view is shown, so switching views and back keeps them.
export function ProductActivityLog({ platformOptions, kept }: { platformOptions: string[]; kept?: { current: KeptActivityView | null } }) {
  const [filters, setFilters] = useState<ActivityFilters>(() => kept?.current?.filters ?? emptyActivityFilters)
  const [searchText, setSearchText] = useState(() => kept?.current?.searchText ?? '')
  useEffect(() => { if (kept) kept.current = { filters, searchText } }, [kept, filters, searchText])
  useEffect(() => {
    const timeout = window.setTimeout(() => setFilters(current => current.search === searchText ? current : { ...current, search: searchText }), searchDelayMs)
    return () => window.clearTimeout(timeout)
  }, [searchText])
  const update = (patch: Partial<ActivityFilters>) => setFilters(current => ({ ...current, ...patch }))
  const clear = () => { setSearchText(''); setFilters(emptyActivityFilters) }
  // Filtrele (and Enter in the search) sends the typed search at once instead of after the typing pause.
  const apply = () => setFilters(current => current.search === searchText ? current : { ...current, search: searchText })

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
    .map(code => ({ value: code, label: marketplacePlatformLabel(code), mark: <RrLogo code={code} /> })) }], [platformOptions, filters.platforms])

  const kindChipProps = (kind: string) => {
    const on = filters.kinds.includes(kind)
    const toggle = () => update({ kinds: toggleActivityKind(filters.kinds, kind) })
    return { role: 'button', tabIndex: 0, 'aria-pressed': on, className: `pal-kind${on ? ' is-on' : ''}`, onClick: toggle, onKeyDown: (event: React.KeyboardEvent) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); toggle() } } } as const
  }
  const now = Date.now()

  return <div className="psl pal">
    <ActivityFilterCard filters={filters} searchText={searchText} onSearch={setSearchText} platformGroups={platformGroups} kindCounts={summary} onChange={update} onClear={clear} onApply={apply} clearCount={filterCount} />
    <div className="jobs-reference-toolbar psl-toolbar">
      <div className="jobs-reference-status-summary" aria-label="Ürün hareketi özeti">
        {activityKinds.map(kind => <span key={kind.key} {...kindChipProps(kind.key)}>
          <UiIcon name={kind.icon} size={14} />{kind.label} <strong>{summary ? summary[kind.key].toLocaleString('tr-TR') : '—'}</strong>
        </span>)}
      </div>
      <div className="jobs-reference-toolbar-actions">
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
            ? <EmptyState title="Kayıt Bulunamadı">Seçili filtrelerle eşleşen ürün hareketi bulunamadı.</EmptyState>
            : <EmptyState title="Henüz Hareket Yok">Siparişle düşen ya da iadeyle eklenen stok, elle veya toplu yapılan stok ve fiyat değişiklikleri, ürünün satış durumu burada listelenir.</EmptyState>}</td></tr>}
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
