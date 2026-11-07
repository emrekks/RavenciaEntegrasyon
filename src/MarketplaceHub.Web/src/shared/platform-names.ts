/**
 * The one place that names the platforms on screen. Codes ('N11', 'PTTAVM') are stored identifiers and never change;
 * only the label shown to the user comes from here. "PTT Kargo" is a cargo company, not a platform, and is not listed.
 */
export const TRENDYOL_EFATURAM_CODE = 'TRENDYOL_EFATURAM'

/** Marketplaces in the order every platform list shows them. */
export const platformDisplayOrder = ['TRENDYOL', 'HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY'] as const
export type MarketplaceCode = typeof platformDisplayOrder[number]

export const platformDisplayNames: Record<MarketplaceCode | typeof TRENDYOL_EFATURAM_CODE, string> = {
  TRENDYOL: 'Trendyol',
  HEPSIBURADA: 'Hepsiburada',
  N11: 'N11',
  PAZARAMA: 'Pazarama',
  PTTAVM: 'PttAVM',
  SHOPIFY: 'Shopify',
  TRENDYOL_EFATURAM: 'Trendyol E-Faturam'
}

/** The display name of a platform code; null for a code the panel does not know. */
export function knownPlatformName(code: string | null | undefined): string | null {
  const normalized = code?.trim().toUpperCase() ?? ''
  return (platformDisplayNames as Record<string, string>)[normalized] ?? null
}

/** The display name of a platform code, or `fallback` (the code itself by default) when it is unknown. */
export function platformDisplayName(code: string | null | undefined, fallback?: string): string {
  return knownPlatformName(code) ?? fallback ?? code ?? ''
}

/** A comparer that sorts platform codes in the display order; the invoice provider and unknown codes go last. */
export function comparePlatformCodes(a: string, b: string): number {
  const rank = (code: string) => {
    const normalized = code.trim().toUpperCase()
    const index = (platformDisplayOrder as readonly string[]).indexOf(normalized)
    return index >= 0 ? index : platformDisplayOrder.length + (normalized === TRENDYOL_EFATURAM_CODE ? 0 : 1)
  }
  return rank(a) - rank(b) || a.localeCompare(b, 'tr-TR')
}

/** The title of a connection card: its own name, except that the invoice provider's generic "E-Faturam" reads in full. */
export function connectionTitle(platformCode: string | null | undefined, displayName: string): string {
  if (platformCode?.trim().toUpperCase() === TRENDYOL_EFATURAM_CODE && /^e-?faturam$/iu.test(displayName.trim())) return platformDisplayNames.TRENDYOL_EFATURAM
  return displayName
}
