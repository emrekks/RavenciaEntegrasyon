import { useEffect, useMemo } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi, hubPoll } from '../../../shared/api'
import { useConfirmDialog } from '../../../shared/components'
import { newIdempotencyKey } from '../../../shared/idempotency'
import { appendNotification, failureNotice } from '../../../shared/notifications'
import { scopedStorageKey } from '../../../shared/storage-scope'
import { formatDate, formatDateTime } from '../../../shared/format-date'
import type { StatusExtras } from '../../dashboard/IntegrationStatusPanel'
import type { IntegrationFeedback } from '../shared'
import { orderSourceHint, priceStockMonitorQueryKey,priceStockProblemSignature, priceStockWriteState, relativeAgeText, type PriceStockConnectionMonitor, type PriceStockLostListing, type PriceStockMonitor } from './integration-helpers'
import './price-stock-monitor.css'

const notifiedKey = 'price-stock-monitor-notified'

function readNotified(): Record<string, string> {
  try { return JSON.parse(localStorage.getItem(scopedStorageKey(notifiedKey)) ?? '{}') as Record<string, string> } catch { return {} }
}

function writeNotified(value: Record<string, string>) {
  try { localStorage.setItem(scopedStorageKey(notifiedKey), JSON.stringify(value)) } catch { /* a private window keeps no history */ }
}

// A failed or refused price/stock push, or an offer the pipeline cannot send, raises one notification per change of
// the connection's problem set (not one per poll).
function notifyNewProblems(rows: PriceStockConnectionMonitor[]) {
  const notified = readNotified()
  let changed = false
  for (const row of rows) {
    const signature = priceStockProblemSignature(row)
    if (signature === (notified[row.connectionId] ?? null)) continue
    changed = true
    if (signature === null) { delete notified[row.connectionId]; continue }
    notified[row.connectionId] = signature
    const parts = [row.failedPushes7d ? `${row.failedPushes7d} başarısız gönderim` : '', row.manualReviewPushes ? `${row.manualReviewPushes} kısmi/inceleme bekleyen gönderim` : '', row.invalidOffers ? `${row.invalidOffers} gönderilemeyen teklif` : ''].filter(Boolean)
    appendNotification({
      title: 'Fiyat-Stok Gönderiminde Sorun',
      message: `${row.displayName}: ${parts.join(', ')}.${row.latestFailure?.errorSummary ? ` Son hata: ${row.latestFailure.errorSummary}` : ''}`,
      kind: 'error',
      platformCode: row.platformCode,
      connectionId: row.connectionId,
      code: row.latestFailure?.errorCode ?? null,
      jobId: row.latestFailure?.jobId ?? null,
      record: true,
      target: { href: '/jobs?sekme=api-saglik', label: 'API Sağlık’a Git' }
    })
  }
  if (changed) writeNotified(notified)
}

const writeOffHint = 'Otomatik fiyat ve stok gönderimi bu bağlantıda kapalı. Bu bir ayardır, hata değildir: panelde yapılan fiyat ve stok değişiklikleri pazaryerine kendiliğinden gönderilmez. Entegrasyon ayarlarından açılabilir.'

function writeTitle(label: string, tone: 'on' | 'partial' | 'off') {
  if (tone === 'on') return 'Fiyat ve stok değişiklikleri pazaryerine otomatik gönderiliyor.'
  if (tone === 'partial') return 'Fiyat ve stok yazma akışlarından yalnız biri açık; gönderimler ikisi de açılana kadar bekletiliyor.'
  return label.includes('Ana Anahtar') ? `${writeOffHint} Bağlantının ana anahtarı kapalı.` : writeOffHint
}

const exactAge = (value: string | null | undefined) => (value && relativeAgeText(value) ? `${relativeAgeText(value)} (${formatDateTime(value)})` : 'henüz yok')

// "MZ050YNC-01 … MZ050YNC-24" for a long run of stock codes, all of them for three or fewer.
function skuRange(listing: PriceStockLostListing) {
  const skus = listing.skus
  if (skus.length === 0) return ''
  return skus.length <= 3 ? skus.join(', ') : `${skus[0]} … ${skus[skus.length - 1]}`
}

