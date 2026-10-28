export function productPublicationTargets(isPanelOnlySave: boolean, connectionIds: readonly string[]) {
  return isPanelOnlySave ? [] : [...connectionIds]
}

// What Yayınla did on one marketplace for the colour cards of the family: how many jobs it queued, how many cards it
// left as they were (live, matched or still being published) and each refusal with the cards (0 = the opened one) it
// stopped. Live 2026-10-28: Shopify refused all 8 cards of MZ054YNC and the only trace was one line in a passing toast
// that did not name Shopify, so every line now names its marketplace and a marketplace that got no job says so.
export type FamilyPublicationOutcome = { enqueued: number; skipped: number; failures: ReadonlyMap<string, readonly number[]> }

export function familyPublicationWarnings(platformName: string, outcome: FamilyPublicationOutcome) {
  const warnings: string[] = []
  for (const [failure, indexes] of outcome.failures) {
    const cards = indexes.length > 1 ? ` (${indexes.length} renk kartında)` : indexes[0] ? ` (${indexes[0] + 1}. renk kartı)` : ''
    warnings.push(`${platformName}: ${failure}${cards}`)
  }
  if (outcome.enqueued === 0 && outcome.failures.size === 0 && outcome.skipped > 0) {
    warnings.push(`${platformName}: yeni yayın işi açılmadı; ${outcome.skipped > 1 ? `${outcome.skipped} renk kartı` : 'ürün'} zaten yayında, eşleşmiş veya yayın işi sürüyor.`)
  }
  if (outcome.enqueued === 0 && outcome.failures.size > 0) warnings.push(`${platformName} için yayın işi açılamadı.`)
  return warnings
}
