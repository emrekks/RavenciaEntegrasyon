import type { ApiAnswer } from '../../../shared/api/api-answer'
import { useEffect, useMemo, useRef, useState, type DragEvent } from 'react'
import { DateRangePicker } from '../../../shared/DateRangePicker'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../../shared/api'
import { Busy, ErrorBox, TitleHint, useConfirmDialog } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { formatIstanbul } from '../../../shared/format-date'
import { parseTrNumber } from '../../../shared/tr-number'
import '../commission-tariffs.css'
import { forgetTariff, loadRowPage, rememberTariff, tariffRowPaging, tariffsQuery } from './commission-tariff-rows'
import { EmptyState } from '../../../shared/components/EmptyState'
import { queryKeys } from '../../../shared/api/query-keys'
import { type CommissionTariff, lastMinute, shortDay, periodName, weekTabLabel, periodLabel, type TariffFilter, noFilter } from './commission-tariffs/tariff-helpers'
import { TariffPlan } from './commission-tariffs/TariffPlan'
export { type CommissionBand, weekAverage, type CommissionTariffRow, type CommissionChoice, type TariffPick, noBulk, type TariffSelection, selectionBody, selectionState, exportMessage, bulkChoice, pickedTier, ownChoice, groupByModel, stockText, priceRange, bandLimitText, bandLimitHint, amountRange, bestTiers, mostProfit, rateText, rateDisagrees, modelsPerView, phoneQuery, selectionSaveKey, selectionSaveSettled, useSavedSelection, ownPriceValue } from './commission-tariffs/TariffPlan'
export { type CommissionTariff, periodDays, periodName, weekTabLabel, type TariffBulk, ProductImage, type TariffFilter, noFilter, type TariffSort } from './commission-tariffs/tariff-helpers'
export { nextSort, FilterField, FilterRange, FilterActions, filterEntries, filterHasValue, SheetScrim, SheetHead, PhoneSearch } from './commission-tariffs/TariffFilters'

// The marketplaces whose commission tariff export the panel reads (CommissionTariffPlatforms on the server).
export const commissionTariffPlatforms = new Set(['TRENDYOL'])

// An uploaded file with its periods, the earliest first.
export type TariffGroup = { id: string; periods: CommissionTariff[] }

// The listed tariffs by file, in the list's order (the latest file first); a tariff without a group is a file of its own.
export function tariffGroups(tariffs: CommissionTariff[]): TariffGroup[] {
  const groups = new Map<string, CommissionTariff[]>()
  for (const tariff of tariffs) {
    const key = tariff.groupId ?? tariff.id
    groups.set(key, [...(groups.get(key) ?? []), tariff])
  }
  return [...groups].map(([id, periods]) => ({ id, periods: [...periods].sort((a, b) => Date.parse(a.periodStart) - Date.parse(b.periodStart)) }))
}

// The period a file opens on: the one in force now, else its first.
export const defaultPeriod = (periods: CommissionTariff[]) => periods.find(period => period.isCurrent) ?? periods[0] ?? null

// The span of a file's periods: "29 Eki 2026 – 5 Kas 2026".
export function groupRangeText(periods: CommissionTariff[]) {
  if (!periods.length) return '—'
  const end = periods.reduce((last, period) => Date.parse(period.periodEnd) > Date.parse(last) ? period.periodEnd : last, periods[0].periodEnd)
  return `${shortDay(periods[0].periodStart, true)} – ${shortDay(lastMinute(end), true)}`
}
// An instant's Istanbul calendar day as a date field holds it ("2026-10-29").
const istanbulDayFormat = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Istanbul', year: 'numeric', month: '2-digit', day: '2-digit' })
export const istanbulDay = (value: string | number) => istanbulDayFormat.format(new Date(value))
// The validity days the seller picks (Geçerlilik Başlangıcı / Bitişi): Istanbul days, both included.
export type ValidityDays = { from: string; to: string }
// The days a file's periods cover, as its "Tarihleri Değiştir" fields open on: the first period's start day and the day
// of the last period's last moment (the stored end is exclusive).
export function validityDays(periods: CommissionTariff[]): ValidityDays {
  const end = periods.reduce((last, period) => Date.parse(period.periodEnd) > last ? Date.parse(period.periodEnd) : last, Date.parse(periods[0].periodEnd))
  return { from: istanbulDay(periods[0].periodStart), to: istanbulDay(end - 1) }
}
// Why the picked days cannot be used yet; null when both are picked and in order.
export function validityProblem(days: ValidityDays): string | null {
  if (!days.from || !days.to) return 'Önce geçerlilik başlangıcı ve bitişini seçin.'
  if (days.from > days.to) return 'Geçerlilik başlangıcı, bitişten sonra olamaz.'
  return null
}

