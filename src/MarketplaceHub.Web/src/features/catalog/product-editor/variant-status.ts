import type { VariantPlatformStatus } from '../shared'
import { isProductUpdateInFlight } from '../product-platform-status'

// The status light before each variant's options in the editor's variant table (owner, 14 Nov 2026: "yeşil kırmızı
// yanan, hatalarını göstersin, üstüne gelince"). Red when the row has a field problem or a marketplace refused or lost
// the variant; green only when nothing is wrong and at least one marketplace has the variant; grey otherwise (nothing
// known yet). A state is never inferred from a missing field: a connection without a listing or link says nothing.
// Blue (pending) while the marketplace's word is awaited: approval pending, a refused variant sent again, or an update
// of its card on its way; never green then (owner, 15 Nov 2026: the list mark and these lights agree).
export type VariantStatusTone = 'ok' | 'error' | 'pending' | 'unknown'
export type VariantStatusLine = { tone: 'ok' | 'error' | 'pending' | 'info'; text: string }
export type VariantStatus = { tone: VariantStatusTone; lines: VariantStatusLine[] }

type Row = { barcodeIssue?: string; skuIssue?: string; salePrice: number; listPrice: number; platformStatuses?: VariantPlatformStatus[] | null }

export function variantFieldIssues(row: Row): string[] {
  const issues: string[] = []
  if (row.barcodeIssue) issues.push(row.barcodeIssue)
  if (row.skuIssue) issues.push(row.skuIssue)
  if (!(row.salePrice > 0)) issues.push('Satış fiyatı 0\'dan büyük olmalı.')
  if (!(row.listPrice > 0)) issues.push('Liste fiyatı 0\'dan büyük olmalı.')
  else if (row.salePrice > 0 && row.listPrice < row.salePrice) issues.push('Liste fiyatı satış fiyatından küçük olamaz.')
  return issues
}

function platformLine(status: VariantPlatformStatus): VariantStatusLine | null {
  const name = status.platform || status.platformCode
  const listing = status.listingStatus?.trim().toUpperCase() ?? ''
  // The card's own state (its profile): an update job writes UPDATE_QUEUED / UPDATE_*_SUBMITTED / UPDATE_IN_PROGRESS.
  const updating = isProductUpdateInFlight(status.status)
  const reason = status.rejectionReason?.trim() ? ` · ${status.rejectionReason.trim()}` : ''
  if (listing === 'REJECTED' && updating) return { tone: 'pending', text: `${name}: Onaya gönderildi (yeniden), sonuç bekleniyor${reason ? ` · Önceki ret${reason}` : ''}` }
  if (listing === 'REJECTED') return { tone: 'error', text: `${name}: Reddedildi${reason}` }
  if (status.isMissing) return { tone: 'error', text: `${name}: Pazaryerinde bulunamadı` }
  if (listing === 'APPROVAL_PENDING') return { tone: 'pending', text: `${name}: Onay bekliyor` }
  if ((status.isMatched || listing === 'LIVE') && updating) return { tone: 'pending', text: `${name}: Yayında · güncelleniyor` }
  if (status.isMatched || listing === 'LIVE') return { tone: 'ok', text: `${name}: Eşleşti` }
  return null
}

export function variantStatus(row: Row): VariantStatus {
  const fieldLines: VariantStatusLine[] = variantFieldIssues(row).map(text => ({ tone: 'error', text }))
  const platformLines = (row.platformStatuses ?? []).map(platformLine).filter((line): line is VariantStatusLine => line !== null)
  const lines = [...fieldLines, ...platformLines.filter(line => line.tone === 'error'), ...platformLines.filter(line => line.tone !== 'error')]
  const tone: VariantStatusTone = lines.some(line => line.tone === 'error') ? 'error' : lines.some(line => line.tone === 'pending') ? 'pending' : lines.some(line => line.tone === 'ok') ? 'ok' : 'unknown'
  if (tone === 'unknown' && !lines.length) lines.push({ tone: 'info', text: 'Henüz pazaryeri bilgisi yok.' })
  return { tone, lines }
}

export function variantStatusLabel(tone: VariantStatusTone) {
  return tone === 'error' ? 'Sorun var' : tone === 'pending' ? 'Pazaryeri yanıtı bekleniyor' : tone === 'ok' ? 'Sorun yok' : 'Durum bilinmiyor'
}
