import { useRef, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { Modal } from '../../shared/components'
import { newIdempotencyKey } from '../../shared/idempotency'
import { formatIstanbul } from '../../shared/format-date'
import { formatCount, formatNumber, formatPercent, formatTl } from './report-format'

// Trendyol's product ads report the seller uploads ("Ürün Reklamları Raporum", GET /reports/ad-report). Every figure is a
// campaign's total since it started, as of the export: it is never the selected period's figure and is never mixed with
// the advertising charged on Trendyol's statement (Trendyol Reklamı).
export type AdCampaign = {
  name: string; status: string | null; start: string | null; end: string | null; productCount: number | null; totalBudget: number | null; dailyBudget: number | null
  remainingBudget: number | null; spent: number | null; cpcBid: string | null; actualCpc: number | null; clicks: number | null; impressions: number | null
  directSales: number | null; indirectSales: number | null; totalSales: number | null; directRevenue: number | null; indirectRevenue: number | null; totalRevenue: number | null; roas: number | null
}
export type AdTotals = {
  spent: number; revenue: number; directRevenue: number; indirectRevenue: number; roas: number | null; clicks: number; impressions: number
  cpc: number | null; ctr: number | null; sales: number; directSales: number; indirectSales: number
}
export type AdReport = { id: string; connectionId: string; storeName: string; fileName: string; exportedAt: string; exportedAtFromFileName: boolean; uploadedAt: string; totals: AdTotals; campaigns: AdCampaign[] }
export type AdReports = { reports: AdReport[]; stores: Array<{ connectionId: string; storeName: string }> }

export const adReportQuery = { queryKey: ['reports', 'ad-report'], queryFn: () => hubApi<AdReports>('/reports/ad-report') }

// A report older than this is said to be old: the totals have moved on since.
const staleDays = 7
const dayMs = 86_400_000

export const reportAge = (report: AdReport, now = Date.now()) => Math.floor((now - new Date(report.exportedAt).getTime()) / dayMs)
// ROAS with two decimals, as Trendyol writes Harcama Getirisi (3,83).
const roasText = (value: number) => value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const when = (value: string) => formatIstanbul(value, { dateStyle: 'medium', timeStyle: 'short' })

// The stores' newest reports added up: with one store its own totals; with more, the sums and the rates from them.
export function combined(reports: AdReport[]): AdTotals | null {
  if (!reports.length) return null
  if (reports.length === 1) return reports[0].totals
  const sum = (pick: (totals: AdTotals) => number) => reports.reduce((total, report) => total + pick(report.totals), 0)
  const spent = sum(totals => totals.spent), revenue = sum(totals => totals.revenue), clicks = sum(totals => totals.clicks), impressions = sum(totals => totals.impressions)
  return {
    spent, revenue, directRevenue: sum(totals => totals.directRevenue), indirectRevenue: sum(totals => totals.indirectRevenue), clicks, impressions,
    roas: spent > 0 ? revenue / spent : null, cpc: clicks > 0 ? spent / clicks : null, ctr: impressions > 0 ? clicks / impressions : null,
    sales: sum(totals => totals.sales), directSales: sum(totals => totals.directSales), indirectSales: sum(totals => totals.indirectSales)
  }
}

// The card's row: ROAS and the ads' revenue of the newest report, or that none was uploaded; an old report is marked.
export function adReportRow(data: AdReports | undefined, failed: boolean, now = Date.now()) {
  const reports = data?.reports ?? []
  const totals = combined(reports)
  const oldest = reports.length ? Math.max(...reports.map(report => reportAge(report, now))) : null
  const stale = oldest !== null && oldest > staleDays
  const value = failed ? '—' : !data ? '…' : !totals ? 'Yüklenmedi' : totals.roas === null ? 'ROAS —' : `ROAS ${roasText(totals.roas)}`
  const sub = failed || !data ? undefined : !totals ? 'yüklemek için tıklayın'
    : `${formatTl(totals.revenue)} ciro${stale ? ' · eski' : ''}`
  const note = !totals
    ? 'Trendyol reklam raporu: kampanyaların harcaması, reklam cirosu ve ROAS.\nTrendyol API\'si reklam performansını vermediği için Reklam Yönetimi\'nden indirilen "Ürün Reklamları Raporum" dosyası yüklenir. Yüklemek için tıklayın.'
    : `Trendyol reklam raporu, kampanya toplamı; son rapor: ${reports.map(report => when(report.exportedAt)).join(', ')}.\nKampanyaların başlangıcından rapor anına kadarki toplamlar; seçili dönemin rakamı değildir ve Trendyol Reklamı (cari hesaptaki reklam bedeli) ile toplanmaz. Uyarı rengi: rapor ${formatCount(staleDays)} günden eskiyse.${stale ? ` Bu rapor ${formatCount(oldest)} gün önce alındı; yenisini yükleyin.` : ''} Ayrıntı ve kampanyalar için tıklayın.`
  return { value, sub, note, tone: stale ? 'warn' as const : undefined }
}

type Message = { kind: 'success' | 'error' | 'info'; text: string; warnings?: string[] }

// The report in a window: what it says in total (labelled a campaign total with its date), the campaigns, and the upload.
export function AdReportModal({ open, onClose, data }: { open: boolean; onClose: () => void; data: AdReports | undefined }) {
  const client = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const stores = data?.stores ?? []
  const [store, setStore] = useState<string>('')
  const [message, setMessage] = useState<Message | null>(null)
  const target = store || stores[0]?.connectionId || ''
  const upload = useMutation({
    mutationFn: (file: File) => {
      const body = new FormData()
      body.append('file', file)
      return hubApi<{ report: AdReport; warnings: string[] }>(`/connections/${target}/ad-reports`, { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey('ad-report') }, body })
    },
    onSuccess: async result => {
      setMessage({ kind: result.warnings.length ? 'info' : 'success', text: `${formatCount(result.report.campaigns.length)} kampanya yüklendi; rapor zamanı ${when(result.report.exportedAt)}.`, warnings: result.warnings })
      await client.invalidateQueries({ queryKey: adReportQuery.queryKey })
    },
    onError: reason => setMessage({ kind: 'error', text: reason instanceof Error ? reason.message : 'Dosya yüklenemedi.' })
  })
  const choose = (file: File | undefined) => {
    if (!file) return
    if (!file.name.toLowerCase().endsWith('.xlsx')) { setMessage({ kind: 'error', text: 'Yalnız Trendyol\'dan indirilen .xlsx dosyası yüklenebilir.' }); return }
    if (!target) { setMessage({ kind: 'error', text: 'Rapor yüklenecek Trendyol mağazası yok.' }); return }
    setMessage(null)
    upload.mutate(file)
  }
  const reports = data?.reports ?? []
  const totals = combined(reports)
  const now = Date.now()
  const figures: Array<[string, string, string]> = totals ? [
    ['Harcanan', formatTl(totals.spent), 'Kampanyalarda tıklamalara harcanan bütçe.'],
    ['Reklam Cirosu', formatTl(totals.revenue), `Doğrudan ${formatTl(totals.directRevenue)} · dolaylı ${formatTl(totals.indirectRevenue)}`],
    ['ROAS', totals.roas === null ? '—' : roasText(totals.roas), 'Reklam cirosu ÷ harcanan.'],
    ['TBM', totals.cpc === null ? '—' : formatTl(totals.cpc), 'Tıklama başına maliyet: harcanan ÷ tıklanma.'],
    ['Tıklanma Oranı', totals.ctr === null ? '—' : formatPercent(totals.ctr, 2), `${formatCount(totals.clicks)} tıklanma ÷ ${formatCount(totals.impressions)} görüntülenme`],
    ['Reklamla Satılan', `${formatNumber(totals.sales)} adet`, `Doğrudan ${formatNumber(totals.directSales)} · dolaylı ${formatNumber(totals.indirectSales)}`]
  ] : []
  return <Modal open={open} onClose={onClose} className="reports-ad-modal" title="Trendyol Reklam Raporu"
    description="Kampanya toplamı: kampanyaların başlangıcından rapor anına kadar. Seçili dönemin rakamı değildir.">
    <div className="reports-ad">
      {reports.map(report => {
        const age = reportAge(report, now)
        return <p key={report.id} className={`reports-ad-source${age > staleDays ? ' is-stale' : ''}`}>
          <b>{report.storeName}</b> · son rapor: {when(report.exportedAt)}{report.exportedAtFromFileName ? '' : ' (yükleme zamanı)'} · {report.fileName}
          {age > staleDays && <> · {formatCount(age)} gün önce alındı, yenisini yükleyin</>}
        </p>
      })}
      {totals ? <dl className="reports-ad-figures">{figures.map(([label, value, note]) => <div key={label}><dt>{label}</dt><dd>{value}</dd><small>{note}</small></div>)}</dl>
        : <p className="reports-empty">{data ? 'Rapor yüklenmedi.' : 'Yükleniyor…'}</p>}
      <p className="reports-note">Cari hesaptaki Trendyol Reklamı (Reklam Bedeli) reklam bütçesi yükleme ve faturaların tarihine göredir; bu rapor ise tıklamalara harcanan tutarı gösterir. İkisi ayrı tutulur, toplanmaz.</p>
      {reports.length > 0 && <div className="reports-table-wrap"><table className="reports-table reports-ad-campaigns">
        <thead><tr><th>Kampanya</th><th>Durum</th><th className="is-num">Harcanan</th><th className="is-num">Reklam Cirosu</th><th className="is-num">ROAS</th></tr></thead>
        <tbody>{reports.flatMap(report => report.campaigns.map((campaign, index) => <tr key={`${report.id}-${index}`}>
          <td>{campaign.name}{reports.length > 1 && <small> · {report.storeName}</small>}</td><td>{campaign.status ?? '—'}</td>
          <td className="is-num">{campaign.spent === null ? '—' : formatTl(campaign.spent)}</td>
          <td className="is-num">{campaign.totalRevenue === null ? '—' : formatTl(campaign.totalRevenue)}</td>
          <td className="is-num">{campaign.spent && campaign.totalRevenue !== null ? roasText(campaign.totalRevenue / campaign.spent) : '—'}</td>
        </tr>))}</tbody>
      </table></div>}
      <div className="reports-ad-upload">
        {stores.length > 1 && <label>Mağaza<select value={target} onChange={event => setStore(event.target.value)}>{stores.map(item => <option key={item.connectionId} value={item.connectionId}>{item.storeName}</option>)}</select></label>}
        <button type="button" className="secondary" disabled={upload.isPending || !target} onClick={() => input.current?.click()}>{upload.isPending ? 'Dosya Okunuyor…' : 'Rapor Yükle'}</button>
        <input ref={input} type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" hidden onChange={event => { choose(event.target.files?.[0]); event.target.value = '' }} />
        <small>Trendyol satıcı panelinde Reklam Yönetimi'nden indirilen "Ürün Reklamları Raporum" .xlsx dosyası; dosya adındaki saat rapor zamanı sayılır. Her yükleme ayrı saklanır, en yenisi gösterilir.</small>
      </div>
      {message && <div className={`reports-ad-message is-${message.kind}`} role={message.kind === 'error' ? 'alert' : 'status'}><p>{message.text}</p>{message.warnings?.length ? <ul>{message.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul> : null}</div>}
    </div>
  </Modal>
}
