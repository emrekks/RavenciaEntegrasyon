import './change-pill.css'

// The one change indicator of the panel, as on the Raporlar cards: a tinted pill with a glyph (▲ up, ▼ down, ■ flat), the
// sign and the percentage ("▲ +%49", "▼ −%12", "■ %0"). Good is green, bad red, flat neutral; invert turns the colours
// round for figures where up is bad (returns, cancellations). Something against nothing is "▲ Yeni" (newChange), nothing
// against something "▼ −%100"; only nothing against nothing is "■ %0".
export type PillChange = { text: string; direction: 'up' | 'down' | 'flat' }

export const changeGlyph = { up: '▲', down: '▼', flat: '■' } as const

/** A figure that rose from zero: no percentage can say it, so the pill reads "▲ Yeni". */
export const newChangeText = 'Yeni'
export const newChange: PillChange = { text: newChangeText, direction: 'up' }

/** The pill's tooltip when the previous period lies (partly) before the panel's first synced order: no comparison. */
export const noPreviousDataTitle = 'Önceki dönem için veri yok'

/** A dashboard trend ("%10,7", "-%33,3" plus whether it went up) as the pill's change: signed like the reports, "%0" flat. */
export function trendChange(trend: readonly [string, boolean]): PillChange {
  const [text, up] = trend
  if (text === newChangeText) return newChange
  const magnitude = text.replace(/^[-−+]/u, '')
  if (/^%0(,0)?$/u.test(magnitude)) return { text: '%0', direction: 'flat' }
  return { text: `${up ? '+' : '−'}${magnitude}`, direction: up ? 'up' : 'down' }
}

// noData: the previous period has no data source (it starts before the first synced order): a neutral "—", no comparison.
export function ChangePill({ change, invert = false, title = 'Önceki döneme göre değişim', className, noData = false }: { change: PillChange | null | undefined; invert?: boolean; title?: string; className?: string; noData?: boolean }) {
  if (noData) return <em className={`rv-change is-flat${className ? ` ${className}` : ''}`} title={noPreviousDataTitle} aria-label={noPreviousDataTitle}>—</em>
  // Nothing to compare reads a flat "%0".
  const shown: PillChange = change ?? { text: '%0', direction: 'flat' }
  const tone = shown.direction === 'flat' ? 'flat' : (shown.direction === 'up') !== invert ? 'good' : 'bad'
  return <em className={`rv-change is-${tone}${className ? ` ${className}` : ''}`} title={change ? title : 'Önceki dönem: —'}>{changeGlyph[shown.direction]} {shown.text}</em>
}

/**
 * Whether a previous period starting on previousFrom (yyyy-MM-dd) has data to compare with: the chosen stores' first
 * order day (dataStartsOn) is on or before it. Null: no order at all, nothing to compare; undefined: an older server
 * that does not say, compared as before.
 */
export function previousHasData(previousFrom: string, dataStartsOn: string | null | undefined) {
  if (dataStartsOn === undefined) return true
  return dataStartsOn !== null && previousFrom >= dataStartsOn
}
