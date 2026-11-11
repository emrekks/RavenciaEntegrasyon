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

describe('a size added to a colour starts with that colour’s pictures', () => {
  it('gives a fourth Bordo size the three pictures assigned to Bordo', () => {
    const before = [row('b-l', 'Bordo', 'L', bordo), row('b-xl', 'Bordo', 'XL', bordo), row('b-2xl', 'Bordo', '2XL', bordo)]
    const after = inheritAssignments(before, [...before, row('b-3xl', 'Bordo', '3XL')], colourOf)
    expect(after.map(item => item.mediaRefs)).toEqual([bordo, bordo, bordo, bordo])
    // Every Bordo size then goes with the same list (one Trendyol content, one picture list).
    expect(coloursWithMixedPictures(after.map(item => ({ colour: item.colour, refs: item.mediaRefs })))).toEqual([])
  })

  it('gives a new colour nothing from another colour or from a size (no size rule)', () => {
    const detail = url('xl-detay')
    const before = [row('b-xl', 'Bordo', 'XL', [...bordo, detail]), row('s-xl', 'Siyah', 'XL', [...siyah, detail]), row('s-l', 'Siyah', 'L', siyah)]
    const [, , , added] = inheritAssignments(before, [...before, row('k-xl', 'Kahverengi', 'XL')], colourOf)
    expect(added.mediaRefs).toEqual([])
  })

  it('takes only the pictures every assigned size of the colour holds, and leaves an unassigned colour unassigned', () => {
    const extra = url('ozel')
    const before = [row('b-l', 'Bordo', 'L', bordo), row('b-xl', 'Bordo', 'XL', [...bordo, extra]), row('b-2xl', 'Bordo', '2XL'), row('s-l', 'Siyah', 'L')]
    const after = inheritAssignments(before, [...before, row('b-3xl', 'Bordo', '3XL'), row('s-xl', 'Siyah', 'XL')], colourOf)
    expect(after.slice(4).map(item => item.mediaRefs)).toEqual([bordo, []])
  })

  it('keeps a row that already has pictures and every saved row as they are', () => {
    const before = [row('b-l', 'Bordo', 'L', bordo)]
    const own = row('b-xl', 'Bordo', 'XL', [url('ozel')])
    expect(inheritAssignments(before, [...before, own], colourOf)[1]).toBe(own)
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
