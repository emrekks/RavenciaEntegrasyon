import { act, useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../test/render'
import { PLATFORM_NONE, PlatformFilter, platformFilterSummary } from './PlatformFilter'
import { PanelSelect } from './PanelSelect'

const options = [{ value: 'N11', label: 'n11' }, { value: 'TRENDYOL', label: 'Trendyol' }, { value: 'PTTAVM', label: 'PTT AVM' }]
const rows = () => [...document.querySelectorAll<HTMLLabelElement>('.pf-pop .rr-opt')]
const headButton = () => document.querySelector<HTMLButtonElement>('.pf-pop .rr-ph button')!

function Harness({ allowNone = false, start = [] as string[], seen }: { allowNone?: boolean; start?: string[]; seen: string[][] }) {
  const [value, setValue] = useState<string[]>(start)
  return <PlatformFilter options={options} selected={value} allowNone={allowNone} counts={{ TRENDYOL: 4, N11: 0 }} onChange={next => { seen.push(next); setValue(next) }} />
}

describe('PlatformFilter', () => {
  afterEach(() => cleanup())

  it('lists the platforms in the panel order with their dictionary names, every one ticked for "every platform"', () => {
    render(<Harness seen={[]} />)
    expect(document.querySelector('.pf-trigger')!.textContent).toBe('Tüm Platformlar')
    act(() => document.querySelector<HTMLButtonElement>('.pf-trigger')!.click())
    expect(rows().map(row => row.querySelector('.rr-grow')!.textContent)).toEqual(['Trendyol', 'N11', 'PttAVM'])
    expect(rows().every(row => row.querySelector('input')!.checked)).toBe(true)
    expect(rows().map(row => row.querySelector('.rr-opt-count')?.textContent ?? '')).toEqual(['4', '0', '0'])
    expect(headButton().textContent).toBe('Tümünü Kaldır')
  })

  it('unticks one platform from "every platform", and ticking the last one back is "every platform" again', () => {
    const seen: string[][] = []
    render(<Harness seen={seen} />)
    act(() => document.querySelector<HTMLButtonElement>('.pf-trigger')!.click())
    act(() => rows()[1].querySelector('input')!.click())
    expect(seen.at(-1)).toEqual(['TRENDYOL', 'PTTAVM'])
    expect(document.querySelector('.pf-trigger')!.textContent).toBe('2 Platform Seçili')
    act(() => rows()[1].querySelector('input')!.click())
    expect(seen.at(-1)).toEqual([])
  })

  it('without allowNone, Tümünü Kaldır clears the ticks in the menu only, so one platform can be picked next', () => {
    const seen: string[][] = []
    render(<Harness seen={seen} />)
    act(() => document.querySelector<HTMLButtonElement>('.pf-trigger')!.click())
    act(() => headButton().click())
    expect(seen).toEqual([])
    expect(rows().some(row => row.querySelector('input')!.checked)).toBe(false)
    act(() => rows()[2].querySelector('input')!.click())
    expect(seen.at(-1)).toEqual(['PTTAVM'])
    expect(document.querySelector('.pf-trigger')!.textContent).toBe('PttAVM')
  })

  it('with allowNone, Tümünü Kaldır chooses no platform', () => {
    const seen: string[][] = []
    render(<Harness allowNone seen={seen} />)
    act(() => document.querySelector<HTMLButtonElement>('.pf-trigger')!.click())
    act(() => headButton().click())
    expect(seen.at(-1)).toEqual([PLATFORM_NONE])
    expect(platformFilterSummary(options, [PLATFORM_NONE])).toBe('Platform Seçilmedi')
    expect(headButton().textContent).toBe('Tümünü Seç')
  })
})

describe('PanelSelect', () => {
  afterEach(() => cleanup())

  it('opens a radio list under its trigger, moves with the arrows and type-ahead, and picks with a click', () => {
    const picked: string[] = []
    function Select() {
      const [value, setValue] = useState('NAME_ASC')
      return <form><PanelSelect name="sort" ariaLabel="Sırala" value={value} options={[{ value: 'NAME_ASC', label: 'A–Z' }, { value: 'NAME_DESC', label: 'Z–A' }, { value: 'NEW', label: 'En Yeni' }]} onChange={next => { picked.push(next); setValue(next) }} /></form>
    }
    render(<Select />)
    const trigger = document.querySelector<HTMLButtonElement>('.pm-trigger')!
    expect(trigger.getAttribute('aria-label')).toBe('Sırala: A–Z')
    expect(document.querySelector<HTMLInputElement>('input[type="hidden"][name="sort"]')!.value).toBe('NAME_ASC')
    act(() => trigger.click())
    const list = document.querySelector<HTMLElement>('.pm-pop[role="listbox"]')!
    expect(document.activeElement?.textContent).toBe('A–Z')
    expect(list.querySelector('[aria-selected="true"]')!.textContent).toBe('A–Z')
    act(() => { list.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })) })
    expect(document.activeElement?.textContent).toBe('Z–A')
    act(() => { list.dispatchEvent(new KeyboardEvent('keydown', { key: 'e', bubbles: true })) })
    expect(document.activeElement?.textContent).toBe('En Yeni')
    act(() => (document.activeElement as HTMLButtonElement).click())
    expect(picked).toEqual(['NEW'])
    expect(document.querySelector('.pm-pop')).toBeNull()
    expect(document.querySelector<HTMLInputElement>('input[type="hidden"][name="sort"]')!.value).toBe('NEW')
  })
})
