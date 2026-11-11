import { describe, expect, it } from 'vitest'
import { familyGalleryPlan, familyPictureOwners, familySizeChartUrl, familyUploadTargets, pictureColourLabel, type ColourRowPictures, type FamilyCardPictures } from './family-gallery-save'

// MZ051BYC (owner, 11.11.2026): the Siyah card's gallery holds the other colours' pictures and six new uploads; the new
// Kahverengi and Mürdüm cards have none.
const url = (name: string) => `https://cdn.example.com/${name}.jpg`
const names = (prefix: string) => [1, 2, 3].map(index => url(`${prefix}-${index}`))
const bordo = names('bordo'); const siyah = names('siyah'); const kahve = names('kahve'); const murdum = names('murdum')
const chart = url('beden-tablosu')

const liveCards = (): FamilyCardPictures[] => [
  { productId: 'bordo', mediaUrls: bordo, variantMediaUrls: bordo, sizeChartUrl: null },
  { productId: 'siyah', mediaUrls: [...bordo, ...siyah, ...kahve, ...murdum], variantMediaUrls: siyah, sizeChartUrl: null },
  { productId: 'kahverengi', mediaUrls: [], variantMediaUrls: [], sizeChartUrl: null },
  { productId: 'murdum', mediaUrls: [], variantMediaUrls: [], sizeChartUrl: null }
]
const strip = [...bordo, ...siyah, ...kahve, ...murdum]
const plan = (rows: ColourRowPictures[], extra: Partial<Parameters<typeof familyGalleryPlan>[0]> = {}) => familyGalleryPlan({
  strip, cards: liveCards(), rows, uploaded: new Map(), newUrls: [], removedKeys: new Set(), chart: { url: null, initialUrl: null }, ...extra
})
const importedRows: ColourRowPictures[] = [
  { productId: 'bordo', refs: bordo }, { productId: 'bordo', refs: [] },
  { productId: 'siyah', refs: siyah }, { productId: 'siyah', refs: [] },
  { productId: 'kahverengi', refs: [] }, { productId: 'murdum', refs: [] }
]

describe('familyGalleryPlan', () => {
  it('moves each assigned picture to its own colour card and takes the other colours off Siyah', () => {
    const rows: ColourRowPictures[] = [...importedRows.slice(0, 4),
      { productId: 'kahverengi', refs: kahve }, { productId: 'kahverengi', refs: kahve },
      { productId: 'murdum', refs: murdum }]
    const { galleryWrites } = plan(rows)
    // The cards that only gain go first, Siyah (which loses Bordo's three and the six uploads) last; Bordo is untouched.
    expect(galleryWrites).toEqual([
      { productId: 'kahverengi', urls: kahve, added: 3, removed: 0 },
      { productId: 'murdum', urls: murdum, added: 3, removed: 0 },
      { productId: 'siyah', urls: siyah, added: 0, removed: 9 }
    ])
  })

  it('leaves a picture without an assignment where it is: nothing saved is spread or moved by itself', () => {
    const { galleryWrites } = plan(importedRows)
    // Only the other colours' assigned pictures leave Siyah; its six unassigned uploads stay until the owner assigns them.
    expect(galleryWrites).toEqual([{ productId: 'siyah', urls: [...siyah, ...kahve, ...murdum], added: 0, removed: 3 }])
  })

  it('writes nothing for a model whose cards already hold their own pictures', () => {
    const cards: FamilyCardPictures[] = [
      { productId: 'bordo', mediaUrls: bordo, variantMediaUrls: bordo, sizeChartUrl: null },
      { productId: 'siyah', mediaUrls: siyah, variantMediaUrls: siyah, sizeChartUrl: null }
    ]
    expect(familyGalleryPlan({ strip: [...bordo, ...siyah], cards, rows: importedRows.slice(0, 4), uploaded: new Map(), newUrls: [], removedKeys: new Set(), chart: { url: null, initialUrl: null } }).galleryWrites).toEqual([])
  })

  it('gives a new picture without a colour to every colour card, a card without a gallery keeping its variants’ pictures first', () => {
    const fresh = url('kombin')
    const cards: FamilyCardPictures[] = [
      { productId: 'bordo', mediaUrls: bordo, variantMediaUrls: bordo, sizeChartUrl: null },
      { productId: 'siyah', mediaUrls: [], variantMediaUrls: siyah, sizeChartUrl: null }
    ]
    const { galleryWrites } = familyGalleryPlan({ strip: [...bordo, ...siyah, fresh], cards, rows: importedRows.slice(0, 4), uploaded: new Map([['bordo', [fresh]]]), newUrls: [fresh], removedKeys: new Set(), chart: { url: null, initialUrl: null } })
    // Bordo got the upload already (its set is unchanged); Siyah's gallery is created with its own pictures, then the new one.
    expect(galleryWrites).toEqual([{ productId: 'siyah', urls: [...siyah, fresh], added: 1, removed: 0 }])
  })

  it('puts the size chart on every colour card without a chart of its own and marks it there', () => {
    const cards: FamilyCardPictures[] = [
      { productId: 'bordo', mediaUrls: bordo, variantMediaUrls: bordo, sizeChartUrl: null },
      { productId: 'siyah', mediaUrls: [...siyah, chart], variantMediaUrls: siyah, sizeChartUrl: chart },
      { productId: 'gri', mediaUrls: [url('gri-1'), url('gri-tablo')], variantMediaUrls: [], sizeChartUrl: url('gri-tablo') }
    ]
    const result = familyGalleryPlan({ strip: [...bordo, ...siyah, url('gri-1'), chart, url('gri-tablo')], cards, rows: importedRows.slice(0, 4), uploaded: new Map(), newUrls: [], removedKeys: new Set(), chart: { url: chart, initialUrl: chart } })
    // Gri keeps its own chart (the chart was not changed in this session).
    expect(result.galleryWrites).toEqual([{ productId: 'bordo', urls: [...bordo, chart], added: 1, removed: 0 }])
    expect(result.chartMarks).toEqual([{ productId: 'bordo', url: chart }])
  })

  it('takes a picture the owner removed off every card', () => {
    const { galleryWrites } = plan(importedRows, { removedKeys: new Set([siyah[2], ...kahve, ...murdum].map(item => item.toLowerCase())) })
    expect(galleryWrites).toEqual([{ productId: 'siyah', urls: siyah.slice(0, 2), added: 0, removed: 10 }])
  })
})

