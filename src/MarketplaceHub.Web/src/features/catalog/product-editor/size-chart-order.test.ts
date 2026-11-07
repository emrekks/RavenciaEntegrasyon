import { describe, expect, it } from 'vitest'
import { reorderItems } from '../product-media-editor'
import { isSizeChartRef, pinSizeChartLast, replaceSizeChart, sizeChartKey } from './size-chart-order'

const chart = '/api/v1/files/product-media/c0000000-0000-0000-0000-000000000001/content'
const key = sizeChartKey(chart)

describe('size chart is always the last picture', () => {
  it('stays last after a new upload is added after it', () => {
    const strip = ['url|/a.jpg', `url|${chart}`, 'file|new-1']
    expect(pinSizeChartLast(strip, key)).toEqual(['url|/a.jpg', 'file|new-1', `url|${chart}`])
  })

  it('cannot be moved before other pictures by a reorder', () => {
    const strip = ['url|/a.jpg', 'url|/b.jpg', `url|${chart}`]
    expect(pinSizeChartLast(reorderItems(strip, 2, 0), key)).toEqual(strip)
    expect(pinSizeChartLast(reorderItems(strip, 0, 2), key)).toEqual(['url|/b.jpg', 'url|/a.jpg', `url|${chart}`])
  })

  it('matches the chart under another spelling and keeps a single copy', () => {
    expect(pinSizeChartLast([`${chart}/`, '/a.jpg', chart.toUpperCase()], key)).toEqual(['/a.jpg', `${chart}/`])
  })

  it('leaves a list without the chart, or already ending with it, unchanged', () => {
    const list = ['/a.jpg', '/b.jpg']
    expect(pinSizeChartLast(list, key)).toBe(list)
    expect(pinSizeChartLast(list, null)).toBe(list)
    const pinned = ['/a.jpg', chart]
    expect(pinSizeChartLast(pinned, key)).toBe(pinned)
  })

  it('replaces the previous chart with the new one at the end', () => {
    const next = '/api/v1/files/product-media/c0000000-0000-0000-0000-000000000002/content'
    expect(replaceSizeChart(['url|/a.jpg', `url|${chart}`, 'url|/b.jpg'], key, next, true)).toEqual(['url|/a.jpg', 'url|/b.jpg', `url|${next}`])
    expect(replaceSizeChart(['/a.jpg', chart], key, next, false)).toEqual(['/a.jpg', next])
    expect(replaceSizeChart(['/a.jpg'], key, next, false, false)).toEqual(['/a.jpg'])
    expect(replaceSizeChart(['/a.jpg', chart], key, next, false, false)).toEqual(['/a.jpg', next])
  })

  it('never treats a pending file as the chart', () => {
    expect(isSizeChartRef('file|x', key)).toBe(false)
    expect(isSizeChartRef(`url|${chart}`, key)).toBe(true)
  })
})
