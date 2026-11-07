import { act, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { OptionValueChips, sortOptionValues } from './OptionValueChips'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const toValues = (names: string[]) => names.map((value, index) => ({ id: `v${index}`, value }))
const names = (values: Array<{ value: string }>) => values.map(item => item.value)

describe('sortOptionValues', () => {
  it('sorts sizes in size order with numeric sizes ascending', () => {
    const sorted = sortOptionValues('Beden', toValues(['XL', '38', 'M', 'S', '2XL', 'XS', '40', 'L']))
    expect(names(sorted).filter(value => ['XS', 'S', 'M', 'L', 'XL', '2XL'].includes(value))).toEqual(['XS', 'S', 'M', 'L', 'XL', '2XL'])
    expect(names(sorted).filter(value => /^\d+$/.test(value))).toEqual(['38', '40'])
  })

  it('sorts colours A-Z in Turkish collation', () => {
    const sorted = sortOptionValues('Renk', toValues(['Şeftali', 'Siyah', 'Çivit', 'Zeytin', 'Ak', 'Kırmızı', 'Işık']))
    expect(names(sorted)).toEqual(['Ak', 'Çivit', 'Işık', 'Kırmızı', 'Siyah', 'Şeftali', 'Zeytin'])
  })
})

describe('OptionValueChips', () => {
  let container: HTMLElement
  let root: Root
  beforeEach(() => { container = document.createElement('main'); document.body.append(container); root = createRoot(container) })
  afterEach(() => { act(() => root.unmount()); container.remove() })

  const values = sortOptionValues('Renk', toValues(['Siyah', 'Ak', 'Kırmızı', 'Zeytin']))
  function Harness({ initial = [] as string[] }) {
    const [selected, setSelected] = useState(initial)
    return <OptionValueChips groupName="Renk" values={values} selectedIds={selected} filter={rest => rest} onToggle={id => setSelected(current => current.includes(id) ? current.filter(item => item !== id) : [...current, id])} />
  }
  const area = (label: string) => container.querySelector(`[aria-label="${label}"]`)
  const chipNames = (label: string) => [...(area(label)?.querySelectorAll('button') ?? [])].map(button => button.textContent?.replace('×', '').trim())
  const chip = (label: string, name: string) => [...(area(label)?.querySelectorAll('button') ?? [])].find(button => button.textContent?.startsWith(name))!
  const click = (button: Element) => act(() => { button.dispatchEvent(new MouseEvent('click', { bubbles: true })) })

  it('lists unselected values A-Z and shows no selected area at first', () => {
    act(() => root.render(<Harness />))
    expect(area('Seçili Renk değerleri')).toBeNull()
    expect(chipNames('Renk değerleri')).toEqual(['Ak', 'Kırmızı', 'Siyah', 'Zeytin'])
    expect(chip('Renk değerleri', 'Ak').getAttribute('aria-pressed')).toBe('false')
  })

  it('moves a chip to the top on select and back to its sorted place on unselect', () => {
    act(() => root.render(<Harness />))
    click(chip('Renk değerleri', 'Siyah'))
    click(chip('Renk değerleri', 'Ak'))
    expect(chipNames('Seçili Renk değerleri')).toEqual(['Ak', 'Siyah'])
    expect(chipNames('Renk değerleri')).toEqual(['Kırmızı', 'Zeytin'])
    expect(chip('Seçili Renk değerleri', 'Ak').getAttribute('aria-pressed')).toBe('true')
    click(chip('Seçili Renk değerleri', 'Ak'))
    expect(chipNames('Seçili Renk değerleri')).toEqual(['Siyah'])
    expect(chipNames('Renk değerleri')).toEqual(['Ak', 'Kırmızı', 'Zeytin'])
  })
})