/** The tooltip lines that name the listings the marketplace no longer holds: what that means, what to do and which products. */
export function lostListingLines(row: Pick<PriceStockConnectionMonitor, 'notFoundLinks' | 'rejectedLinks' | 'lostListings' | 'automaticWritesOpen'>): string[] {
  const lost = row.notFoundLinks + row.rejectedLinks
  if (!lost) return []
  const listings = row.lostListings ?? []
  const products = new Set(listings.map(listing => listing.productId)).size
  const counts = [row.notFoundLinks ? `${row.notFoundLinks.toLocaleString('tr-TR')} varyant bulunamadı` : '', row.rejectedLinks ? `${row.rejectedLinks.toLocaleString('tr-TR')} varyant reddedildi` : ''].filter(Boolean).join(', ')
  const lines = [`Pazaryerinde bulunamayan ilan: ${counts}${products ? ` (${products} ürün)` : ''}.`]
  lines.push(row.notFoundLinks
    ? 'Bu bir gönderim hatası değil: gece yapılan ürün taraması ve tekil sorgu bu kodları pazaryerinde artık bulamıyor (ürün pazaryerinden silinmiş ya da kodu değişmiş olabilir).'
    : 'Pazaryeri bu kodları fiyat-stok gönderiminde tanımadı.')
  if (!row.automaticWritesOpen) lines.push('Otomatik fiyat-stok gönderimi kapalı; bu ilanlara bir şey gönderilmiyor.')
  for (const listing of listings) {
    const state = listing.healthStatus === 'REJECTED' ? 'reddedildi' : 'bulunamadı'
    const range = skuRange(listing)
    lines.push(`• ${listing.title} · ${listing.variants} varyant ${state}${range ? ` (${range})` : ''}${listing.since ? ` · ${formatDate(listing.since)} tarihinden beri` : ''}`)
  }
  lines.push('Ürün pazaryerinde olmalıysa ürün kartından Yayınla ile yeniden gönderin; artık satılmayacaksa bu bağlantıdaki eşleştirmesini kaldırın.')
  return lines
}

/** What one connection adds to the Entegrasyon Durumu row: the automatic write state, the problem count and the details behind them. */
export function statusExtras(row: PriceStockConnectionMonitor): StatusExtras {
  const state = priceStockWriteState(row)
  const problems = row.failedPushes7d + row.manualReviewPushes
  const lost = row.notFoundLinks + row.rejectedLinks
  const total = problems + row.invalidOffers + lost
  const parts = [
    row.failedPushes7d ? `${row.failedPushes7d} başarısız gönderim` : '',
    row.manualReviewPushes ? `${row.manualReviewPushes} kısmi / inceleme bekliyor` : '',
    row.invalidOffers ? `${row.invalidOffers} gönderilemeyen teklif (pazaryerine gönderilemeyip bekletildi)` : ''
  ].filter(Boolean)
  const lines = parts.length ? [parts.join(' · ')] : []
  if (row.latestFailure) lines.push(`Son hata: ${row.latestFailure.errorSummary ?? row.latestFailure.errorCode ?? row.latestFailure.status}${row.latestFailure.at ? ` · ${exactAge(row.latestFailure.at)}` : ''}`)
  for (const issue of row.invalidOfferSamples) lines.push(`Gönderilmedi: ${issue.summary}`)
  lines.push(...lostListingLines(row))
  const queue = [`Son başarılı gönderim: ${exactAge(row.lastSuccessfulPushAt)}`, `Kuyruk: ${row.queuedJobs.toLocaleString('tr-TR')}${row.waitingForOrderRead ? ` (${row.waitingForOrderRead} gönderim sipariş okumasını bekliyor)` : ''}`]
  return {
    write: { label: state.label, tone: state.tone, title: [writeTitle(state.label, state.tone), ...queue].join('\n') },
    problem: total > 0 ? { text: total.toLocaleString('tr-TR'), tone: problems > 0 ? 'bad' : 'warn', title: lines.join('\n') } : null,
    sourceHint: `Sipariş kaynağı: ${orderSourceHint(row)}`,
    attention: total > 0
  }
}

