import { describe, expect, it } from 'vitest'
import { mediaPickerLabel, mediaPickerRefs } from './media-picker-order'
import { sizeChartKey } from './size-chart-order'

const picture = (name: string) => `https://cdn.dsmcdn.com/ryp007/${name}.jpg`
const chart = picture('beden-tablosu')

describe('the variant picture window order', () => {
  it('lists the strip as it stands, new uploads in place, extras after it and the size chart last', () => {
    const strip = [`url|${picture('a')}`, 'file|n1', `url|${picture('b')}`, 'file|n2', 'file|n3', `url|${chart}`]
    const refs = mediaPickerRefs(strip, [picture('b'), picture('extra'), chart], sizeChartKey(chart))
    expect(refs).toEqual([`url|${picture('a')}`, 'file|n1', `url|${picture('b')}`, 'file|n2', 'file|n3', `url|${picture('extra')}`, `url|${chart}`])
  })

  it('puts the chart last even when the strip has it elsewhere', () => {
    const strip = [`url|${chart}`, `url|${picture('a')}`, 'file|n1']
    expect(mediaPickerRefs(strip, [], sizeChartKey(chart))).toEqual([`url|${picture('a')}`, 'file|n1', `url|${chart}`])
  })

  it('numbers the pictures in that order', () => {
    expect(mediaPickerLabel(0, false)).toBe('Görsel 01')
    expect(mediaPickerLabel(13, true)).toBe('Görsel 14 · Yüklenen')
  })
})
