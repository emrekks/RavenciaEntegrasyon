import { describe, expect, it } from 'vitest'
import { assignedColoursByPicture, coloursWithMixedPictures, inheritAssignments, variantOutgoingRefs } from './family-gallery-save'

// Owner, 11.11.2026: "burdan atama yapıyorum onlar yazsın varyantta görsel olarak"; "neye atarsa onda duracak";
// "bordoya atandı, 3 varyant eşlendi, 4. beden eklenirse o da eşlenmiş olsun renkle".
const url = (name: string) => `url|https://cdn.example.com/${name}.jpg`
const bordo = [url('bordo-1'), url('bordo-2'), url('bordo-3')]
const siyah = [url('siyah-1'), url('siyah-2')]
const chart = url('beden-tablosu')
type Row = { key: string; mediaRefs: string[]; colour: string; size: string }
const row = (key: string, colour: string, size: string, mediaRefs: string[] = []): Row => ({ key, colour, size, mediaRefs })
const colourOf = (item: Row) => item.colour
const sizeOf = (item: Row) => item.size

// Owner, 12.11.2026: a row added later starts with the pictures its colour’s rows all hold and those its size’s rows
// (in other colours) all hold, pre-selected once; nothing is stored for a colour or a size.
describe('a new row starts with its colour’s and its size’s common pictures', () => {
  const strip = [...siyah, ...bordo, url('xl-detay'), chart]
  const add = (before: Row[], added: Row[]) => inheritAssignments(before, [...before, ...added], colourOf, sizeOf, strip).slice(before.length).map(item => item.mediaRefs)

  it('gives a fourth Bordo size the three pictures every Bordo size holds', () => {
    const before = [row('b-l', 'Bordo', 'L', bordo), row('b-xl', 'Bordo', 'XL', bordo), row('b-2xl', 'Bordo', '2XL', bordo)]
    const after = inheritAssignments(before, [...before, row('b-3xl', 'Bordo', '3XL')], colourOf, sizeOf, strip)
    expect(after.map(item => item.mediaRefs)).toEqual([bordo, bordo, bordo, bordo])
    // Every Bordo size then goes with the same list (one Trendyol content, one picture list).
    expect(coloursWithMixedPictures(after.map(item => ({ colour: item.colour, refs: item.mediaRefs })))).toEqual([])
  })

  it('gives an XL in a new colour the picture every XL holds, and nothing of another colour', () => {
    const detail = url('xl-detay')
    const before = [row('b-xl', 'Bordo', 'XL', [...bordo, detail]), row('s-xl', 'Siyah', 'XL', [...siyah, detail]), row('s-l', 'Siyah', 'L', siyah)]
    expect(add(before, [row('k-xl', 'Kahverengi', 'XL')])).toEqual([[detail]])
  })

  it('joins the colour’s and the size’s common pictures in the gallery order, once each', () => {
    const detail = url('xl-detay')
    const before = [row('b-l', 'Bordo', 'L', bordo), row('b-2xl', 'Bordo', '2XL', [...bordo, siyah[0]]), row('s-xl', 'Siyah', 'XL', [detail, ...siyah]), row('h-xl', 'Haki', 'XL', [detail, bordo[0]])]
    // Bordo XL: Bordo’s three (L and 2XL) and the detail every XL holds, in the strip’s order, bordo-1 once.
    expect(add(before, [row('b-xl', 'Bordo', 'XL')])).toEqual([[...bordo, detail]])
  })

  it('takes nothing from a size only one other colour holds (its pictures are that colour’s)', () => {
    const before = [row('b-xl', 'Bordo', 'XL', bordo)]
    expect(add(before, [row('s-xl', 'Siyah', 'XL')])).toEqual([[]])
  })

  it('takes nothing from a colour one of whose rows lacks the picture', () => {
    const before = [row('b-l', 'Bordo', 'L', bordo), row('b-xl', 'Bordo', 'XL')]
    expect(add(before, [row('b-2xl', 'Bordo', '2XL')])).toEqual([[]])
  })

  it('leaves a new colour in a new size empty', () => {
    const before = [row('b-l', 'Bordo', 'L', bordo), row('s-l', 'Siyah', 'L', siyah)]
    expect(add(before, [row('k-3xl', 'Kahverengi', '3XL')])).toEqual([[]])
  })

  it('keeps a row that already has pictures and every saved row as they are', () => {
    const before = [row('b-l', 'Bordo', 'L', bordo)]
    const own = row('b-xl', 'Bordo', 'XL', [url('ozel')])
    expect(inheritAssignments(before, [...before, own], colourOf, sizeOf, strip)[1]).toBe(own)
  })
})

describe('what a row goes to the marketplace with (its icon count)', () => {
  const strip = [...siyah, ...bordo, chart]
  const assigned = assignedColoursByPicture([{ colour: 'Bordo', refs: bordo }])

  it('is exactly the assignment for an assigned row, in the strip order, chart last', () => {
    const result = variantOutgoingRefs({ rowRefs: [bordo[2], bordo[0]], rowCardId: 'bordo', rowColour: 'Bordo', strip, owners: null, assignedColours: assigned, chartRef: chart })
    expect(result).toEqual({ refs: [bordo[0], bordo[2], chart], assigned: true })
  })

  it('is the gallery without the other colours’ assigned pictures for an unassigned row', () => {
    const result = variantOutgoingRefs({ rowRefs: [], rowCardId: 'siyah', rowColour: 'Siyah', strip, owners: null, assignedColours: assigned, chartRef: chart })
    expect(result).toEqual({ refs: [...siyah, chart], assigned: false })
  })

  it('names the colours whose sizes go with different lists (Trendyol keeps one list per colour)', () => {
    expect(coloursWithMixedPictures([{ colour: 'Siyah', refs: siyah }, { colour: 'Siyah', refs: [siyah[0]] }, { colour: 'Bordo', refs: bordo }, { colour: 'bordo', refs: bordo }])).toEqual(['Siyah'])
  })
})