/** The line above Entegrasyon Durumu: price/stock send problems and listings the marketplace no longer holds, told apart. */
export function priceStockMonitorNotice(rows: PriceStockConnectionMonitor[] | undefined): string | null {
  if (!rows) return null
  const sends = rows.filter(row => row.failedPushes7d + row.manualReviewPushes + row.invalidOffers > 0).length
  const lost = rows.filter(row => row.notFoundLinks + row.rejectedLinks > 0)
  const parts: string[] = []
  if (sends) parts.push(`${sends} bağlantıda sorunlu fiyat/stok gönderimi var.`)
  if (lost.length) {
    const names = lost.map(row => row.displayName).join(', ')
    const variants = lost.reduce((sum, row) => sum + row.notFoundLinks + row.rejectedLinks, 0)
    parts.push(`${names}: ${variants.toLocaleString('tr-TR')} varyantın ilanı pazaryerinde bulunamıyor.`)
  }
  return parts.length ? `${parts.join(' ')} Ayrıntı ve ürün listesi Sorun hücresinin ipucunda.` : null
}

/** The price/stock monitor read once (and its problem notifications raised), as per-connection extras for Entegrasyon Durumu. */
export function usePriceStockMonitor() {
  const monitor = useQuery({ queryKey: priceStockMonitorQueryKey, queryFn: () => hubPoll<PriceStockMonitor>('/price-stock-monitor'), refetchInterval: 60_000, refetchIntervalInBackground: false })
  const rows = monitor.data?.connections
  useEffect(() => { if (rows) notifyNewProblems(rows) }, [rows])
  const extras = useMemo(() => rows ? Object.fromEntries(rows.map(row => [row.connectionId, statusExtras(row)])) : undefined, [rows])
  const notice = useMemo(() => priceStockMonitorNotice(rows), [rows])
  return { rows, extras, notice, anyOpen: rows?.some(row => row.priceWriteEnabled || row.stockWriteEnabled) ?? false }
}

// The emergency stop: closes every automatic write at once, after a confirmation. It never opens one.
export function EmergencyStopButton({ anyOpen, onFeedback }: { anyOpen: boolean; onFeedback: (feedback: IntegrationFeedback) => void }) {
  const client = useQueryClient()
  const [confirm, confirmDialog] = useConfirmDialog()
  const stop = useMutation({
    mutationFn: () => hubApi<{ connectionsStopped: number; policiesDisabled: number; jobsCancelled: number }>('/price-stock-monitor/emergency-stop', { method: 'POST', headers: { 'Idempotency-Key': newIdempotencyKey() }, body: '{}' }),
    onSuccess: async result => {
      onFeedback({ kind: 'success', title: 'Otomatik Yazmalar Durduruldu', message: `${result.connectionsStopped} bağlantıda otomatik fiyat-stok gönderimi kapatıldı; ${result.jobsCancelled} gönderilmemiş otomatik iş iptal edildi. Panelden başlatılan gönderimler ve sonucu beklenen istekler sürer.` })
      await Promise.all([client.invalidateQueries({ queryKey: priceStockMonitorQueryKey }), client.invalidateQueries({ queryKey: ['sync-policies'] }), client.invalidateQueries({ queryKey: ['jobs'] })])
    },
    onError: reason => onFeedback(failureNotice(reason, 'Otomatik yazmalar durdurulamadı.'))
  })
  const confirmStop = async () => {
    const accepted = await confirm({ title: 'Tüm Otomatik Yazmalar Durdurulsun mu?', message: 'Her bağlantıda otomatik fiyat ve stok gönderimi kapatılır ve bekleyen fiyat-stok işleri iptal edilir. Yeniden açtığınızda kapalıyken yapılan değişiklikler gönderilmez; stok ve fiyatları elle kontrol etmeniz gerekir.', confirmLabel: 'Tümünü Durdur', destructive: true })
    if (accepted) stop.mutate()
  }
  return <>{confirmDialog}<button type="button" className="psm-stop" disabled={stop.isPending || !anyOpen} title={anyOpen ? undefined : 'Açık otomatik yazma yok.'} onClick={() => void confirmStop()}>{stop.isPending ? 'Durduruluyor…' : 'Tüm Otomatik Yazmaları Durdur'}</button></>
}
