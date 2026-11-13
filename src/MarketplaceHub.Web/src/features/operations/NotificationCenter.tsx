import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { hubPoll } from '../../shared/api'
import { DateRangePicker } from '../../shared/DateRangePicker'
import { PlatformSquareMark } from '../../shared/platform-square-mark'
import { Button, EmptyState, UiIcon } from '../../shared/components'
import { Busy } from '../../shared/components/AsyncState'
import { formatDateTime, formatIstanbul } from '../../shared/format-date'
import { describeNotification } from '../../shared/notification-describe'
import { markAllNotificationsRead, markNotificationRead } from '../../shared/notifications'
import {
  centerFilterCount, centerNotificationCard, centerQueryString, emptyCenterFilters, internalNotificationHref, markCenterNotificationsRead, notificationCategoryLabels, notificationSeverityLabels,
  subscribeNotificationCenter, type CenterFilters, type CenterNotification, type CenterNotificationPage, type CenterNotificationSummary, type NotificationCategory, type NotificationSeverity
} from '../../shared/notification-center'
import { RrIcon } from '../marketplace/returns-ui'
import { JobStateGlyph } from './job-state-glyph'
import { ChoiceField, FilterMenu, OpsFilterCard, SearchField, type FilterMenuGroup } from './filter-fields'
import './notification-center.css'

// İşlem Takibi › Bildirimler: every notification the bell showed in the last seven days, also the ones cleared from the
// bell (owner, 13 Nov 2026). Newest first, 50 a page; the first page is polled every 30 seconds and refreshed when this
// browser sends the center something.
const pollMs = 30_000
const searchDelayMs = 350

const categoryGroups: FilterMenuGroup[] = [{ options: (Object.keys(notificationCategoryLabels) as NotificationCategory[]).map(value => ({ value, label: notificationCategoryLabels[value] })) }]
const severityGroups: FilterMenuGroup[] = [{ options: (['ERROR', 'SUCCESS', 'INFO'] as NotificationSeverity[]).map(value => ({ value, label: notificationSeverityLabels[value] })) }]
const stateOptions = [{ value: '', label: 'Tümü' }, { value: 'UNREAD', label: 'Okunmamış' }, { value: 'READ', label: 'Okunmuş' }]
const glyphTone: Record<NotificationSeverity, string> = { ERROR: 'error', SUCCESS: 'success', INFO: 'waiting' }
const timeOptions: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }
const dayOptions: Intl.DateTimeFormatOptions = { day: '2-digit', month: '2-digit', year: 'numeric' }

function CenterRow({ row, expanded, onToggle, onOpen, onRead }: { row: CenterNotification; expanded: boolean; onToggle: () => void; onOpen: (href: string) => void; onRead: () => void }) {
  const card = useMemo(() => centerNotificationCard(row), [row])
  const facts = expanded && card ? describeNotification(card) : []
  const unread = !row.readAt
  const source = row.storeName ?? null
  return <article className={`ncl-row is-${row.severity.toLowerCase()}${unread ? ' is-unread' : ''}`}>
    <span className="ncl-glyph"><JobStateGlyph tone={glyphTone[row.severity]} /></span>
    <div className="ncl-body">
      <div className="ncl-top">
        <strong className="ncl-title">{row.title}{row.count > 1 && <b className="ncl-count" aria-label={`${row.count} kez`}>{row.count}×</b>}</strong>
        {unread && <i className="ncl-unread" aria-label="Okunmadı" />}
      </div>
      <p className="ncl-message">{row.message}</p>
      <div className="ncl-meta">
        <span className="ncl-tag">{notificationCategoryLabels[row.category] ?? 'Diğer'}</span>
        {(row.platformCode || source) && <span className="ncl-source">{row.platformCode && <PlatformSquareMark code={row.platformCode} name={source} />}{source && <span>{source}</span>}</span>}
        {row.dismissedAt && <span className="ncl-tag is-muted" title={`Zilden ${formatDateTime(row.dismissedAt)} tarihinde kaldırıldı`}>Zilden Kaldırıldı</span>}
        {row.shared && <span className="ncl-tag is-muted" title="Bu uyarı hesaptaki herkes için gösterilir">Herkese</span>}
      </div>
      {expanded && <div className="ncl-detail">
        <dl>
          {facts.map(fact => <div key={fact.label}><dt>{fact.label}</dt><dd>{fact.href ? <button type="button" className="ncl-link" onClick={() => onOpen(fact.href!)}>{fact.value}</button> : fact.value}</dd></div>)}
          {card?.details?.map((detail, index) => <div key={`${detail.label}:${index}`}><dt>{detail.label}</dt><dd>{detail.value}</dd></div>)}
          <div><dt>İlk Görülme</dt><dd>{formatDateTime(row.createdAt)}</dd></div>
          {row.updatedAt !== row.createdAt && <div><dt>Son Güncelleme</dt><dd>{formatDateTime(row.updatedAt)}</dd></div>}
          {row.readAt && <div><dt>Okundu</dt><dd>{formatDateTime(row.readAt)}</dd></div>}
        </dl>
        {card?.items && card.items.length > 1 && <ul className="ncl-items">{card.items.map((item, index) => <li key={`${item.id ?? item.label}:${index}`}>{item.href ? <button type="button" className="ncl-link" onClick={() => onOpen(item.href!)}>{item.label}</button> : item.label}</li>)}</ul>}
      </div>}
      <div className="ncl-actions">
        {row.href && <button type="button" className="ncl-btn" onClick={() => onOpen(row.href!)}><span>{row.actionLabel ?? 'Sayfaya Git'}</span><UiIcon name="arrowRight" size={14} /></button>}
        <button type="button" className="ncl-btn" aria-expanded={expanded} onClick={onToggle}><span>{expanded ? 'Ayrıntıyı Gizle' : 'Ayrıntı'}</span><UiIcon name="chevronDown" size={14} /></button>
        {unread && <button type="button" className="ncl-btn" onClick={onRead}><UiIcon name="circleCheck" size={14} /><span>Okundu</span></button>}
      </div>
    </div>
    <time className="ncl-time" dateTime={row.createdAt} title={formatDateTime(row.createdAt)}><strong>{formatIstanbul(row.createdAt, timeOptions)}</strong><small>{formatIstanbul(row.createdAt, dayOptions)}</small></time>
  </article>
}

