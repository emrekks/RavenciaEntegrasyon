import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, click, render } from '../../../test/render'
import { sizeChartKey } from './size-chart-order'
import { MediaOptionThumb, toggleVariantMediaRef, variantMediaHint, withoutChartOnly, type ProductMediaOption } from './variant-media-options'

afterEach(() => cleanup())

const picture = (name: string) => `https://cdn.dsmcdn.com/mz051byc/${name}.jpg`
const chart = picture('beden-tablosu')
const chartKey = sizeChartKey(chart)
const options: ProductMediaOption[] = ['siyah-1', 'siyah-2', 'siyah-3'].map((name, index) => ({ value: `url|${picture(name)}`, url: picture(name), label: `Görsel 0${index + 1}` }))
  .concat({ value: `url|${chart}`, url: chart, label: 'Görsel 04' })
const [one, two, three, chartRef] = options.map(option => option.value)

describe('the size chart tile of Varyant Görsellerini Seç (MZ051BYC Siyah)', () => {
  it('is ticked and locked with "Otomatik eklenir (sonda)", and a click does nothing', () => {
    const onClick = vi.fn()
    const { host } = render(<MediaOptionThumb option={options[3]} selected={false} automatic onClick={onClick} />)
    const tile = host.querySelector<HTMLButtonElement>('.variant-media-option')!
    expect(tile.classList.contains('selected')).toBe(true)
    expect(tile.classList.contains('is-automatic')).toBe(true)
    expect(tile.getAttribute('aria-pressed')).toBe('true')
    expect(tile.getAttribute('aria-disabled')).toBe('true')
    expect(tile.querySelector('.variant-media-option-note')?.textContent).toBe('Otomatik eklenir (sonda)')
    expect(tile.title).toContain('otomatik')
    expect(tile.querySelector('i .ui-icon')).not.toBeNull()
    click(tile)
    expect(onClick).not.toHaveBeenCalled()
  })

  it('leaves an ordinary tile a choice', () => {
    const onClick = vi.fn()
    const { host } = render(<MediaOptionThumb option={options[0]} selected={false} onClick={onClick} />)
    const tile = host.querySelector<HTMLButtonElement>('.variant-media-option')!
    expect(tile.getAttribute('aria-pressed')).toBe('false')
    expect(tile.hasAttribute('aria-disabled')).toBe(false)
    expect(tile.querySelector('.variant-media-option-note')).toBeNull()
    click(tile)
    expect(onClick).toHaveBeenCalledOnce()
  })

  it('never toggles the chart, keeps the strip order, and an assignment left with the chart alone is none', () => {
    expect(toggleVariantMediaRef(options, [one, three], chartRef, chartKey)).toEqual([one, three])
    expect(toggleVariantMediaRef(options, [three], one, chartKey)).toEqual([one, three])
    expect(toggleVariantMediaRef(options, [one, chartRef], one, chartKey)).toEqual([])
    expect(toggleVariantMediaRef(options, [one, chartRef], two, chartKey)).toEqual([one, two, chartRef])
    expect(withoutChartOnly([chartRef], chartKey)).toEqual([])
    expect(withoutChartOnly([one, chartRef], chartKey)).toEqual([one, chartRef])
  })

  it('says the row icon count as "N görsel + beden tablosu", the count with the chart', () => {
    expect(variantMediaHint({ refs: [one, two, three, chartRef], assigned: true }, chartKey))
      .toEqual({ count: 4, hint: 'Bu varyanta atanan 3 görsel + beden tablosu ile gider (beden tablosu sonda, otomatik)' })
    expect(variantMediaHint({ refs: [one, two, three, chartRef], assigned: false }, chartKey).hint)
      .toBe('Atama yok: kartın galerisinden 3 görsel + beden tablosu ile gider (diğer renklere atananlar hariç; beden tablosu sonda, otomatik)')
    expect(variantMediaHint({ refs: [one, two], assigned: false }, null))
      .toEqual({ count: 2, hint: 'Atama yok: kartın galerisinden 2 görsel ile gider (diğer renklere atananlar hariç)' })
    expect(variantMediaHint({ refs: [], assigned: false }, chartKey)).toEqual({ count: 0, hint: 'Atama yok, gidecek görsel yok' })
  })
})
