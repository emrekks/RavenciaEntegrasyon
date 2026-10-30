// Toplu KDV Ata: KDV Oranı and KDV Dahil mi for every selected product card. Only the cards change; an existing
// marketplace listing keeps the VAT it has. The selection goes out in chunks of 500 (see runInChunks).
import type { ChunkRunResult } from './bulk-status-run'
import { TURKISH_VAT_RATES } from './product-editor-state'

export { TURKISH_VAT_RATES as bulkVatRateOptions } from './product-editor-state'
export type BulkVatInclusion = 'INCLUDED' | 'EXCLUDED'
export type BulkVatResponse = { updatedCount: number; unchangedCount: number }

// The rate the dialog sends: one of the offered rates, never a guess. Empty ("Seçin") or anything else is null.
export function bulkVatRate(value: string): number | null {
  return TURKISH_VAT_RATES.includes(value.trim()) ? Number(value.trim()) : null
}

export function bulkVatResultMessage(result: ChunkRunResult, counts: BulkVatResponse): { title: string; message: string; kind: 'success' | 'error' } {
  const reason = result.error instanceof Error ? result.error.message : 'Toplu KDV ataması başarısız.'
  const done = `${counts.updatedCount.toLocaleString('tr-TR')} kayıt güncellendi${counts.unchangedCount ? `, ${counts.unchangedCount.toLocaleString('tr-TR')} kayıt zaten bu KDV’deydi` : ''}`
  if (!result.remainingIds.length) return { title: 'KDV Atandı', message: `${done}. Pazar yerlerindeki ilanların KDV’si değişmedi.`, kind: 'success' }
  if (!result.doneIds.length) return { title: 'KDV Atanamadı', message: reason, kind: 'error' }
  return { title: 'KDV Kısmen Atandı', message: `${done}; kalan ${result.remainingIds.length.toLocaleString('tr-TR')} kayıt güncellenemedi: ${reason}`, kind: 'error' }
}
