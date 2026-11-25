import type { ApiAnswer } from '../../../shared/api/api-answer'
import { initialSort, rememberSort } from '../../../shared/sticky-sort'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { hubApi } from '../../../shared/api'
import { Busy, CardBackgroundIcon, EmptyState, ErrorBox, Pager, UiIcon, type UiIconName } from '../../../shared/components'
import { DateRangePicker } from '../../../shared/DateRangePicker'
import { formatIstanbul } from '../../../shared/format-date'
import { TabSlidePanel } from '../../../shared/tab-slide'
import { formatCount, formatTl, formatTlWhole, platformName } from '../report-format'
import { transactionLabel } from '../transaction-labels'
import { byDay, emptyPayoutFilters, estimateNote, filterGroups, isRolledToPayoutDay, isWaitingPayout, lastPayout, lineMoney, nextPayout, otherCurrenciesText, rolledPayoutNote, payoutRuleText, payoutGap, payoutLinesQuery, payoutState, payoutStateLabels, payoutsQuery, readPayoutLines, readPayouts, regionLabel, regionLabels, shortDay, statusLabels, type PayoutFilters, type PayoutGroup, type PayoutRegion, type PayoutSort, type PayoutState } from '../payouts'
import { readUrlChoice, readUrlList, readUrlText, useListUrlSync } from '../../../shared/list-url-state'
import { RrAnchor, RrFilterCard, RrIcon } from '../../../shared/ui/rr-components'
import { RETURN_NO_PLATFORM, ReturnColumnPop, ReturnPlatformPop } from '../../marketplace/shared'
import { PlatformSquareMark } from '../../../shared/platform-square-mark'
import { queryKeys } from '../../../shared/api/query-keys'
import { SubTabs, Panel, type Kpi, KpiStrip } from './report-parts'

