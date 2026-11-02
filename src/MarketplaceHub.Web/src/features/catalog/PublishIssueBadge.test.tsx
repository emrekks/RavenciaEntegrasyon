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
    // No row of buttons and no hint line: the popover ends with the chips, each opening its fix.
    expect(container.querySelector('.publish-issue-actions')).toBeNull()
    expect(container.querySelector('.publish-issue-hint')).toBeNull()
    act(() => sections[0].querySelector<HTMLButtonElement>('button.publish-issue-chip')!.click())
    expect(mapCategory).toHaveBeenCalledOnce()
    // The fix's window opens over the popover, which stays open behind it.
    expect(container.querySelector('.publish-issue-tooltip.is-open')).not.toBeNull()
  })

  it('stands as a yellow warning badge when nothing blocks', () => {
    act(() => root.render(<PublishIssueBadge id="w" label="2 uyarı" heading="" rows={[]} optionalHeading="2 isteğe bağlı özellik uyarısı" optionalRows={optional} optionalCount={2} />))
    const trigger = container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!
    expect(trigger.classList.contains('status-warning')).toBe(true)
    expect(trigger.title).toBe('')
    expect(trigger.querySelector('.publish-issue-label')?.textContent).toBe('2 uyarı')
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
    // No native title; the accessible name carries the gap.
    expect(required[0].hasAttribute('title')).toBe(false)
    expect(required[0].getAttribute('aria-label')).toBe('Kumaş Tipi. Zorunlu Hepsiburada özelliği için güncel ve doğrulanmış eşleme yok.')
    expect(container.querySelector('.publish-issue-tooltip')?.textContent).not.toContain('doğrulanmış eşleme yok')
    // Hover shows the styled tooltip, outside the popover.
    act(() => { optional[0].dispatchEvent(new MouseEvent('mouseover', { bubbles: true })) })
    const tip = document.querySelector('.publish-issue-chip-tip')
    expect([...tip!.querySelectorAll('span')].map(line => line.textContent)).toEqual(['“Orta” için değer eşlemesi yok. · “İnce” için değer eşlemesi yok.'])
    expect(container.querySelector('.publish-issue-tooltip')?.contains(tip)).toBe(false)
    expect(optional[0].getAttribute('aria-describedby')).toBe(tip!.id)
    act(() => { optional[0].dispatchEvent(new MouseEvent('mouseout', { bubbles: true })) })
    expect(document.querySelector('.publish-issue-chip-tip')).toBeNull()
    act(() => optional[0].click())
    expect(openKalinlik).toHaveBeenCalledOnce()
    expect(openKumas).not.toHaveBeenCalled()
  })
})

describe('PublishIssueBadge with the window a chip opens', () => {
  it('shows the field name on the chip and the missing values in its tooltip', () => {
    act(() => root.render(<PublishIssueBadge id="v" label="Yayın için zorunlu eksik" heading="Yayın için 2 eksik var" requiredCount={2}
      rows={[
        { key: 'wc-1', title: 'Web Color', detail: '“Mürdüm” için değer yok.', values: ['Mürdüm'], onRun: vi.fn() },
        { key: 'wc-2', title: 'Web Color', detail: '“Kiremit” için değer yok.', values: ['Kiremit'], onRun: vi.fn() },
        { key: 'kumas', title: 'Kumaş Tipi', detail: 'Eşleme yok.', tip: ['Kumaş Tipi eşlenmemiş', 'Eşlenebilecek panel özelliği: Materyal'], onRun: vi.fn() }
      ]} />))
    act(() => container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!.click())
    const chips = [...container.querySelectorAll<HTMLElement>('.publish-issue-chip')]
    expect(chips.map(chip => chip.textContent)).toEqual(['Web Color', 'Kumaş Tipi'])
    act(() => { chips[0].dispatchEvent(new FocusEvent('focusin', { bubbles: true })) })
    expect(document.querySelector('.publish-issue-chip-tip')?.textContent).toBe('Eşlenmemiş değerler: Mürdüm, Kiremit')
    act(() => { chips[0].dispatchEvent(new FocusEvent('focusout', { bubbles: true })) })
    act(() => { chips[1].dispatchEvent(new FocusEvent('focusin', { bubbles: true })) })
    expect([...document.querySelectorAll('.publish-issue-chip-tip span')].map(line => line.textContent)).toEqual(['Kumaş Tipi eşlenmemiş', 'Eşlenebilecek panel özelliği: Materyal'])
    expect(chips[0].getAttribute('aria-label')).toBe('Web Color. Eşlenmemiş değerler: Mürdüm, Kiremit')
  })

  it('stays open while the window is in front, ignores its presses and Escape, and gives focus back to the chip', async () => {
    vi.useFakeTimers()
    try {
      const dialog = document.createElement('section')
      dialog.setAttribute('aria-modal', 'true')
      const open = vi.fn(() => { document.body.append(dialog) })
      act(() => root.render(<PublishIssueBadge id="m" label="Yayın için zorunlu eksik" heading="Yayın için 2 eksik var" requiredCount={2}
        rows={[{ key: 'a', title: 'Kumaş Tipi', detail: 'Yok.', onRun: open }, { key: 'b', title: 'Kesim Kalıbı', detail: 'Yok.', onRun: vi.fn() }]} />))
      act(() => container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!.click())
      const chip = [...container.querySelectorAll<HTMLButtonElement>('.publish-issue-chip')][0]
      act(() => chip.click())
      expect(open).toHaveBeenCalledOnce()
      act(() => { vi.advanceTimersByTime(300) })
      // A press inside the window and its Escape are the window's, not the popover's.
      act(() => { dialog.dispatchEvent(new MouseEvent('mousedown', { bubbles: true })) })
      act(() => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
      expect(container.querySelector('.publish-issue-tooltip.is-open')).not.toBeNull()
      // The window closes: focus comes back to the chip, the popover is still open.
      dialog.remove()
      act(() => { vi.advanceTimersByTime(300) })
      expect(document.activeElement).toBe(chip)
      expect(container.querySelector('.publish-issue-tooltip.is-open')).not.toBeNull()
      // With no window in front an outside press closes it again.
      act(() => { document.body.dispatchEvent(new MouseEvent('mousedown', { bubbles: true })) })
      expect(container.querySelector('.publish-issue-tooltip.is-open')).toBeNull()
    } finally { vi.useRealTimers() }
  })

  it('says no gap is left when its list empties while open', () => {
    const render = (rows: Array<{ key: string; title: string; detail: string; onRun?: () => void }>) => act(() => root.render(<PublishIssueBadge id="e" label="Yayın için zorunlu eksik" heading="Yayın için 1 eksik var" rows={rows} requiredCount={rows.length} optionalRows={[{ key: 'x', title: 'Boy', detail: 'Uyarı.' }]} />))
    render([{ key: 'a', title: 'Kumaş Tipi', detail: 'Yok.', onRun: vi.fn() }])
    act(() => container.querySelector<HTMLButtonElement>('.publish-issue-trigger')!.click())
    act(() => root.render(<PublishIssueBadge id="e" label="Henüz yayınlanmadı" heading="" rows={[]} />))
    expect(container.querySelector('.publish-issue-tooltip.is-open .publish-issue-empty')?.textContent).toBe('Eksik kalmadı.')
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
