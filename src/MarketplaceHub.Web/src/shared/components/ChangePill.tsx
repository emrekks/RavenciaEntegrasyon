import './change-pill.css'

// The one change indicator of the panel, as on the Raporlar cards: a tinted pill with a glyph (▲ up, ▼ down, ■ flat), the
// sign and the percentage ("▲ +%49", "▼ −%12", "■ %0"). Good is green, bad red, flat neutral; invert turns the colours
// round for figures where up is bad (returns, cancellations).
export type PillChange = { text: string; direction: 'up' | 'down' | 'flat' }

export const changeGlyph = { up: '▲', down: '▼', flat: '■' } as const

/** A dashboard trend ("%10,7", "-%33,3" plus whether it went up) as the pill's change: signed like the reports, "%0" flat. */
export function trendChange(trend: readonly [string, boolean]): PillChange {
  const [text, up] = trend
  const magnitude = text.replace(/^[-−+]/u, '')
  if (/^%0(,0)?$/u.test(magnitude)) return { text: '%0', direction: 'flat' }
  return { text: `${up ? '+' : '−'}${magnitude}`, direction: up ? 'up' : 'down' }
}

export function ChangePill({ change, invert = false, title = 'Önceki döneme göre değişim', className }: { change: PillChange | null | undefined; invert?: boolean; title?: string; className?: string }) {
  // Nothing to compare reads a flat "%0".
  const shown: PillChange = change ?? { text: '%0', direction: 'flat' }
  const tone = shown.direction === 'flat' ? 'flat' : (shown.direction === 'up') !== invert ? 'good' : 'bad'
  return <em className={`rv-change is-${tone}${className ? ` ${className}` : ''}`} title={change ? title : 'Önceki dönem: —'}>{changeGlyph[shown.direction]} {shown.text}</em>
}