// The days a tariff is valid for, picked as one range on a two-month calendar (days to come too).
function ValidityFields({ days, onChange, disabled }: { days: ValidityDays; onChange: (days: ValidityDays) => void; disabled?: boolean }) {
  return <DateRangePicker className="commission-validity" label="Geçerlilik Tarihleri" placeholder="Başlangıç – Bitiş" future disabled={disabled}
    from={days.from} to={days.to} onChange={range => onChange({ from: range.from, to: range.to })} />
}

// A period's tab: its name (else its length in days) and its days, "3 Gün (29 Eki – 1 Kas 2026)".
export function periodTabLabel(period: CommissionTariff) {
  return `${periodName(period)} (${shortDay(period.periodStart, false)} – ${shortDay(lastMinute(period.periodEnd), true)})`
}

// A page of a tariff's rows as the tab shows it: its models in the filter's order, 50 at a time.
export const tariffRowsQuery = (connectionId: string, tariffId: string, filter: TariffFilter = noFilter, page = 0) => ({
  queryKey: queryKeys.commissionTariffRows(connectionId, tariffId, filter, page),
  queryFn: ({ signal }: { signal: AbortSignal }) => loadRowPage(connectionId, tariffId, tariffRowPaging(filterQuery(filter), page, 50), signal)
})