// The owner's rule: no "which colour did you open" difference. The editor feeds the plan from the family's lists
// (oldest card first) and the rows, never from the opened card, so opening Siyah or Bordo gives the same requests.
describe('the same save from every colour row', () => {
  const rows: ColourRowPictures[] = [...importedRows.slice(0, 4), { productId: 'kahverengi', refs: ['file|a', 'file|b'] }, { productId: 'murdum', refs: ['file|c'] }]
  const run = (openedProductId: string) => {
    const targets = familyUploadTargets(['a', 'b', 'c', 'd'], liveCards(), rows, openedProductId)
    const stored = new Map([['a', '/api/v1/files/product-media/a/content'], ['b', '/api/v1/files/product-media/b/content'], ['c', '/api/v1/files/product-media/c/content'], ['d', '/api/v1/files/product-media/d/content']])
    const uploaded = new Map<string, string[]>()
    for (const [fileId, productId] of targets) uploaded.set(productId, [...(uploaded.get(productId) ?? []), stored.get(fileId)!])
    const resolve = (ref: string) => ref.startsWith('file|') ? stored.get(ref.slice(5))! : ref
    return {
      targets: [...targets],
      plan: familyGalleryPlan({ strip: [...strip, ...stored.values()], cards: liveCards(), rows: rows.map(row => ({ ...row, refs: row.refs.map(resolve) })), uploaded, newUrls: [...stored.values()], removedKeys: new Set(), chart: { url: null, initialUrl: null } })
    }
  }

  it('uploads each new file to its colour and writes the same cards whichever row was opened', () => {
    const fromSiyah = run('siyah')
    expect(run('bordo')).toEqual(fromSiyah)
    expect(run('kahverengi')).toEqual(fromSiyah)
    // a and b to Kahverengi, c to Mürdüm, d (no colour) to the first card; d then goes to every other card.
    expect(fromSiyah.targets).toEqual([['a', 'kahverengi'], ['b', 'kahverengi'], ['c', 'murdum'], ['d', 'bordo']])
    expect(fromSiyah.plan.galleryWrites.map(write => [write.productId, write.urls.length])).toEqual([['kahverengi', 3], ['murdum', 2], ['siyah', 10]])
  })
})

describe('captions and chart', () => {
  it('names a picture’s colours, all of them as Tüm Renkler', () => {
    const colours = new Map([['bordo', 'Bordo'], ['siyah', 'Siyah']])
    expect(pictureColourLabel(new Set(['siyah']), colours)).toBe('Siyah')
    expect(pictureColourLabel(new Set(['siyah', 'bordo']), colours)).toBe('Tüm Renkler')
    const owners = familyPictureOwners({ strip: [`url|${bordo[0]}`, 'file|x'], cards: liveCards(), rows: importedRows, newRefs: ['file|x'], chart: { url: null, initialUrl: null } })
    expect([...owners.get('file|x')!]).toEqual(['bordo', 'siyah', 'kahverengi', 'murdum'])
    expect([...owners.get(bordo[0].toLowerCase())!]).toEqual(['bordo'])
  })

  it('takes the first colour card’s chart, whichever card was opened', () => {
    expect(familySizeChartUrl([{ productId: 'a', mediaUrls: [], variantMediaUrls: [], sizeChartUrl: null }, { productId: 'b', mediaUrls: [], variantMediaUrls: [], sizeChartUrl: chart }], null)).toBe(chart)
    expect(familySizeChartUrl(null, chart)).toBe(chart)
  })
})
