// A model's pictures are one gallery in the editor, whichever colour row opened it (owner, 11.11.2026, MZ051BYC). Each
// colour is its own product card on the server and on the marketplaces (Trendyol: one content per colour under the
// model's productMainId, every size of a colour with that colour's pictures), so Kaydet writes the gallery colour by
// colour:
//   - a picture assigned to variants (Varyant Görselleri, Seçeneklere Görsel Ata) belongs to those variants' colours;
//   - the size chart belongs to every colour (a card with a chart of its own keeps it unless the chart was changed);
//   - a new picture (an upload or a link added now) with no assignment belongs to every colour;
//   - any other picture stays with the colours that hold it now: nothing saved is spread or moved without an assignment.
// A card's gallery keeps its pictures that still belong to it and takes the ones that newly do, in the strip's order. A
// card is written only when its set of pictures changes, so an untouched model is not rewritten. Nothing here depends
// on the card the editor was opened from: the same screen gives the same writes from every colour row.

import { mediaImageKey, uniqueMediaUrls } from '../product-media-editor'

export type FamilyCardPictures = { productId: string; mediaUrls: readonly string[]; variantMediaUrls: readonly string[]; sizeChartUrl: string | null }
// A variant row's assignment with its card: refs ('url|…', 'file|<pending id>') before the uploads, urls after.
export type ColourRowPictures = { productId: string; refs: readonly string[] }
export type FamilyChart = { url: string | null; initialUrl: string | null }

const refKey = (ref: string) => ref.startsWith('file|') ? ref : mediaImageKey(ref.startsWith('url|') ? ref.slice(4) : ref)

function chartCards(cards: readonly FamilyCardPictures[], chart: FamilyChart): Set<string> {
  if (!chart.url) return new Set()
  const key = mediaImageKey(chart.url)
  const changed = !chart.initialUrl || mediaImageKey(chart.initialUrl) !== key
  return new Set(cards.filter(card => changed || !card.sizeChartUrl || mediaImageKey(card.sizeChartUrl) === key).map(card => card.productId))
}

// The colours (cards) a picture belongs to; `isNew` says it was added in this session (no card held it).
function cardsOf(key: string, cards: readonly FamilyCardPictures[], rows: readonly ColourRowPictures[], chart: FamilyChart, chartSet: ReadonlySet<string>, isNew: (key: string) => boolean): Set<string> {
  if (chart.url && key === mediaImageKey(chart.url)) return new Set(chartSet)
  const assigned = new Set(rows.filter(row => row.refs.some(ref => refKey(ref) === key)).map(row => row.productId))
  if (assigned.size) return assigned
  if (isNew(key)) return new Set(cards.map(card => card.productId))
  return new Set(cards.filter(card => [...card.mediaUrls, ...card.variantMediaUrls].some(url => mediaImageKey(url) === key)).map(card => card.productId))
}

// Before the uploads: the card each new file is uploaded to (the first of its colours in the family's order), so the
// stored copy lands on a card that keeps it.
export function familyUploadTargets(fileIds: readonly string[], cards: readonly FamilyCardPictures[], rows: readonly ColourRowPictures[], fallbackProductId: string): Map<string, string> {
  const targets = new Map<string, string>()
  for (const fileId of fileIds) {
    const owners = cardsOf(`file|${fileId}`, cards, rows, { url: null, initialUrl: null }, new Set(), () => true)
    targets.set(fileId, cards.find(card => owners.has(card.productId))?.productId ?? fallbackProductId)
  }
  return targets
}

export type FamilyGalleryPlan = {
  // Each card whose set of gallery pictures changes, with its whole gallery in the strip's order.
  galleryWrites: Array<{ productId: string; urls: string[]; added: number; removed: number }>
  // The size chart mark each card gets (null clears it).
  chartMarks: Array<{ productId: string; url: string | null }>
}

