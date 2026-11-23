// A safeguard promised to the owner (23 Nov 2026, after the "1.250 -> 1,25" bug): before a save, a panel or marketplace
// price that falls below half of its previous value, or rises above three times it, is named in the save's confirmation
// ("3 Varyantın Fiyatı Yarıdan Fazla Düşüyor: 1.250,00 → 1,25"). It never stops the save; the person confirms it openly.
// A price without a previous value (the first one typed) is no jump. One rule for every place that saves prices: the
// products list's quick edit (Hızlı Fiyat), the product editor's own fields and what Toplu Düzenle handed to it.

// One price going out: key names the variant (several prices of one variant count it once).
export type PriceChange = { key: string; before: number | null | undefined; after: number }
export type PriceJumps = { drops: PriceChange[]; rises: PriceChange[]; lines: string[] }

const money = (value: number) => value.toLocaleString('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const isDrop = (change: PriceChange) => change.before != null && change.before > 0 && Number.isFinite(change.after) && change.after < change.before / 2
const isRise = (change: PriceChange) => change.before != null && change.before > 0 && Number.isFinite(change.after) && change.after > change.before * 3

function line(changes: PriceChange[], text: string): string | null {
  if (!changes.length) return null
  const variants = new Set(changes.map(change => change.key)).size
  const first = changes[0]
  return `${variants} Varyantın Fiyatı ${text}: ${money(first.before ?? 0)} → ${money(first.after)}`
}

export function priceJumps(changes: Iterable<PriceChange>): PriceJumps {
  const all = [...changes]
  const drops = all.filter(isDrop)
  const rises = all.filter(isRise)
  return { drops, rises, lines: [line(drops, 'Yarıdan Fazla Düşüyor'), line(rises, 'Üç Katından Fazla Artıyor')].filter((text): text is string => text != null) }
}
