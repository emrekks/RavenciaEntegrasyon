import { describe, expect, it } from 'vitest'
import { mediaImageKey, mediaStripOwnPosition, mediaStripRefs, reorderItems } from '../product-media-editor'
import { mapWithConcurrency, mediaSaveProgressMessage, newColoursWithoutPictures, savedMediaOrder, storedProductMediaUrl, variantMediaReconcilePlan } from './product-media-save'
import { orderMediaUrlsByVariants } from './publication-readiness'
import type { Variant } from '../shared'

describe('one picture strip', () => {
  const front = 'https://cdn.example.com/front.jpg'
  const side = 'https://cdn.example.com/side.jpg'
  const newFileUrl = storedProductMediaUrl('33333333-3333-4333-8333-333333333333')

  it('saves a new file moved before the saved pictures in that place, and it is the main picture', () => {
    const strip = mediaStripRefs([front, side], ['new'], [])
    expect(strip).toEqual([`url|${front}`, `url|${side}`, 'file|new'])
    // The new file is moved in front of the saved pictures (drag, touch or the arrows: one reorder of the strip).
    const shown = mediaStripRefs([front, side], ['new'], reorderItems(strip, 2, 0))
    expect(shown).toEqual(['file|new', `url|${front}`, `url|${side}`])
    const ownKeys = new Set([front, side].map(mediaImageKey))
    expect(mediaStripOwnPosition(shown, 0, ownKeys)).toBe(0)
    expect(mediaStripOwnPosition(shown, 1, ownKeys)).toBe(1)
    // The save writes the order shown, the new file by the stored copy its upload made.
    expect(savedMediaOrder(shown, new Map([['new', newFileUrl]]))).toEqual([newFileUrl, front, side])
  })

  it('keeps a new file between saved pictures when the pictures change around it', () => {
    const order = [`url|${front}`, 'file|new', `url|${side}`]
    expect(mediaStripRefs([side, front], ['new'], order)).toEqual([`url|${side}`, 'file|new', `url|${front}`])
    expect(mediaStripRefs([front], ['new', 'later'], order)).toEqual([`url|${front}`, 'file|new', 'file|later'])
    expect(mediaStripRefs([front, side, 'https://cdn.example.com/back.jpg'], [], order)).toEqual([`url|${front}`, `url|${side}`, 'url|https://cdn.example.com/back.jpg'])
  })

  it('does not count the other colours’ pictures for the main picture', () => {
    const grey = 'https://cdn.example.com/grey.jpg'
    const strip = [`url|${grey}`, 'file|new', `url|${front}`]
    expect(mediaStripOwnPosition(strip, 1, new Set([mediaImageKey(front)]))).toBe(0)
    expect(savedMediaOrder(strip, new Map())).toEqual([grey, front])
  })
})

describe('variantMediaReconcilePlan', () => {
  const stored = new Map([['a', storedProductMediaUrl('11111111-1111-1111-1111-111111111111')], ['b', storedProductMediaUrl('22222222-2222-2222-2222-222222222222')]])

  it('references the stored copy of each uploaded file at its own position, links as they are', () => {
    const plan = variantMediaReconcilePlan(['file|b', 'url|https://cdn.example.com/x.jpg', 'file|a'], stored, new Set(['a', 'b']))
    expect(plan.items).toEqual([
      { url: '/api/v1/files/product-media/22222222-2222-2222-2222-222222222222/content', sortOrder: 0 },
      { url: 'https://cdn.example.com/x.jpg', sortOrder: 1 },
      { url: '/api/v1/files/product-media/11111111-1111-1111-1111-111111111111/content', sortOrder: 2 }
    ])
    expect(plan.uploads).toEqual([])
  })

  it('leaves a file with no stored copy for its own upload and drops a file that is gone', () => {
    const plan = variantMediaReconcilePlan(['file|a', 'file|c', 'file|gone'], stored, new Set(['a', 'c']))
    expect(plan.items).toEqual([{ url: stored.get('a'), sortOrder: 0 }])
    expect(plan.uploads).toEqual([{ fileId: 'c', sortOrder: 1 }])
  })

  it('makes 18 Beden × Renk rows sharing 3 files per colour need no variant upload', () => {
    const files = new Map(Array.from({ length: 18 }, (_, index) => [`f${index}`, storedProductMediaUrl(`0000000${index}`)]))
    const rows = Array.from({ length: 18 }, (_, row) => [0, 1, 2].map(offset => `file|f${(Math.floor(row / 3) * 3) + offset}`))
    const uploads = rows.flatMap(refs => variantMediaReconcilePlan(refs, files, new Set(files.keys())).uploads)
    expect(uploads).toHaveLength(0)
  })
})

describe('mapWithConcurrency', () => {
  it('keeps at most the limit running and returns results in input order', async () => {
    let running = 0; let peak = 0
    const results = await mapWithConcurrency([5, 1, 4, 2, 3, 0], 3, async value => {
      running++; peak = Math.max(peak, running)
      await new Promise(resolve => setTimeout(resolve, value))
      running--
      return value * 10
    })
    expect(results).toEqual([50, 10, 40, 20, 30, 0])
    expect(peak).toBe(3)
  })

  it('throws the first failure and starts no new task after it', async () => {
    const started: number[] = []
    await expect(mapWithConcurrency([0, 1, 2, 3, 4], 2, async value => {
      started.push(value)
      if (value === 1) throw new Error('boom')
      await new Promise(resolve => setTimeout(resolve, 5))
      return value
    })).rejects.toThrow('boom')
    expect(started).toEqual([0, 1])
  })

  it('handles an empty list', async () => {
    expect(await mapWithConcurrency([], 4, async () => 1)).toEqual([])
  })
})

