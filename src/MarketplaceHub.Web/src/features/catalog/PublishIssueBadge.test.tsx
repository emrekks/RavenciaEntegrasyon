import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PublishIssueBadge } from './PublishIssueBadge'

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true

let container: HTMLDivElement
let root: Root

beforeEach(() => {
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
})

afterEach(() => {
  act(() => root.unmount())
  container.remove()
})

const optional = [{ key: 'materyal', title: 'Materyal', detail: 'Hepsiburada özelliği eşlenmemiş.', href: '/mappings/attributes' }, { key: 'boy', title: 'Boy', detail: '“Midi” için değer eşlemesi yok.' }]

describe('PublishIssueBadge', () => {
  it('lists the required gaps first and the optional warnings below them, with a counter each', () => {
    act(() => root.render(<PublishIssueBadge id="b" label="Yayın için zorunlu eksik" heading="Yayın için 1 eksik var" rows={[{ key: 'k', title: 'Kategori eşlemesi', detail: 'Kazak kategorisinin Hepsiburada karşılığı seçilmemiş.' }]} requiredCount={1} optionalHeading="2 isteğe bağlı özellik uyarısı" optionalRows={optional} optionalCount={2} actions={[{ key: 'category', label: 'Kategori Eşle', onRun: vi.fn() }]} />))
    const trigger = container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!
    expect(trigger.classList.contains('status-danger')).toBe(true)
    expect([...container.querySelectorAll('.publish-issue-count')].map(item => [item.textContent, item.classList.contains('is-optional')])).toEqual([['1', false], ['2', true]])
    // Optional rows are there, so the press opens the popover instead of the one fix.
    act(() => trigger.click())
    const sections = [...container.querySelectorAll('.publish-issue-tooltip.is-open > section')]
    expect(sections.map(section => section.className)).toEqual(['publish-issue-required', 'publish-issue-optional'])
    expect(sections[1].querySelector('a')?.getAttribute('href')).toBe('/mappings/attributes')
  })

  it('stands as a yellow warning badge when nothing blocks', () => {
    act(() => root.render(<PublishIssueBadge id="w" label="2 uyarı" heading="" rows={[]} optionalHeading="2 isteğe bağlı özellik uyarısı" optionalRows={optional} optionalCount={2} actions={[]} />))
    const trigger = container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!
    expect(trigger.classList.contains('status-warning')).toBe(true)
    expect(trigger.title).toBe('2 uyarı')
    act(() => trigger.click())
    expect(container.querySelector('.publish-issue-required')).toBeNull()
    expect(container.querySelectorAll('.publish-issue-optional li')).toHaveLength(2)
  })
})