// Weekly commission tariffs the seller downloads from the marketplace and uploads here. The panel reads a sale's
// commission from the band its price falls in when the order does not carry it, and shows which price earns a lower
// band. Nothing is sent to the marketplace.
export function CommissionTariffsPanel({ connectionId, platformName }: { connectionId: string; platformName: string }) {
  const client = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)
  const [selected, setSelected] = useState<string | null>(null)
  const [filter, setFilter] = useState<TariffFilter>(noFilter)
  const [page, setPage] = useState(0)
  const [message, setMessage] = useState<{ kind: 'success' | 'error' | 'info' | 'warning'; text: string; warnings?: string[] } | null>(null)
  // The days the next upload is valid for, and the file whose days are being changed.
  const [uploadDays, setUploadDays] = useState<ValidityDays>({ from: '', to: '' })
  const uploadProblem = validityProblem(uploadDays)
  const [editing, setEditing] = useState<{ groupId: string; days: ValidityDays } | null>(null)
  const [confirm, confirmDialog] = useConfirmDialog()
  const tariffs = useQuery(tariffsQuery(connectionId))
  // The list shows a file once; its periods are the tabs over the table. The open period is the one picked, else the
  // period in force now of the file in force now, else the first file's.
  const groups = useMemo(() => tariffGroups(tariffs.data ?? []), [tariffs.data])
  const activeGroup = groups.find(group => group.periods.some(period => period.id === selected)) ?? groups.find(group => group.periods.some(period => period.isCurrent)) ?? groups[0] ?? null
  // The 7-day tab of a file with two periods (Trendyol's 3 Gün and 4 Gün): the first period's table with the second's
  // commission and profit beside each band, and a pick made for both.
  const [week, setWeek] = useState(false)
  const weekPeriods = week && activeGroup?.periods.length === 2 ? activeGroup.periods : null
  const active = weekPeriods ? weekPeriods[0] : activeGroup?.periods.find(period => period.id === selected) ?? (activeGroup ? defaultPeriod(activeGroup.periods) : null)
  const rows = useQuery({ ...tariffRowsQuery(connectionId, active?.id ?? '', filter, page), enabled: active !== null, retry: false })
  // The tariff the tab opens on by itself, asked for at once the next time Promosyon opens (prefetchLastTariff).
  const opensOn = selected === null && !weekPeriods ? active?.id ?? null : null
  useEffect(() => { if (opensOn) rememberTariff({ connectionId, tariffId: opensOn }) }, [connectionId, opensOn])
  // The tariff the tab opened on by itself has no rows to read any more: forget it instead of asking for it again.
  useEffect(() => { if (opensOn && rows.isError) forgetTariff() }, [opensOn, rows.isError])
  const upload = useMutation({
    mutationFn: ({ file, days }: { file: File; days: ValidityDays }) => {
      const body = new FormData()
      body.append('file', file)
      body.append('validFrom', days.from)
      body.append('validTo', days.to)
      return hubApi<ApiAnswer<'postConnectionsByConnectionIdCommissionTariffs'>>(`/connections/${connectionId}/commission-tariffs`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff') }, body })
    },
    onSuccess: async result => {
      setSelected(defaultPeriod(result.tariffs)?.id ?? null)
      setWeek(false)
      setPage(0)
      setMessage(result.alreadyUploaded
        ? { kind: 'info', text: 'Bu dosya bu dönem için daha önce yüklenmiş; mevcut tarife gösteriliyor.', warnings: result.warnings }
        : { kind: result.skippedRows ? 'info' : 'success', text: `${result.importedRows.toLocaleString('tr-TR')} ürünün tarifesi yüklendi${result.skippedRows ? `; ${result.skippedRows.toLocaleString('tr-TR')} satır okunamadı` : ''}.`, warnings: result.warnings })
      await client.invalidateQueries({ queryKey: queryKeys.commissionTariffs(connectionId) })
    },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Tarife yüklenemedi.' })
  })
  const remove = useMutation({
    mutationFn: (tariff: CommissionTariff) => hubApi<ApiAnswer<'deleteConnectionsByConnectionIdCommissionTariffsByTariffId'>>(`/connections/${connectionId}/commission-tariffs/${tariff.id}`, { method: 'DELETE', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-delete') } }),
    onSuccess: async () => {
      setSelected(null)
      setMessage({ kind: 'success', text: 'Tarife silindi.' })
      await client.invalidateQueries({ queryKey: queryKeys.commissionTariffs(connectionId) })
    },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Tarife silinemedi.' })
  })
  // A file's days changed: every period of it (wholeFile), the boundaries between its periods kept.
  const changeDays = useMutation({
    mutationFn: ({ tariffId, days }: { tariffId: string; days: ValidityDays }) => hubApi<ApiAnswer<'putConnectionsByConnectionIdCommissionTariffsByTariffIdPeriod'>>(`/connections/${connectionId}/commission-tariffs/${tariffId}/period`, {
      method: 'PUT', headers: { 'Idempotency-Key': newIdempotencyKey('commission-tariff-period') }, body: JSON.stringify({ validFrom: days.from, validTo: days.to, wholeFile: true })
    }),
    onSuccess: async () => {
      setEditing(null)
      setMessage({ kind: 'success', text: 'Tarifenin geçerlilik tarihleri değiştirildi.' })
      await client.invalidateQueries({ queryKey: queryKeys.commissionTariffs(connectionId) })
    },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Tarihler değiştirilemedi.' })
  })
  const choose = (file: File | undefined) => {
    if (!file) return
    if (uploadProblem) { setMessage({ kind: 'error', text: uploadProblem }); return }
    if (!file.name.toLowerCase().endsWith('.xlsx')) { setMessage({ kind: 'error', text: 'Yalnız pazaryerinden indirilen .xlsx tarife dosyası yüklenebilir.' }); return }
    setMessage(null)
    upload.mutate({ file, days: uploadDays })
  }
  const onDrop = (event: DragEvent<HTMLDivElement>) => { event.preventDefault(); setDragging(false); choose(event.dataTransfer.files[0]) }
  // A file goes with all its periods (the server deletes them together).
  const askDelete = async (group: TariffGroup) => {
    const periods = group.periods.map(period => period.periodText).join(' ve ')
    const many = group.periods.length > 1
    if (!(await confirm({ title: 'Tarife Silinsin mi?', message: `${periods} ${many ? 'dönemlerine' : 'dönemine'} ait tarife, ${group.periods[0].rowCount.toLocaleString('tr-TR')} ürün satırı ve kaydedilen seçimler silinecek. ${many ? 'Bu dönemlerin' : 'Bu dönemin'} bant ve kâr hesabı, Avantajlı Ürün Etiketi ve Flash Ürünler'de de bu tarifeden yapılamaz.`, confirmLabel: 'Tarifeyi Sil', destructive: true }))) return
    remove.mutate(group.periods[0])
  }
  const pageCount = rows.data ? Math.max(1, Math.ceil(rows.data.totalCount / rows.data.pageSize)) : 1

  return <section className="panel commission-tariffs" aria-labelledby="commission-tariffs-title">
    <div className="cs-sec-head"><h3 id="commission-tariffs-title">Komisyon Tarifeleri<TitleHint label="Komisyon Tarifeleri">{platformName}’un komisyon tarifesi dosyasını geçerli günleriyle yükleyin. Her ürünün fiyatının düştüğü bandı, bantların komisyonunu ve kalan kârı, alt banda inmek için gereken fiyatı görürsünüz; Avantajlı Ürün Etiketi ve Flash Ürünler de bu bantları kullanır. Raporlardaki komisyon tarifeden değil, sipariş ve finans kayıtlarından gelir. Pazaryerine hiçbir şey gönderilmez.</TitleHint></h3></div>
    <div className={`commission-dropzone${dragging ? ' is-dragging' : ''}`} onDragOver={event => { event.preventDefault(); setDragging(true) }} onDragLeave={() => setDragging(false)} onDrop={onDrop}>
      <ValidityFields days={uploadDays} onChange={setUploadDays} disabled={upload.isPending} />
      <strong>{upload.isPending ? 'Tarife okunuyor…' : 'Tarife dosyasını buraya bırakın'}</strong>
      <span>{platformName} satıcı panelinden indirilen .xlsx dosyası · en fazla 10 MiB</span>
      <small className="commission-validity-note">Yeni dosya yüklenince önceki dosya ve satırları silinir; yürürlükteki başka haftanın tarifesi bitene kadar kalır.</small>
      <button type="button" className="secondary" disabled={upload.isPending || uploadProblem !== null} onClick={() => input.current?.click()}>Dosya Seç</button>
      {uploadProblem && <small className="commission-validity-hint">{uploadProblem}</small>}
      <input ref={input} type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" hidden onChange={event => { choose(event.target.files?.[0]); event.target.value = '' }} />
    </div>
    {message && <div className={`commission-message is-${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'}><p>{message.text}</p>{message.warnings?.length ? <ul>{message.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul> : null}</div>}

    {tariffs.isLoading && <Busy />}
    {tariffs.isError && <ErrorBox error={tariffs.error} onRetry={() => void tariffs.refetch()} />}
    {tariffs.data && tariffs.data.length === 0 && <EmptyState icon="percent" title="Henüz Tarife Yok">Komisyon yalnız siparişin kendi bilgisinden okunuyor.</EmptyState>}
    {groups.length > 0 && <div className="commission-periods" role="list">
      {groups.map(group => {
        const first = group.periods[0]
        const on = group.id === activeGroup?.id
        const many = group.periods.length > 1
        return <div key={group.id} role="listitem" className={`commission-period${on ? ' is-on' : ''}`}>
          <button type="button" className="commission-period-main" aria-pressed={on} onClick={() => { setSelected(defaultPeriod(group.periods)?.id ?? null); setWeek(false); setPage(0) }}>
            <span className="commission-period-title">{periodLabel(group.periods)}{group.periods.some(period => period.isCurrent) && <em className="commission-badge is-current">Geçerli</em>}</span>
            <small>{many ? `${group.periods.length.toLocaleString('tr-TR')} dönem (${group.periods.map(period => period.periodLabel?.trim() || period.periodText).join(', ')}) · ` : ''}{first.rowCount.toLocaleString('tr-TR')} ürün · {first.matchedRowCount.toLocaleString('tr-TR')} panelde eşleşti · {first.fileName} · {formatIstanbul(first.uploadedAt, { dateStyle: 'medium', timeStyle: 'short' })}</small>
          </button>
          <div className="commission-period-actions">
            <button type="button" className="secondary commission-period-days" aria-expanded={editing?.groupId === group.id} disabled={changeDays.isPending}
              onClick={() => setEditing(editing?.groupId === group.id ? null : { groupId: group.id, days: validityDays(group.periods) })}>Tarihleri Değiştir</button>
            <button type="button" className="secondary commission-period-delete is-danger-outline" disabled={remove.isPending} onClick={() => void askDelete(group)} aria-label={`${group.periods.map(period => period.periodText).join(' ve ')} tarifesini sil`}>Sil</button>
          </div>
          {editing?.groupId === group.id && (() => {
            const problem = validityProblem(editing.days)
            return <form className="commission-period-edit" aria-label="Geçerlilik Tarihleri" onSubmit={event => { event.preventDefault(); if (!problem) changeDays.mutate({ tariffId: first.id, days: editing.days }) }}>
              <ValidityFields days={editing.days} onChange={days => setEditing({ groupId: group.id, days })} disabled={changeDays.isPending} />
              <div className="commission-period-edit-actions">
                <button type="submit" disabled={changeDays.isPending || problem !== null}>{changeDays.isPending ? 'Kaydediliyor…' : 'Kaydet'}</button>
                <button type="button" className="secondary" disabled={changeDays.isPending} onClick={() => setEditing(null)}>Vazgeç</button>
              </div>
              {problem && <small className="commission-validity-hint">{problem}</small>}
              {many && <small className="commission-validity-note">Dosyanın {group.periods.length.toLocaleString('tr-TR')} dönemi var; seçilen günler ilk dönemin başlangıcını ve son dönemin bitişini değiştirir, dönemler arasındaki sınır aynı kalır.</small>}
            </form>
          })()}
        </div>
      })}
    </div>}

    {/* A file with more than one period: its span and a tab for each period; the table, the band cards, Toplu Seçim and
        the picks are the open period's. */}
    {activeGroup && active && activeGroup.periods.length > 1 && <div className="commission-period-switch">
      <span className="commission-period-range">Geçerli Tarih Aralığı: <strong>{groupRangeText(activeGroup.periods)}</strong></span>
      <div className="commission-period-tabs" role="group" aria-label="Tarife dönemi">
        {activeGroup.periods.map(period => <button key={period.id} type="button" aria-pressed={!weekPeriods && period.id === active.id} onClick={() => { setSelected(period.id); setWeek(false); setPage(0) }}>
          {periodTabLabel(period)}{period.isCurrent && <em>Şu An</em>}
        </button>)}
        {activeGroup.periods.length === 2 && <button type="button" aria-pressed={weekPeriods !== null} onClick={() => { setSelected(activeGroup.periods[0].id); setWeek(true); setPage(0) }}>
          {weekTabLabel(activeGroup.periods)}
        </button>}
      </div>
    </div>}

    {active && <TariffPlan key={`${active.id}${weekPeriods ? '-week' : ''}`} connectionId={connectionId} tariff={active} partner={weekPeriods?.[1]} periods={activeGroup?.periods ?? [active]} rows={rows.data} loading={rows.isLoading} error={rows.isError ? rows.error : null} onRetry={() => void rows.refetch()}
      filter={filter} onFilter={value => { setFilter(value); setPage(0) }} page={page} pageCount={pageCount} onPage={setPage} onMessage={setMessage} />}
    {confirmDialog}
  </section>
}

// The rows query's parameters for a filter: empty fields left out, a stock that is not a number left out.
export function filterQuery(filter: TariffFilter): Record<string, string> {
  const query: Record<string, string> = {}
  for (const key of ['tier', 'cost', 'name', 'barcode', 'model', 'category', 'brand'] as const) if (filter[key].trim()) query[key] = filter[key].trim()
  for (const key of ['minStock', 'maxStock'] as const) {
    // Read as Turkish: "1.250" is 1250, never 1.25.
    const value = parseTrNumber(filter[key])
    if (value !== null && value >= 0) query[key] = String(value)
  }
  // A profit range counts only with a band picked; a loss is typed with a minus sign.
  if (filter.profitTier) {
    let ranged = false
    for (const key of ['minProfit', 'maxProfit', 'minShare', 'maxShare'] as const) {
      const value = parseTrNumber(filter[key] ?? '')
      if (value !== null) { query[key] = String(value); ranged = true }
    }
    if (ranged) query.profitTier = filter.profitTier
  }
  if (filter.sort) { query.sort = filter.sort; query.dir = filter.dir ?? 'asc' }
  if (filter.search?.trim()) query.search = filter.search.trim()
  return query
}