export function NotificationCenter() {
  const navigate = useNavigate()
  const client = useQueryClient()
  const [filters, setFilters] = useState<CenterFilters>(emptyCenterFilters)
  const [searchText, setSearchText] = useState('')
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const [marking, setMarking] = useState(false)
  useEffect(() => {
    const timeout = window.setTimeout(() => setFilters(current => current.search === searchText ? current : { ...current, search: searchText }), searchDelayMs)
    return () => window.clearTimeout(timeout)
  }, [searchText])
  const update = (patch: Partial<CenterFilters>) => setFilters(current => ({ ...current, ...patch }))
  const clear = () => { setSearchText(''); setFilters(emptyCenterFilters) }
  const apply = () => setFilters(current => current.search === searchText ? current : { ...current, search: searchText })

  const summary = useQuery({ queryKey: ['notification-center', 'summary'], queryFn: () => hubPoll<CenterNotificationSummary>('/notifications/summary'), refetchInterval: pollMs })
  const list = useInfiniteQuery({
    queryKey: ['notification-center', 'list', filters],
    queryFn: context => hubPoll<CenterNotificationPage>(`/notifications${centerQueryString(filters, context.pageParam)}`),
    initialPageParam: null as string | null,
    getNextPageParam: lastPage => lastPage.hasMore && lastPage.nextCursor ? lastPage.nextCursor : undefined,
    placeholderData: keepPreviousData,
    refetchInterval: query => (query.state.data?.pages.length ?? 0) <= 1 ? pollMs : false
  })
  const refresh = () => client.invalidateQueries({ queryKey: ['notification-center'] })
  useEffect(() => subscribeNotificationCenter(() => { void client.invalidateQueries({ queryKey: ['notification-center'] }) }), [client])
  const rows = useMemo(() => {
    const seen = new Set<string>()
    return (list.data?.pages ?? []).flatMap(page => page.items).filter(row => !seen.has(row.id) && Boolean(seen.add(row.id)))
  }, [list.data])
  const filterCount = centerFilterCount({ ...filters, search: searchText })

  const markRead = async (row: CenterNotification) => {
    markNotificationRead(row.clientKey, { local: true })
    await markCenterNotificationsRead({ ids: [row.id] }).catch(() => undefined)
    await refresh()
  }
  const markAll = async () => {
    setMarking(true)
    try {
      markAllNotificationsRead({ local: true })
      await markCenterNotificationsRead({ all: true })
      await refresh()
    } catch { /* the list shows what the server holds after the next refresh */ } finally { setMarking(false) }
  }
  // Only a path inside the panel is followed; anything else a row carries is ignored.
  const open = (row: CenterNotification) => (href: string) => {
    const target = internalNotificationHref(href)
    if (!target) return
    if (!row.readAt) void markRead(row)
    navigate(target)
  }

  return <div className="ncl">
    <OpsFilterCard wide label="Bildirim filtreleri" active={centerFilterCount({ ...filters, search: '' })} clearCount={filterCount} onClear={clear} onApply={apply}>
      <SearchField label="Bildirim Ara" ariaLabel="Bildirimlerde ara" placeholder="Başlık, mesaj veya mağaza" value={searchText} onChange={value => setSearchText(value.slice(0, 100))} onEnter={apply} />
      <FilterMenu name="Tür" allLabel="Tüm Türler" unit="Tür" groups={categoryGroups} selected={filters.categories} onChange={categories => update({ categories })} />
      <FilterMenu name="Önem" allLabel="Tümü" unit="Önem" groups={severityGroups} selected={filters.severities} onChange={severities => update({ severities })} />
      <ChoiceField name="Durum" allValue="" value={filters.state} options={stateOptions} onChange={value => update({ state: value as CenterFilters['state'] })} />
      <DateRangePicker className="rr-fld rr-date" label="Tarih" placeholder="Başlangıç – Bitiş" from={filters.from} to={filters.to} onChange={range => update({ from: range.from, to: range.to })} icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />
    </OpsFilterCard>
    <div className="jobs-reference-toolbar ncl-toolbar">
      <div className="jobs-reference-status-summary" aria-label="Bildirim özeti">
        <span><JobStateGlyph tone="waiting" />Son 7 Gün <strong>{summary.data ? summary.data.total.toLocaleString('tr-TR') : '—'}</strong></span>
        <span role="button" tabIndex={0} aria-pressed={filters.state === 'UNREAD'} className={filters.state === 'UNREAD' ? 'is-on' : undefined}
          onClick={() => update({ state: filters.state === 'UNREAD' ? '' : 'UNREAD' })}
          onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); update({ state: filters.state === 'UNREAD' ? '' : 'UNREAD' }) } }}>
          <JobStateGlyph tone="running" />Okunmamış <strong>{summary.data ? summary.data.unread.toLocaleString('tr-TR') : '—'}</strong>
        </span>
        <span role="button" tabIndex={0} aria-pressed={filters.severities.length === 1 && filters.severities[0] === 'ERROR'} className={filters.severities.length === 1 && filters.severities[0] === 'ERROR' ? 'is-on' : undefined}
          onClick={() => update({ severities: filters.severities.length === 1 && filters.severities[0] === 'ERROR' ? [] : ['ERROR'] })}
          onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); update({ severities: filters.severities.length === 1 && filters.severities[0] === 'ERROR' ? [] : ['ERROR'] }) } }}>
          <JobStateGlyph tone="error" />Hata <strong>{summary.data ? summary.data.errors.toLocaleString('tr-TR') : '—'}</strong>
        </span>
      </div>
      <div className="jobs-reference-toolbar-actions">
        <Button variant="secondary" className="ncl-mark-all" loading={marking} disabled={!summary.data || summary.data.unread === 0} onClick={() => void markAll()}><UiIcon name="check" size={16} />Tümünü Okundu İşaretle</Button>
        <button type="button" className="jobs-reference-refresh" title="Yenile" aria-label="Bildirimleri yenile" onClick={() => void refresh()}><UiIcon name="refresh" size={16} /></button>
      </div>
    </div>
    {list.isLoading ? <Busy text="Bildirimler yükleniyor…" /> : list.isError && !list.data ? <div role="alert" className="jobs-reference-state jobs-reference-state-error">Bildirimler alınamadı.{list.error instanceof Error && list.error.message ? ` ${list.error.message}` : ''}</div> : <>
      {rows.length === 0
        ? <div className="ncl-empty">{filterCount > 0
          ? <EmptyState title="Bildirim Bulunamadı">Seçili filtrelerle eşleşen bildirim yok.</EmptyState>
          : <EmptyState title="Henüz Bildirim Yok" icon="bell">Zilde gösterilen her bildirim burada 7 gün saklanır; zilden temizlenen bildirimler de burada kalır.</EmptyState>}</div>
        : <div className="ncl-list">{rows.map(row => <CenterRow key={row.id} row={row} expanded={expandedId === row.id} onToggle={() => setExpandedId(current => current === row.id ? null : row.id)} onOpen={open(row)} onRead={() => void markRead(row)} />)}</div>}
      {rows.length > 0 && <div className="jobs-reference-pagination">
        <strong>{list.hasNextPage ? `${rows.length.toLocaleString('tr-TR')} bildirim yüklendi, daha eskileri var` : `Toplam ${rows.length.toLocaleString('tr-TR')} bildirim`}</strong>
        {list.hasNextPage && <div className="jobs-reference-pagination-actions"><Button variant="secondary" className="jobs-reference-load-more" loading={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>Daha Fazla Yükle</Button></div>}
      </div>}
    </>}
  </div>
}

export default NotificationCenter