// Hakediş: what the marketplaces will pay, by payout day, and what they paid; every figure from the stores' accounting
// records as read. The balance matches Trendyol's own (Toplam Güncel Bakiye); the days to come are estimates.
// The filters live in the address (hp platforms, hr region, hs state, hf/ht days, hmin/hmax amounts, hq search, ho order).
// Platforms are asked of the server, so every figure follows them; the region narrows the figures too (from the
// server's region totals, exact); the state, days and amounts narrow the list only.
export function PayoutsReport({ platforms: pagePlatforms }: { platforms: string[] | null }) {
  const [params] = useSearchParams()
  const [platforms, setPlatforms] = useState<string[]>(() => readUrlList(params, 'hp').length ? readUrlList(params, 'hp') : pagePlatforms ?? [])
  const [applied, setApplied] = useState<PayoutFilters>(() => ({
    region: readUrlChoice(params, 'hr', ['ALL', 'TR_GULF', 'EUROPE', 'AZ', 'UNKNOWN'] as const, 'ALL'),
    state: readUrlChoice(params, 'hs', ['ALL', 'EXPECTED', 'PAID', 'OVERDUE', 'DEDUCTION'] as const, 'ALL'),
    from: readUrlText(params, 'hf'), to: readUrlText(params, 'ht'), min: readUrlText(params, 'hmin'), max: readUrlText(params, 'hmax'),
    sort: initialSort('payouts', ['date', 'amount'] as const, 'date', params.get('ho'))
  }))
  const [search, setSearch] = useState(() => readUrlText(params, 'hq'))
  const [draft, setDraft] = useState({ ...applied, platforms, search })
  const [tab, setTab] = useState<'upcoming' | 'past'>(() => applied.state === 'PAID' ? 'past' : 'upcoming')
  // The page's Platform filter, once changed while the tab is open, sets the marketplaces here too (the address's hp
  // only picks them when the tab opens).
  const [pageSeen, setPageSeen] = useState(pagePlatforms)
  if (pageSeen !== pagePlatforms) {
    setPageSeen(pagePlatforms)
    setPlatforms(pagePlatforms ?? [])
    setDraft(current => ({ ...current, platforms: pagePlatforms ?? [] }))
  }
  const [open, setOpen] = useState<string | null>(null)
  // Trendyol's regions open under its card on a click of its Bölgeler button and close on the next click (or Escape).
  const [regionsOpen, setRegionsOpen] = useState(false)
  const regionsRef = useRef<HTMLLIElement>(null)
  useEffect(() => {
    if (!regionsOpen) return
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setRegionsOpen(false) }
    document.addEventListener('keydown', escape)
    return () => document.removeEventListener('keydown', escape)
  }, [regionsOpen])
  useListUrlSync({ hp: platforms, hr: applied.region, hs: applied.state, hf: applied.from, ht: applied.to, hmin: applied.min, hmax: applied.max, hq: search, ho: applied.sort },
    { hp: [], hr: 'ALL', hs: 'ALL', hf: '', ht: '', hmin: '', hmax: '', hq: '', ho: 'date' })
  const none = platforms.includes(RETURN_NO_PLATFORM)
  const query = payoutsQuery(platforms.length ? platforms : null)
  const result = useQuery({ queryKey: queryKeys.reportsPayouts(query), queryFn: async ({ signal }) => readPayouts(await hubApi<ApiAnswer<'getReportsPayouts'>>(`/reports/payouts?${query}`, { signal })), staleTime: 60_000, placeholderData: previous => previous, enabled: !none })
  const everything = useQuery({ queryKey: queryKeys.reportsPayouts(''), queryFn: async ({ signal }) => readPayouts(await hubApi<ApiAnswer<'getReportsPayouts'>>('/reports/payouts?', { signal })), staleTime: 60_000, enabled: platforms.length > 0 })
  const apply = (next: typeof draft) => {
    setDraft(next)
    setPlatforms(next.platforms)
    setSearch(next.search)
    rememberSort('payouts', next.sort)
    setApplied({ region: next.region, state: next.state, from: next.from, to: next.to, min: next.min, max: next.max, sort: next.sort })
    if (next.state === 'PAID') setTab('past')
    else if (next.state !== 'ALL') setTab('upcoming')
    setOpen(null)
  }
  const pickRegion = (region: PayoutRegion | 'ALL') => apply({ ...draft, platforms, search, ...applied, region })
  // A card adds its marketplace to the picked ones or takes it out (owner, 7 Nov 2026): several add up, none is all of
  // them. The server answers for the picked set as a whole, so totals and days are each counted once.
  const pickPlatform = (code: string) => {
    const kept = platforms.filter(item => item !== RETURN_NO_PLATFORM)
    apply({ ...draft, ...applied, search, platforms: kept.includes(code) ? kept.filter(item => item !== code) : [...kept, code] })
  }
  // The marketplaces' cards stay all in view while one is picked: they come from the unfiltered answer then.
  const platformCards = none ? [] : (platforms.length ? everything.data?.platforms : result.data?.platforms) ?? []
  // A summary card in the metric card language: its mark and name, its balance, the next payout, its overdue part.
  type CardItem = { expected: number; nextDay: string | null; nextAmount: number | null; unknown: number }
  const nextText = (item: CardItem) => item.nextDay ? `Sıradaki ${shortDay(item.nextDay, false)} · ${formatTl(item.nextAmount)}` : 'Sıradaki ödeme günü belli değil'
  // Overdue sales wait for a payout; an overdue sum below zero is deductions waiting to come off the next one.
  // Phones show the short form (Gecikmiş / Kesinti, whole liras) so the chip fits the card.
  const overdueChip = (item: CardItem) => <>
    {item.unknown > 0 && <em className="reports-payout-chip is-overdue"><span className="reports-payout-chip-long">Vadesi Geçmiş {formatTl(item.unknown)}</span><span className="reports-payout-chip-short">Gecikmiş {formatTlWhole(item.unknown)}</span></em>}
    {item.unknown < 0 && <em className="reports-payout-chip is-deduction"><span className="reports-payout-chip-long">Bekleyen Kesinti {formatTl(-item.unknown)}</span><span className="reports-payout-chip-short">Kesinti {formatTlWhole(-item.unknown)}</span></em>}
  </>
  const summaryCard = (key: string, name: string, mark: string, icon: UiIconName, item: CardItem, pressed: boolean, pick: () => void, extra?: { tag?: string; children: ReactNode }) =>
    <li key={key} ref={extra ? regionsRef : undefined} className={extra ? `reports-payout-has-regions${regionsOpen ? ' is-open' : ''}` : undefined}>
      <button type="button" className="reports-payout-region" aria-pressed={pressed} onClick={pick}>
        <CardBackgroundIcon name={icon} />
        <span className="reports-payout-region-name"><PlatformSquareMark code={mark} name={platformName(mark)} />{name}
          {extra?.tag && <em className="reports-payout-chip is-region">{extra.tag}</em>}</span>
        <strong>{formatTl(item.expected)}</strong>
        <span className="reports-payout-region-next">{nextText(item)}</span>
        {overdueChip(item)}
      </button>
      {extra?.children}
    </li>
  const clear = () => apply({ ...emptyPayoutFilters, platforms: [], search: '', sort: applied.sort })
  const data = result.data
  if (result.isError && !data) return <ErrorBox error={result.error} onRetry={() => void result.refetch()} />
  if (!data && !none) return <Busy text="Hakediş hazırlanıyor…" />
  const trendyol = platforms.length === 0 || platforms.includes('TRENDYOL')
  const region = trendyol ? applied.region : 'ALL'
  const regions = trendyol ? data?.regions.filter(item => item.region !== 'NONE') ?? [] : []
  const upcoming = none ? [] : (data?.upcoming ?? []).filter(group => region === 'ALL' || group.region === region)
  const past = none ? [] : (data?.past ?? []).filter(group => region === 'ALL' || group.region === region)
  // The figures for the chosen region come from the server's region totals and groups, so they stay exact.
  const picked = region === 'ALL' ? null : regions.find(item => item.region === region) ?? null
  // The server's next payout, overall or the picked region's: a first day that nets to nothing or less is skipped there.
  const { day: nextDay, amount: nextAmount } = none ? { day: null, amount: null } : nextPayout(data, region)
  const lastPaid = lastPayout(data, past, region)
  const balance = none ? 0 : picked ? picked.expected : data?.expected ?? 0
  const overdue = none ? 0 : picked ? picked.unknown : data?.unknown ?? 0
  const pendingDelivery = none ? 0 : picked ? picked.pendingDelivery : data?.pendingDelivery ?? 0
  const pendingOrders = none ? 0 : picked ? picked.pendingDeliveryOrders : data?.pendingDeliveryOrders ?? 0
  // The payout day under Sıradaki Ödeme when one marketplace is in view (Trendyol's rule in a few words).
  const covered = platforms.length ? platforms : data?.platforms.map(item => item.platformCode) ?? []
  const payoutDay = covered.length !== 1 ? '' : covered[0] === 'TRENDYOL' ? ' Trendyol, Türkiye-Körfez ve Azerbaycan ödemelerini pazartesi ve perşembe, Avrupa ödemelerini ayın 4. pazartesi günü yapar.' : payoutRuleText[covered[0]] ? ` ${payoutRuleText[covered[0]]}` : ''
  // Trendyol records a payout paid but not read yet (read a day after the payout): out of the balance, said in its tip.
  const paidUnread = none || picked ? 0 : data?.paidUnreadRecords ?? 0
  const otherText = none || picked ? '' : otherCurrenciesText(data?.otherCurrencies)
  const paidUnreadText = paidUnread > 0 ? `\nSon Trendyol ödemesinin ${formatCount(paidUnread)} kaydı (${formatTl(data?.paidUnread ?? 0)}) ödendi; kayıtları ödemeden bir gün sonra okunur, bu yüzden bakiyeye sayılmadı.` : ''
  // What earlier days that netted to nothing or less take off the next payout (the overall figure only).
  const carried = region === 'ALL' && !none ? data?.next?.carried ?? null : null
  const carriedText = carried ? `\nÖnceki ödeme gününden devreden kesinti (${formatTl(-carried)}) bu ödemeden düşüldü.` : ''

  const items: Kpi[] = [
    { key: 'expected', label: 'Toplam Güncel Bakiye', value: balance, format: formatTl, icon: 'wallet', note: picked ? regionLabels[picked.region] : `${formatCount(data?.expectedRecords ?? 0)} kayıt`,
      tip: `Teslim edilmiş, henüz ödenmemiş siparişlerin komisyon ve bekleyen kesintiler düşülmüş hakedişi. Kesilmemiş kesinti faturaları dahil değil.${paidUnreadText}${otherText ? `
${otherText}` : ''}` },
    { key: 'pending', label: 'Teslim Bekleyen Sipariş', value: pendingDelivery, format: formatTl, icon: 'truck',
      note: pendingOrders > 0 ? `${formatCount(pendingOrders)} sipariş` : 'Yok',
      tip: 'Onaylanmış ya da kargodaki, henüz teslim edilmemiş siparişlerin müşterinin ödediği tutarı. Teslimden sonra bakiyeye girer.' },
    { key: 'next', label: 'Sıradaki Ödeme', value: nextAmount, format: formatTl, icon: 'calendarClock', note: nextDay ? shortDay(nextDay) : 'Bekleyen ödeme yok',
      tip: `En yakın ödeme gününde gelecek tahmini tutar; kesinti faturaları kesildikçe düşer. Eksi kapanan ödeme günü ödeme yapmaz, eksisi mağazanın sonraki ödemesinden düşülür. Vadesi geçmiş kayıtlar burada sayılmaz, Vadesi Geçmiş Bekleyen'de gösterilir.${payoutDay}${carriedText}` },
    { key: 'last', label: 'Son Ödeme', value: lastPaid?.amount ?? null, format: formatTl, icon: 'circleCheck', note: lastPaid ? shortDay(lastPaid.day) : 'Okunmuş ödeme yok',
      tip: 'Pazaryerinin yaptığı son ödeme, kendi bildirdiği tutarla.' },
    { key: 'unknown', label: 'Vadesi Geçmiş Bekleyen', value: overdue, format: formatTl, icon: 'alertTriangle', tone: overdue > 0 ? 'warn' : undefined,
      note: overdue < 0 ? 'Bekleyen kesinti' : overdue > 0 ? 'Ödeme bekliyor' : 'Yok',
      tip: `Vadesi geçmiş ama henüz bir ödemeye bağlanmamış bakiye; eksiyse sonraki ödemeden düşülecek kesintidir. Ödenmeyecek demek değildir. Sıradaki Ödeme'ye eklenmez.` },
    { key: 'early', label: 'Erken Ödeme', value: none ? 0 : data?.earlyPaymentsTaken ?? 0, format: formatTl, icon: 'banknote',
      note: (data?.earlyPayments ?? 0) > 0 ? `Son ${data?.daysBack ?? 90} gün · ${formatCount(data?.earlyPayments ?? 0)} kayıt` : `Son ${data?.daysBack ?? 90} günde alınmadı`,
      tip: `Son ${data?.daysBack ?? 90} günde alınan erken ödemelerin toplamı.` }
  ]
  // A group netting to nothing pays nothing and is left out; one below zero (a marketplace's deductions) is no payout day:
  // the marketplace takes it off a later payout, so it waits apart, as the overdue records do.
  const listed = filterGroups(tab === 'upcoming' ? upcoming : past, { ...applied, region: 'ALL' }).filter(group => Math.abs(group.amount) >= 0.005)
  const byDate = applied.sort === 'date'
  const deducting = (group: PayoutGroup) => tab === 'upcoming' && !isWaitingPayout(group) && group.amount < 0
  const waiting = byDate ? listed.filter(group => group.kind === 'OVERDUE') : []
  const undated = byDate ? listed.filter(group => group.kind === 'UNDATED') : []
  const deductions = byDate ? listed.filter(deducting) : []
  const days = byDay(byDate ? listed.filter(group => !isWaitingPayout(group) && !deducting(group)) : listed)
  const filtered = [applied.state !== 'ALL', Boolean(applied.from || applied.to), Boolean(applied.min || applied.max)].some(Boolean)
  const activeCount = [platforms.length > 0, region !== 'ALL', applied.state !== 'ALL', Boolean(applied.from || applied.to), Boolean(applied.min || applied.max), Boolean(search.trim())].filter(Boolean).length
  // Temizle is grey while the applied filters and the card's typed edits are all at their defaults.
  const hasFiltersToClear = platforms.length > 0 || draft.platforms.length > 0 || Boolean(search.trim() || draft.search.trim()) || (Object.keys(emptyPayoutFilters) as Array<keyof typeof emptyPayoutFilters>).some(key => key !== 'sort' && (applied[key] !== emptyPayoutFilters[key] || draft[key] !== emptyPayoutFilters[key]))
  const read = data?.coverage.filter(store => store.state === 'READ') ?? []
  const platformOptions = ['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA'].map(code => ({ value: code, label: platformName(code) }))
  const single = (label: string, value: string, options: Array<{ value: string; label: string }>, pick: (value: string) => void, disabled = false, right = false) =>
    <div className="rr-fld"><small>{label}</small><RrAnchor trigger={(isOpen, toggle) => <button type="button" className={`rr-trigger${isOpen ? ' rr-on' : ''}`} onClick={toggle} disabled={disabled}>
      <span>{options.find(option => option.value === value)?.label ?? options[0].label}</span><RrIcon name="chev" /></button>}>
      {close => <ReturnColumnPop label={label} options={options} value={value} right={right} onPick={next => { pick(next); close() }} />}</RrAnchor></div>
  const row = (group: PayoutGroup) => {
    const expanded = open === group.key
    const gap = payoutGap(group)
    const area = regionLabel(group)
    const estimate = estimateNote(group)
    return <li key={group.key} className={expanded ? 'is-open' : undefined}>
      {/* Names on the left, the money on the right, in line with the day's total. */}
      <button type="button" className="reports-payout-row" aria-expanded={expanded} onClick={() => setOpen(expanded ? null : group.key)}>
        <span className="reports-payout-store"><PlatformSquareMark code={group.platformCode} name={platformName(group.platformCode)} /><span>{group.storeName}</span>
          {area && <em className="reports-payout-chip is-region">{area}</em>}</span>
        <span className="reports-payout-state">{applied.sort === 'amount' && <small>{shortDay(group.day, false)}</small>}
          {payoutState(group) !== 'EXPECTED' && <em className={`reports-payout-chip is-${payoutState(group).toLowerCase()}`}>{payoutStateLabels[payoutState(group)]}</em>}</span>
        <strong className="reports-payout-amount" title={estimate ?? undefined}>{formatTl(group.amount)}</strong>
        <UiIcon name="chevronDown" size={16} />
      </button>
      {expanded && <div className="reports-payout-detail">
        {gap !== null && <p className="reports-payout-gap" role="note">Okunan kayıtların toplamı {formatTl(group.linesAmount)}; ödeme tutarından {formatTl(gap)} farklı (kesinti ya da başka ödemede listelenen kayıt).</p>}
        {group.kind === 'PAYOUT' && group.linesAmount === null && <p className="reports-payout-gap" role="note">Bu ödemenin kayıtları ödemeden bir gün sonra okunur; henüz okunmadı.</p>}
        {isRolledToPayoutDay(group) && <p className="reports-payout-gap" role="note">{rolledPayoutNote}</p>}
        {estimate && <p className="reports-payout-gap" role="note">{estimate}</p>}
        <PayoutLinesTable platforms={platforms.length ? platforms : null} group={group} />
      </div>}
    </li>
  }
  // A day's state: paid, partly paid, or estimated.
  const dayState = (groups: PayoutGroup[]) => groups.every(group => group.status === 'PAID') ? ['Ödendi', 'is-paid'] as const
    : groups.some(group => group.status === 'PAID') ? ['Kısmen Ödendi', 'is-paid'] as const : ['Tahmini', 'is-expected'] as const
  return <div className="reports-summary reports-payouts">
    <KpiStrip label="Hakediş özeti" items={items} marks long />
    <Panel title="Ödeme Özeti" description="Kartlar pazaryeri ve bölge filtresine göre; durum, tarih ve tutar filtreleri yalnız listeyi süzer." className="reports-payout-list">
      {/* Every marketplace's total (a card sets the platform filter; the totals add up to the balance). Trendyol's card
          holds its payout regions in a list that opens under it; a region sets the region filter. */}
      {!search.trim() && platformCards.length > 0 && <div className="reports-payout-regions is-platforms" role="group" aria-label="Pazaryeri Toplamları">
        <ul>{platformCards.map(item => {
          const withRegions = item.platformCode === 'TRENDYOL' && regions.length > 0
          return summaryCard(item.platformCode, platformName(item.platformCode), item.platformCode, 'wallet', item,
            platforms.includes(item.platformCode) || (withRegions && region !== 'ALL'), () => pickPlatform(item.platformCode),
            withRegions ? { tag: region !== 'ALL' ? regionLabels[region] : undefined, children: <>
              <button type="button" className="reports-payout-region-more" aria-expanded={regionsOpen} aria-controls="reports-payout-regionpop"
                onClick={() => setRegionsOpen(!regionsOpen)}>{regions.length} Bölge<UiIcon name="chevronDown" size={13} /></button>
              <div id="reports-payout-regionpop" className="reports-payout-regionpop" role="group" aria-label="Trendyol Bölgeleri">
                <button type="button" className="reports-payout-regionpop-all" aria-pressed={region === 'ALL'} onClick={() => { pickRegion('ALL'); setRegionsOpen(false) }}>
                  <span>Tüm Bölgeler</span><strong>{formatTl(item.expected)}</strong></button>
                {regions.map(entry => <button key={entry.region} type="button" aria-pressed={region === entry.region}
                  onClick={() => { pickRegion(region === entry.region ? 'ALL' : entry.region); setRegionsOpen(false) }}>
                  <span>{regionLabels[entry.region]}</span><strong>{formatTl(entry.expected)}</strong>
                  <small>{nextText(entry)}</small>{overdueChip(entry)}
                </button>)}
              </div>
            </> } : undefined)
        })}</ul>
      </div>}
      <div className="rr reports-payout-filters">
        <RrFilterCard active={activeCount}><div className="rr-filters reports-payout-filterbar">
          <label className="rr-fld rr-search"><small>Sipariş / Fatura No</small><span style={{ position: 'relative' }}><RrIcon name="search" style={{ position: 'absolute', left: 11, top: 10, color: 'var(--rr-subtle)' }} />
            <input className="rr-in" style={{ paddingLeft: 34 }} aria-label="Sipariş veya fatura numarasıyla ara" placeholder="Sipariş, fatura veya kayıt no" value={draft.search}
              onChange={event => setDraft({ ...draft, search: event.target.value })} onKeyDown={event => { if (event.key === 'Enter') apply(draft) }} /></span></label>
          <div className="rr-fld"><small>Pazaryeri</small><RrAnchor trigger={(isOpen, toggle) => <button type="button" className={`rr-trigger${isOpen ? ' rr-on' : ''}`} onClick={toggle}>
            <span>{draft.platforms.length === 0 ? 'Tümü' : draft.platforms.includes(RETURN_NO_PLATFORM) ? 'Hiçbiri' : draft.platforms.map(platformName).join(', ')}</span><RrIcon name="chev" /></button>}>
            {() => <ReturnPlatformPop title="Pazaryeri" options={platformOptions} selected={draft.platforms} onChange={next => setDraft({ ...draft, platforms: next })} />}</RrAnchor></div>
          {single('Bölge', draft.region, [{ value: 'ALL', label: 'Tümü' }, { value: 'TR_GULF', label: regionLabels.TR_GULF }, { value: 'EUROPE', label: regionLabels.EUROPE }, { value: 'AZ', label: regionLabels.AZ }],
            value => setDraft({ ...draft, region: value as PayoutRegion | 'ALL' }), draft.platforms.length > 0 && !draft.platforms.includes('TRENDYOL'))}
          {single('Durum', draft.state, (Object.keys(payoutStateLabels) as PayoutState[]).map(value => ({ value, label: payoutStateLabels[value] })), value => setDraft({ ...draft, state: value as PayoutState }))}
          <DateRangePicker className="rr-fld rr-date" from={draft.from} to={draft.to} onChange={range => setDraft({ ...draft, from: range.from, to: range.to })}
            label={tab === 'past' ? 'Ödeme Tarihi' : 'Ödeme Günü'} placeholder="Başlangıç – Bitiş" icon={<RrIcon name="cal" />} chevron={<RrIcon name="chev" />} />
          <div className="rr-fld reports-payout-range"><small>Tutar (₺)</small><span>
            <input className="rr-in" inputMode="decimal" aria-label="En az tutar" placeholder="En az" value={draft.min} onChange={event => setDraft({ ...draft, min: event.target.value })} />
            <input className="rr-in" inputMode="decimal" aria-label="En çok tutar" placeholder="En çok" value={draft.max} onChange={event => setDraft({ ...draft, max: event.target.value })} /></span></div>
          {single('Sıralama', draft.sort, [{ value: 'date', label: 'Tarihe Göre' }, { value: 'amount', label: 'Tutara Göre' }], value => setDraft({ ...draft, sort: value as PayoutSort }), false, true)}
          <div className="rr-fbtns"><button type="button" className="rr-btn rr-clear" onClick={clear} disabled={!hasFiltersToClear}>Temizle</button><button type="button" className="rr-btn rr-primary" onClick={() => apply(draft)}>Filtrele</button></div>
        </div></RrFilterCard>
      </div>
      {search.trim() ? <PayoutLinesTable key={`${search}|${platforms.join(',')}`} platforms={platforms.length ? platforms : null} search={search} />
        : <>
          <SubTabs label="Ödemeler" value={tab} onChange={next => { setTab(next); setOpen(null) }}
            items={[{ value: 'upcoming', label: 'Gelecek Ödemeler', icon: 'calendarClock' }, { value: 'past', label: 'Geçmiş Ödemeler', icon: 'circleCheck' }]} />
          <TabSlidePanel active={tab} order={['upcoming', 'past']} role="tabpanel">{days.length === 0 && waiting.length === 0 && undated.length === 0 && deductions.length === 0
            ? <EmptyState icon="wallet" title={activeCount > 0 ? 'Ödeme Bulunamadı' : 'Henüz Ödeme Yok'} onClearFilters={activeCount > 0 ? clear : undefined}>{filtered || none || region !== 'ALL' ? 'Bu filtrelerle ödeme yok.' : tab === 'upcoming' ? 'Bekleyen ödeme yok.' : `Son ${data?.daysBack ?? 90} günde okunmuş ödeme yok.`}</EmptyState>
            : <div className="reports-payout-days">
              {days.map((item, index) => {
                const next = tab === 'upcoming' && applied.sort === 'date' && item.day === nextDay
                const [stateLabel, stateTone] = dayState(item.groups)
                const [year] = item.day.split('-')
                return <section key={`${item.day}-${index}`} className={`reports-payout-day${next ? ' is-next' : ''}`} aria-label={shortDay(item.day)}>
                  <header>
                    <UiIcon name="calendar" size={16} />
                    <span className="reports-payout-day-name"><b>{shortDay(item.day, false)} {shortDay(item.day).split(' ').at(-1)}</b><small>{year}</small></span>
                    {/* The chips in one box, so on a phone they can drop under the day while the total stays beside it. */}
                    <span className="reports-payout-day-chips">{next && <em className="reports-payout-chip is-next">Sıradaki<span className="reports-payout-chip-more"> Ödeme</span></em>}
                      <em className={`reports-payout-chip ${stateTone}`}>{stateLabel}</em></span>
                    <strong>{formatTl(item.total)}</strong>
                  </header>
                  <ul className="reports-payout-rows">{item.groups.map(row)}</ul>
                </section>
              })}
              {deductions.length > 0 && <section className="reports-payout-day is-deduction" aria-label="Bekleyen kesinti">
                <header>
                  <UiIcon name="alertTriangle" size={16} />
                  <span className="reports-payout-day-name"><b>Bekleyen Kesinti</b><small>Sonraki ödemeden düşülür</small></span>
                  <strong>{formatTl(deductions.reduce((sum, group) => sum + group.amount, 0))}</strong>
                </header>
                <ul className="reports-payout-rows">{deductions.map(row)}</ul>
              </section>}
              {waiting.length > 0 && <section className="reports-payout-day is-waiting" aria-label="Vadesi geçmiş, ödeme bekleyen">
                <header>
                  <UiIcon name="alertTriangle" size={16} />
                  <span className="reports-payout-day-name"><b>Vadesi Geçmiş, Ödeme Bekleyen</b><small>Ödeme günü belli değil</small></span>
                  <strong>{formatTl(waiting.reduce((sum, group) => sum + group.amount, 0))}</strong>
                </header>
                <ul className="reports-payout-rows">{waiting.map(row)}</ul>
              </section>}
              {undated.length > 0 && <section className="reports-payout-day is-waiting" aria-label="Vade tarihi belli olmayan, ödeme bekleyen">
                <header>
                  <UiIcon name="alertTriangle" size={16} />
                  <span className="reports-payout-day-name"><b>Vade Tarihi Belli Değil</b><small>Pazaryeri ödeme tarihini bildirmedi</small></span>
                  <strong>{formatTl(undated.reduce((sum, group) => sum + group.amount, 0))}</strong>
                </header>
                <ul className="reports-payout-rows">{undated.map(row)}</ul>
              </section>}
            </div>}</TabSlidePanel>
        </>}
    </Panel>
    <p className="reports-payout-coverage" role="note">
      {read.length > 0 ? `Okunan mağazalar: ${read.map(store => `${platformName(store.platformCode)} · ${store.storeName}${store.lastSyncedAt ? ` (${formatIstanbul(store.lastSyncedAt, { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })})` : ''}`).join(', ')}. ` : 'Henüz hiçbir mağazanın hakediş kaydı okunmadı. '}
      {data?.coverage.some(store => store.state === 'NOT_SUPPORTED') && `Henüz okunmayanlar: ${[...new Set(data.coverage.filter(store => store.state === 'NOT_SUPPORTED').map(store => platformName(store.platformCode)))].join(', ')}. `}
      Gelecek ödemeler tahminidir; kesin tutar ödeme günü pazaryeri panelinde görünür.
    </p>
  </div>
}

