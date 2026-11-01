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
    const mapCategory = vi.fn()
    act(() => root.render(<PublishIssueBadge id="b" label="Yayın için zorunlu eksik" heading="Yayın için 1 eksik var" rows={[{ key: 'k', title: 'Kategori eşlemesi', detail: 'Kazak kategorisinin Hepsiburada karşılığı seçilmemiş.', onRun: mapCategory }]} requiredCount={1} optionalHeading="2 isteğe bağlı özellik uyarısı" optionalRows={optional} optionalCount={2} />))
    const trigger = container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!
    expect(trigger.classList.contains('status-danger')).toBe(true)
    expect([...container.querySelectorAll('.publish-issue-count')].map(item => [item.textContent, item.classList.contains('is-optional')])).toEqual([['1', false], ['2', true]])
    // Optional rows are there, so the press opens the popover instead of the one fix.
    act(() => trigger.click())
    const sections = [...container.querySelectorAll('.publish-issue-tooltip.is-open > section')]
    expect(sections.map(section => section.className)).toEqual(['publish-issue-required', 'publish-issue-optional'])
    expect(sections[1].querySelector('a.publish-issue-chip')?.getAttribute('href')).toBe('/mappings/attributes')
    // No row of buttons: the gap's chip opens its fix, and a hint says so.
    expect(container.querySelector('.publish-issue-actions')).toBeNull()
    expect(container.querySelector('.publish-issue-hint')?.textContent).toBe('Düzeltmek için bir eksiğe tıklayın.')
    act(() => sections[0].querySelector<HTMLButtonElement>('button.publish-issue-chip')!.click())
    expect(mapCategory).toHaveBeenCalledOnce()
    expect(container.querySelector('.publish-issue-tooltip.is-open')).toBeNull()
  })

  it('stands as a yellow warning badge when nothing blocks', () => {
    act(() => root.render(<PublishIssueBadge id="w" label="2 uyarı" heading="" rows={[]} optionalHeading="2 isteğe bağlı özellik uyarısı" optionalRows={optional} optionalCount={2} />))
    const trigger = container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!
    expect(trigger.classList.contains('status-warning')).toBe(true)
    expect(trigger.title).toBe('2 uyarı')
    act(() => trigger.click())
    expect(container.querySelector('.publish-issue-required')).toBeNull()
    expect(container.querySelectorAll('.publish-issue-optional li')).toHaveLength(2)
  })
})

describe('PublishIssueBadge chips', () => {
  it('shows only the titles as chips, keeps the detail for hover and screen readers, and lists a field once', () => {
    const openKumas = vi.fn()
    const openKalinlik = vi.fn()
    act(() => root.render(<PublishIssueBadge id="c" label="Yayın için zorunlu eksik" heading="Yayın için 2 eksik var" requiredCount={2}
      rows={[
        { key: 'kumas', title: 'Kumaş Tipi', detail: 'Zorunlu Hepsiburada özelliği için güncel ve doğrulanmış eşleme yok.', onRun: openKumas },
        { key: 'kesim', title: 'Kesim Kalıbı', detail: 'Zorunlu Hepsiburada özelliği için güncel ve doğrulanmış eşleme yok.', onRun: vi.fn() }
      ]}
      optionalHeading="3 isteğe bağlı özellik uyarısı" optionalCount={3}
      optionalRows={[
        { key: 'kumas-o', title: 'Kumaş Tipi', detail: 'Kategorideki alan eşlenmemiş.', onRun: vi.fn() },
        { key: 'kal-1', title: 'Kalınlık', detail: '“Orta” için değer eşlemesi yok.', onRun: openKalinlik },
        { key: 'kal-2', title: 'Kalınlık', detail: '“İnce” için değer eşlemesi yok.', onRun: openKalinlik }
      ]} />))
    act(() => container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!.click())
    const required = [...container.querySelectorAll<HTMLElement>('.publish-issue-required .publish-issue-chip')]
    const optional = [...container.querySelectorAll<HTMLElement>('.publish-issue-optional .publish-issue-chip')]
    // The headings stay, the chips carry the titles only.
    expect(container.querySelector('.publish-issue-required > strong')?.textContent).toBe('Yayın için 2 eksik var')
    expect(container.querySelector('.publish-issue-optional > strong')?.textContent).toBe('3 isteğe bağlı özellik uyarısı')
    expect(required.map(chip => chip.textContent)).toEqual(['Kumaş Tipi', 'Kesim Kalıbı'])
    // Kumaş Tipi is required, so it is not repeated among the warnings; Kalınlık's two value gaps share one chip.
    expect(optional.map(chip => chip.textContent)).toEqual(['Kalınlık'])
    expect(required[0].title).toBe('Zorunlu Hepsiburada özelliği için güncel ve doğrulanmış eşleme yok.')
    expect(required[0].getAttribute('aria-label')).toBe('Kumaş Tipi: Zorunlu Hepsiburada özelliği için güncel ve doğrulanmış eşleme yok.')
    expect(optional[0].title).toBe('“Orta” için değer eşlemesi yok. · “İnce” için değer eşlemesi yok.')
    expect(container.querySelector('.publish-issue-tooltip')?.textContent).not.toContain('doğrulanmış eşleme yok')
    act(() => optional[0].click())
    expect(openKalinlik).toHaveBeenCalledOnce()
    expect(openKumas).not.toHaveBeenCalled()
  })
})

describe('PublishIssueBadge shortcut', () => {
  it('opens the one fix directly when there is one gap with a fix', () => {
    const fix = vi.fn()
    act(() => root.render(<PublishIssueBadge id='s' label='Yayın için zorunlu eksik' heading='Yayın için 1 eksik var' rows={[{ key: 'k', title: 'Kategori eşlemesi', detail: 'Eşlenmemiş.', onRun: fix }]} requiredCount={1} />))
    act(() => container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!.click())
    expect(fix).toHaveBeenCalledOnce()
    expect(container.querySelector('.publish-issue-tooltip.is-open')).toBeNull()
  })
})
