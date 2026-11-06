// The size chart ("Beden Tablosu") is always the last picture: in the card's strip, in every variant's own pictures and
// in every list a save writes. These helpers take plain URLs or the editor's refs ('url|…', 'file|<pending id>') and
// compare pictures by mediaImageKey, so the same stored image under another spelling still counts as the chart.
import { mediaImageKey } from '../product-media-editor'

function refKey(value: string): string | null {
  if (value.startsWith('file|')) return null
  return mediaImageKey(value.startsWith('url|') ? value.slice(4) : value)
}

export function sizeChartKey(url: string | null | undefined): string | null {
  return url ? mediaImageKey(url) : null
}

export function isSizeChartRef(value: string, chartKey: string | null): boolean {
  return chartKey !== null && refKey(value) === chartKey
}

// The list with the chart moved to its end (once); the same array when there is nothing to move.
export function pinSizeChartLast<T extends string>(values: readonly T[], chartKey: string | null): T[] {
  if (!chartKey) return values as T[]
  const charts = values.filter(value => refKey(value) === chartKey)
  if (!charts.length) return values as T[]
  const others = values.filter(value => refKey(value) !== chartKey)
  if (charts.length === 1 && refKey(values[values.length - 1]) === chartKey) return values as T[]
  return [...others, charts[0]]
}

// "Kullan": the new chart replaces the previous one and goes last. With `ensure` false it is only moved, not added.
export function replaceSizeChart(values: readonly string[], previousKey: string | null, nextUrl: string, asRef: boolean, ensure = true): string[] {
  const nextKey = mediaImageKey(nextUrl)
  const hadChart = values.some(value => { const key = refKey(value); return key === nextKey || (previousKey !== null && key === previousKey) })
  const kept = values.filter(value => { const key = refKey(value); return key !== nextKey && (previousKey === null || key !== previousKey) })
  return ensure || hadChart ? [...kept, asRef ? `url|${nextUrl}` : nextUrl] : kept
}
