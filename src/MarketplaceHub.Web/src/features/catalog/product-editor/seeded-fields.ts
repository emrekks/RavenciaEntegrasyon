import { parseTrEntry, parseTrMeasure } from '../../../shared/tr-number'
import { editorCount } from './editor-number'

// "An untouched box is never edited and never unreadable" (O2, 23 Nov 2026). The server keeps prices and costs with four
// decimals and a stock may come back fractional; the editor shows such a value exactly as held ("99,999", "12,3456",
// "2,5"), which the typed-value rules refuse (at most two decimals, whole stock). So each box's loaded text is kept
// here, per field, when the editor fills the form; a box whose text still equals it reads as the value it was filled
// with (the comma read with any number of decimals), and only text the person changed goes through the typed rules.
// One product editor is open at a time: the editor's seed resets this before filling the form.
const seeded = new Map<string, string>()

export function resetSeededFields() { seeded.clear() }
export function seedField(key: string, text: string) { seeded.set(key, text) }
export function fieldUntouched(key: string, text: string) { return seeded.has(key) && seeded.get(key) === text }

const loaded = (text: string) => text.trim() === '' ? 0 : parseTrMeasure(text) ?? Number.NaN
// A price or cost box: the loaded value while untouched, else the typed-price rule (priceInputNumber).
export function readMoneyField(key: string, text: string): number { return fieldUntouched(key, text) ? loaded(text) : text.trim() === '' ? 0 : parseTrEntry(text) ?? Number.NaN }
// A stock box: the loaded value while untouched (a fractional server stock included), else a Turkish whole number.
export function readCountField(key: string, text: string | number): number { return typeof text === 'string' && fieldUntouched(key, text) ? loaded(text) : editorCount(text) }

export const channelFieldKey = (connectionId: string, field: 'listPrice' | 'salePrice') => `channel:${connectionId}:${field}`
// Every loaded marketplace card price (the editor's "Pazaryerlerine Özel Fiyat" cards as they open).
export function seedChannelFields(prices: Readonly<Record<string, { listPrice: string; salePrice: string }>>) {
  for (const [connectionId, draft] of Object.entries(prices)) { seedField(channelFieldKey(connectionId, 'listPrice'), draft.listPrice); seedField(channelFieldKey(connectionId, 'salePrice'), draft.salePrice) }
}
