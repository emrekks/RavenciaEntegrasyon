import { describe, expect, it } from 'vitest'
import { missingPublicationChecks, type PublicationProductCheck } from './publication-readiness'

// The live case (product 01a178e0…, 2 Nov 2026): the Web Color check failed and blocked Hepsiburada, n11, Pazarama and
// PTT AVM cards too, though Web Color exists only on Trendyol.
const checks: PublicationProductCheck[] = [
  { title: 'Kategori ve Seçenekler', detail: 'Hazır.', ok: true, shopifyOk: true },
  { title: 'Web Color', detail: 'Web Color otomatik aktarımı için Renk seçeneğini eşitleyip değer seçin.', ok: false, platforms: ['TRENDYOL'] }
]

describe('missingPublicationChecks', () => {
  it('reads the Web Color check on Trendyol cards only', () => {
    expect(missingPublicationChecks(checks, 'TRENDYOL').map(check => check.title)).toEqual(['Web Color'])
    for (const platform of ['HEPSIBURADA', 'N11', 'PAZARAMA', 'PTTAVM', 'SHOPIFY', ' hepsiburada ']) expect(missingPublicationChecks(checks, platform)).toEqual([])
  })
})