it('names the step and the count', () => {
  expect(mediaSaveProgressMessage('upload', 5, 18)).toBe('Görseller yükleniyor (5/18)…')
})

// Owner 7.11.2026, MZ052BOD (Beden × Renk, 7 colours on 7 cards): a size chart uploaded at the end of the strip, after
// every colour's pictures, came back 4th after the save (after the opened colour's 3 pictures), because only the
// card's own list was written. A new file after the other colours' pictures needs the family order written.
describe('a new file after the other colours\' pictures', () => {
  const own = ['https://cdn.example.com/navy-1.jpg', 'https://cdn.example.com/navy-2.jpg', 'https://cdn.example.com/navy-3.jpg']
  const otherColours = ['red', 'green', 'beige', 'black', 'white', 'grey'].flatMap(colour => [1, 2, 3].map(index => `https://cdn.example.com/${colour}-${index}.jpg`))
  const strip = mediaStripRefs([...own, ...otherColours], ['chart'], [])

  it('is the last tile before the save and the family order keeps it last', () => {
    expect(strip[strip.length - 1]).toBe('file|chart')
    expect(strip.indexOf('file|chart')).toBe(own.length + otherColours.length)
    // The family order the save writes keeps it last, after all 21 colour pictures.
    const chartUrl = storedProductMediaUrl('44444444-4444-4444-8444-444444444444')
    const saved = savedMediaOrder(strip, new Map([['chart', chartUrl]]))
    expect(saved).toHaveLength(22)
    expect(saved[saved.length - 1]).toBe(chartUrl)
  })

  it('keeps a file put among the card\'s own pictures in its place', () => {
    const amongOwn = mediaStripRefs([...own, ...otherColours], ['chart'], [`url|${own[0]}`, 'file|chart', ...[...own.slice(1), ...otherColours].map(url => `url|${url}`)])
    expect(amongOwn[1]).toBe('file|chart')
  })
})

// Owner 11.11.2026, MZ051BYC (Siyah card opened, 4 other colours on their own cards, Beden × Renk): six new pictures
// added at the end of the strip, after the other colours' pictures, came back right after Siyah's 3 pictures. The edit
// page gets the whole model's variants, and their pictures read as Siyah's own: the save then saw no file after another
// colour's picture and wrote no family order, and the next save copied every colour's pictures onto Siyah.
describe('the card\'s own pictures on a page that shows the whole model', () => {
  const own = [1, 2, 3].map(index => `https://cdn.example.com/siyah-${index}.jpg`)
  const others = ['lacivert', 'gri', 'bordo', 'haki']
  const otherPictures = others.flatMap(colour => [1, 2, 3].map(index => `https://cdn.example.com/${colour}-${index}.jpg`))
  const variant = (productId: string, mediaUrls: string[]) => ({ id: `${productId}-v`, productId, mediaUrls }) as unknown as Variant
  const variants = [variant('siyah', own), ...others.map((colour, index) => variant(colour, otherPictures.slice(index * 3, index * 3 + 3)))]

  it('takes only the opened card\'s variants, and the family order keeps the new files last', () => {
    const ownUrls = orderMediaUrlsByVariants(variants, own, own[0], false, 'siyah')
    expect(ownUrls).toEqual(own)
    const strip = mediaStripRefs([...ownUrls, ...otherPictures], ['purple', 'brown'], [])
    const purpleUrl = storedProductMediaUrl('55555555-5555-4555-8555-555555555555')
    const brownUrl = storedProductMediaUrl('66666666-6666-4666-8666-666666666666')
    // The family order the save writes keeps the new pictures last, after every colour's pictures.
    expect(savedMediaOrder(strip, new Map([['purple', purpleUrl], ['brown', brownUrl]]))).toEqual([...own, ...otherPictures, purpleUrl, brownUrl])
  })

  it('still takes the card\'s own variant pictures the product list lacks', () => {
    const extra = 'https://cdn.example.com/siyah-detail.jpg'
    expect(orderMediaUrlsByVariants([variant('siyah', [...own, extra]), ...variants.slice(1)], own, own[0], false, 'siyah')).toEqual([...own, extra])
  })
})

// Owner 11.11.2026, MZ051BYC: Kahverengi and Mürdüm added on the Siyah card became cards of their own with no picture
// (the brown and purple pictures were uploaded on Siyah and never assigned to those variants): the list said "Görsel yok".
describe('new colours without a picture', () => {
  const row = (key: string, colour: string, mediaRefs: string[] = []) => ({ key, options: { Renk: colour, Beden: 'L' }, mediaRefs })
  const isColour = (name: string) => name === 'Renk'

  it('names a colour the save adds with no picture on any of its rows', () => {
    const rows = [row('s1', 'Siyah'), row('n1', 'Kahverengi'), row('n2', 'Kahverengi'), row('n3', 'Mürdüm'), row('n4', 'Mor', ['url|https://cdn.example.com/mor.jpg'])]
    expect(newColoursWithoutPictures(rows, new Set(['s1']), isColour)).toEqual(['Kahverengi', 'Mürdüm'])
  })

  it('leaves a saved colour alone, new sizes included', () => {
    expect(newColoursWithoutPictures([row('s1', 'Siyah'), row('n1', 'Siyah')], new Set(['s1']), isColour)).toEqual([])
  })
})