// After the uploads: strip and rows hold urls (a new file by its stored copy); `uploaded` names the urls each card got
// from them, `newUrls` every picture added in this session; `removedKeys` the pictures the owner took away.
export function familyGalleryPlan(input: {
  strip: readonly string[]
  cards: readonly FamilyCardPictures[]
  rows: readonly ColourRowPictures[]
  uploaded: ReadonlyMap<string, readonly string[]>
  newUrls: readonly string[]
  removedKeys: ReadonlySet<string>
  chart: FamilyChart
}): FamilyGalleryPlan {
  const { strip, cards, rows, uploaded, removedKeys, chart } = input
  const newKeys = new Set(input.newUrls.map(mediaImageKey))
  const chartSet = chartCards(cards, chart)
  const stripRank = new Map<string, number>()
  strip.forEach(url => { const key = mediaImageKey(url); if (!stripRank.has(key)) stripRank.set(key, stripRank.size) })
  const ownersByKey = new Map<string, Set<string>>()
  const owners = (url: string) => {
    const key = mediaImageKey(url)
    let found = ownersByKey.get(key)
    if (!found) ownersByKey.set(key, found = cardsOf(key, cards, rows, chart, chartSet, candidate => newKeys.has(candidate)))
    return found
  }
  const inOrder = (urls: string[]) => urls
    .map((url, index) => ({ url, index, rank: stripRank.get(mediaImageKey(url)) ?? Number.MAX_SAFE_INTEGER }))
    .sort((left, right) => left.rank - right.rank || left.index - right.index)
    .map(item => item.url)
  const sameSet = (left: readonly string[], right: readonly string[]) => {
    const leftKeys = new Set(left.map(mediaImageKey))
    const rightKeys = new Set(right.map(mediaImageKey))
    return leftKeys.size === rightKeys.size && [...leftKeys].every(key => rightKeys.has(key))
  }

  const galleryWrites: FamilyGalleryPlan['galleryWrites'] = []
  for (const card of cards) {
    const current = uniqueMediaUrls([...card.mediaUrls, ...(uploaded.get(card.productId) ?? [])])
    const held = new Set([...current, ...card.variantMediaUrls].map(mediaImageKey))
    // A card without a gallery is published with its variants' pictures: those are its base when a picture joins it.
    const base = current.length ? current : uniqueMediaUrls(rows.filter(row => row.productId === card.productId).flatMap(row => [...row.refs]))
    const kept = base.filter(url => !removedKeys.has(mediaImageKey(url)) && owners(url).has(card.productId))
    const added = strip.filter(url => !removedKeys.has(mediaImageKey(url)) && !held.has(mediaImageKey(url)) && owners(url).has(card.productId))
    const desired = inOrder(uniqueMediaUrls([...kept, ...added]))
    const changed = current.length ? !sameSet(desired, current) : added.length > 0
    if (!changed) continue
    const desiredKeys = new Set(desired.map(mediaImageKey))
    galleryWrites.push({ productId: card.productId, urls: desired, added: added.length, removed: current.filter(url => !desiredKeys.has(mediaImageKey(url))).length })
  }
  // A card that only gains pictures goes first: none of its writes takes a stored picture away from the others.
  galleryWrites.sort((left, right) => Number(left.removed > 0) - Number(right.removed > 0))

  const chartMarks: FamilyGalleryPlan['chartMarks'] = []
  if (chart.url) {
    const key = mediaImageKey(chart.url)
    for (const card of cards) if (chartSet.has(card.productId) && (!card.sizeChartUrl || mediaImageKey(card.sizeChartUrl) !== key)) chartMarks.push({ productId: card.productId, url: chart.url })
  } else if (chart.initialUrl) {
    const key = mediaImageKey(chart.initialUrl)
    for (const card of cards) if (card.sizeChartUrl && mediaImageKey(card.sizeChartUrl) === key) chartMarks.push({ productId: card.productId, url: null })
  }
  return { galleryWrites, chartMarks }
}

// The editor's size chart: the first colour card's in the family's order that has one, whichever row was opened.
export function familySizeChartUrl(cards: readonly FamilyCardPictures[] | null | undefined, openedCardChart: string | null): string | null {
  return cards?.find(card => card.sizeChartUrl)?.sizeChartUrl ?? openedCardChart
}

// Which colour labels each picture belongs to, for the strip's tile captions ("Siyah", "Tüm Renkler").
export function pictureColourLabel(owners: ReadonlySet<string>, cardColours: ReadonlyMap<string, string>): string {
  if (!owners.size) return ''
  if (cardColours.size > 1 && [...cardColours.keys()].every(productId => owners.has(productId))) return 'Tüm Renkler'
  return [...cardColours.entries()].filter(([productId]) => owners.has(productId)).map(([, colour]) => colour).join(', ')
}

// The owners of every strip picture as the save will place them (the same rule), for the captions.
export function familyPictureOwners(input: { strip: readonly string[]; cards: readonly FamilyCardPictures[]; rows: readonly ColourRowPictures[]; newRefs: readonly string[]; chart: FamilyChart }): Map<string, Set<string>> {
  const newKeys = new Set(input.newRefs.map(refKey))
  const chartSet = chartCards(input.cards, input.chart)
  return new Map(input.strip.map(ref => [refKey(ref), cardsOf(refKey(ref), input.cards, input.rows, input.chart, chartSet, key => newKeys.has(key))]))
}