// The records of a payout group or of a search, a hundred at a time.
function PayoutLinesTable({ platforms, group, search }: { platforms: string[] | null; group?: PayoutGroup; search?: string }) {
  const [page, setPage] = useState(0)
  const query = payoutLinesQuery({ platforms, group, search, page })
  const result = useQuery({ queryKey: queryKeys.reportsPayoutLines(query), queryFn: async ({ signal }) => readPayoutLines(await hubApi<ApiAnswer<'getReportsPayoutsLines'>>(`/reports/payouts/lines?${query}`, { signal })), staleTime: 60_000, placeholderData: previous => previous })
  const data = result.data
  if (result.isError && !data) return <ErrorBox error={result.error} onRetry={() => void result.refetch()} />
  if (!data) return <Busy />
  if (data.items.length === 0) return <EmptyState title="Kayıt Bulunamadı">{search ? 'Bu numarayla kayıt bulunamadı.' : 'Seçili dönemde kayıt yok.'}</EmptyState>
  const pages = Math.max(1, Math.ceil(data.totalCount / data.pageSize))
  // In a group every record shares its region and state, so those columns show for a search only; days are short.
  const searching = group === undefined
  const day = (value: string) => formatIstanbul(value, { day: '2-digit', month: 'short' })
  return <>
    <div className="reports-table-wrap reports-payout-lines-wrap"><table className="reports-table reports-payout-lines">
      <thead><tr><th scope="col">İşlem</th><th scope="col">Sipariş / Fatura</th><th scope="col">Barkod</th><th scope="col">Tarih</th><th scope="col">Vade</th>
        {searching && <th scope="col">Bölge</th>}<th scope="col" className="is-num">Tutar</th><th scope="col" className="is-num">Komisyon</th><th scope="col" className="is-num">Hakediş</th>
        {searching && <th scope="col">Durum</th>}</tr></thead>
      <tbody>{data.items.map(line => <tr key={`${line.connectionId}:${line.externalId}`}>
        <th scope="row">{transactionLabel(line)}</th>
        <td>{line.orderNumber ?? line.invoiceNumber ?? line.externalId}</td>
        <td>{line.barcode ?? '—'}</td>
        <td>{day(line.transactionDate)}</td>
        <td>{line.dueDay ? shortDay(line.dueDay, false) : '—'}</td>
        {searching && <td>{line.country ?? (regionLabels[line.region] || '—')}</td>}
        <td className="is-num">{lineMoney(line.gross, line.currency)}</td>
        <td className="is-num">{line.commission === null ? '—' : lineMoney(line.commission, line.currency)}</td>
        <td className={`is-num ${line.net < 0 ? 'is-loss' : ''}`}>{lineMoney(line.net, line.currency)}</td>
        {searching && <td><em className={`reports-payout-badge is-${line.status.toLowerCase()}`}>{statusLabels[line.status]}</em></td>}
      </tr>)}</tbody>
      <tfoot><tr><th scope="row" colSpan={searching ? 8 : 7}>{formatCount(data.totalCount)} kayıt</th><td className="is-num">{formatTl(data.net)}</td>{searching && <td />}</tr></tfoot>
    </table></div>
    {pages > 1 && <div className="reports-order-pager"><Pager page={page + 1} totalPages={pages} onPageChange={next => setPage(next - 1)} ariaLabel="Sayfalama" /></div>}
  </>
}
