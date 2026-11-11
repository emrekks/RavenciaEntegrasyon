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

// The pictures a variant row goes to the marketplaces with, the same rule as the server (PublicationVariantMedia): an
// assigned row (Seçeneklere Görsel Ata or its own picture icon) with exactly its assignment; an unassigned row with its
// card's gallery after the save minus the pictures assigned only to other colours; in the strip's order, the size chart
// last. The row icon shows this list's count (owner, 11.11.2026: "onlar yazsın varyantta görsel olarak").
export function variantOutgoingRefs(input: {
  rowRefs: readonly string[]
  rowCardId: string
  rowColour: string
  strip: readonly string[]
  owners: ReadonlyMap<string, ReadonlySet<string>> | null
  assignedColours: ReadonlyMap<string, ReadonlySet<string>>
  chartRef: string | null
}): { refs: string[]; assigned: boolean } {
  const chartKey = input.chartRef ? refKey(input.chartRef) : null
  const rank = new Map(input.strip.map((ref, index) => [refKey(ref), index]))
  const withChartLast = (refs: string[]) => {
    const others = refs.filter(ref => refKey(ref) !== chartKey)
    return chartKey && input.chartRef ? [...others, refs.find(ref => refKey(ref) === chartKey) ?? input.chartRef] : others
  }
  if (input.rowRefs.length) {
    const seen = new Set<string>()
    const own = input.rowRefs.filter(ref => { const key = refKey(ref); if (seen.has(key)) return false; seen.add(key); return true })
    const ordered = own.map((ref, index) => ({ ref, rank: rank.get(refKey(ref)) ?? Number.MAX_SAFE_INTEGER, index })).sort((left, right) => left.rank - right.rank || left.index - right.index).map(item => item.ref)
    return { refs: withChartLast(ordered), assigned: true }
  }
  const colour = input.rowColour.toLocaleLowerCase('tr-TR')
  const fromGallery = input.strip.filter(ref => {
    const key = refKey(ref)
    if (input.owners && !(input.owners.get(key)?.has(input.rowCardId) ?? false)) return false
    const assigned = input.assignedColours.get(key)
    return !assigned?.size || assigned.has(colour)
  })
  return { refs: fromGallery.length || chartKey ? withChartLast(fromGallery) : [], assigned: false }
}

// A colour or size assignment holds for the sizes and colours added later (owner, 11.11.2026: "bordoya atandı, 3 varyant
// eşlendi, 4. beden eklenirse o da eşlenmiş olsun renkle"; likewise a size). A new row without pictures takes the
// pictures every assigned row of its colour holds, and those every assigned row of its size holds across two or more
// colours; it is then saved as the variant's own list like any other. Nothing is matched by name or look.
type AssignmentRow = { key: string; mediaRefs: string[] }
const commonRefs = (rows: ReadonlyArray<AssignmentRow>) => {
  if (!rows.length) return []
  const others = rows.slice(1).map(row => new Set(row.mediaRefs.map(refKey)))
  return rows[0].mediaRefs.filter(ref => others.every(keys => keys.has(refKey(ref))))
}
const sameText = (left: string, right: string) => left.trim().toLocaleLowerCase('tr-TR') === right.trim().toLocaleLowerCase('tr-TR')

function colourAndSizeRefs<R extends AssignmentRow>(row: R, among: readonly R[], colourOf: (row: R) => string, sizeOf: (row: R) => string) {
  const assigned = among.filter(other => other.key !== row.key && other.mediaRefs.length > 0)
  const colour = colourOf(row)
  const size = sizeOf(row)
  const byColour = colour ? commonRefs(assigned.filter(other => sameText(colourOf(other), colour))) : []
  const sizeRows = size ? assigned.filter(other => sameText(sizeOf(other), size)) : []
  const sizeColours = new Set(sizeRows.map(other => colourOf(other).trim().toLocaleLowerCase('tr-TR')))
  const bySize = sizeColours.size >= 2 ? commonRefs(sizeRows) : []
  return { byColour, bySize }
}

export function inheritAssignments<R extends AssignmentRow>(before: readonly R[], after: readonly R[], colourOf: (row: R) => string, sizeOf: (row: R) => string): R[] {
  const beforeKeys = new Set(before.map(row => row.key))
  return after.map(row => {
    if (beforeKeys.has(row.key) || row.mediaRefs.length) return row
    const { byColour, bySize } = colourAndSizeRefs(row, before, colourOf, sizeOf)
    const seen = new Set<string>()
    const refs = [...byColour, ...bySize].filter(ref => { const key = refKey(ref); if (seen.has(key)) return false; seen.add(key); return true })
    return refs.length ? { ...row, mediaRefs: refs } : row
  })
}

// Where each of a row's pictures comes from, for its icon's tooltip: "Bordo atamasından 3, XL atamasından 1, bu varyanta özel 1".
export function assignmentSourceText<R extends AssignmentRow>(row: R, rows: readonly R[], colourOf: (row: R) => string, sizeOf: (row: R) => string): string {
  const { byColour, bySize } = colourAndSizeRefs(row, rows, colourOf, sizeOf)
  const colourKeys = new Set(byColour.map(refKey))
  const sizeKeys = new Set(bySize.map(refKey))
  let fromColour = 0; let fromSize = 0; let own = 0
  for (const ref of row.mediaRefs) {
    if (colourKeys.has(refKey(ref))) fromColour++
    else if (sizeKeys.has(refKey(ref))) fromSize++
    else own++
  }
  return [fromColour ? `${colourOf(row).trim()} atamasından ${fromColour}` : '', fromSize ? `${sizeOf(row).trim()} atamasından ${fromSize}` : '', own ? `bu varyanta özel ${own}` : ''].filter(Boolean).join(', ')
}

// The colours each picture is assigned to by the rows (lower case), for variantOutgoingRefs.
export function assignedColoursByPicture(rows: ReadonlyArray<{ colour: string; refs: readonly string[] }>): Map<string, Set<string>> {
  const result = new Map<string, Set<string>>()
  for (const row of rows) for (const ref of row.refs) {
    const key = refKey(ref)
    const colours = result.get(key) ?? new Set<string>()
    colours.add(row.colour.toLocaleLowerCase('tr-TR'))
    result.set(key, colours)
  }
  return result
}

// Trendyol keeps one content (contentId) per colour and one picture list per content: the colours whose sizes would go
// with different lists (an update of such a content is refused, PRODUCT_CONTENT_GROUP_MISMATCH).
export function coloursWithMixedPictures(rows: ReadonlyArray<{ colour: string; refs: readonly string[] }>): string[] {
  const lists = new Map<string, { label: string; lists: Set<string> }>()
  for (const row of rows) {
    if (!row.colour.trim()) continue
    const key = row.colour.trim().toLocaleLowerCase('tr-TR')
    const entry = lists.get(key) ?? { label: row.colour.trim(), lists: new Set<string>() }
    entry.lists.add(row.refs.map(refKey).join('\n'))
    lists.set(key, entry)
  }
  return [...lists.values()].filter(entry => entry.lists.size > 1).map(entry => entry.label)
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
