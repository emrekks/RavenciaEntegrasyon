import { runInBatches } from './value-drafts'

export type BrandImportFailure = { name: string; message: string }

// A brand mapping backup's mappings, written four at a time (WEB-SAYFALAR-7). A refused one no longer stops the rest:
// it is counted with its brand's name, so the result says what was saved, skipped and refused.
export async function importBrandMappingItems<T>(items: readonly T[], plan: {
  // The panel brand the mapping is written to, or null when it is skipped (no such brand, or the marketplace value is
  // not in the current reference list).
  target: (item: T) => string | null
  nameOf: (item: T) => string
  send: (item: T, targetLocalId: string) => Promise<unknown>
}) {
  const planned = items.flatMap(item => { const targetLocalId = plan.target(item); return targetLocalId ? [{ item, targetLocalId }] : [] })
  const result = await runInBatches(planned, 4, entry => plan.send(entry.item, entry.targetLocalId))
  return {
    mapped: result.saved,
    skipped: items.length - planned.length,
    failures: result.failures.map(failure => ({ name: plan.nameOf(failure.item.item), message: failure.error instanceof Error ? failure.error.message : 'Kaydedilemedi.' }))
  }
}

const listed = (failures: readonly BrandImportFailure[]) => failures.slice(0, 3).map(failure => `${failure.name} (${failure.message})`).join(', ') + (failures.length > 3 ? ` ve ${failures.length - 3} tane daha` : '')

export function brandMappingImportMessage(result: { created: number; brandFailures: readonly BrandImportFailure[]; mapped: number; skipped: number; failures: readonly BrandImportFailure[] }) {
  return [
    `${result.created} marka oluşturuldu, ${result.mapped} marka eşleştirmesi kaydedildi.`,
    result.brandFailures.length ? `${result.brandFailures.length} marka hazırlanamadı: ${listed(result.brandFailures)}.` : '',
    result.skipped ? `${result.skipped} eşleştirme güncel referansta bulunamadığı için atlandı.` : '',
    result.failures.length ? `${result.failures.length} eşleştirme kaydedilemedi: ${listed(result.failures)}.` : ''
  ].filter(Boolean).join(' ')
}
