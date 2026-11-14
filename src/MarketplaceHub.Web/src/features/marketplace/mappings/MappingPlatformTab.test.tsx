import { afterEach, describe, expect, it, vi } from 'vitest'
import { byText, cleanup, click, render } from '../../../test/render'
import { MappingPlatformTab } from './MappingPlatformTab'

afterEach(cleanup)

describe('MappingPlatformTab', () => {
  it('renders a platform without an active connection greyed out, unselected and not clickable', () => {
    const onSelect = vi.fn()
    const { host } = render(<div role="tablist"><MappingPlatformTab code="N11" label="N11" available={false} selected activeTitle="N11 göster" onSelect={onSelect} /></div>)
    const tab = byText(host, '[role="tab"]', 'N11')
    expect(tab.getAttribute('aria-disabled')).toBe('true')
    expect(tab.getAttribute('aria-selected')).toBe('false')
    expect(tab.getAttribute('title')).toBe('Bağlantı etkin değil')
    expect(tab.className).toContain('is-unavailable')
    click(tab)
    expect(onSelect).not.toHaveBeenCalled()
  })

  it('keeps an active platform selectable', () => {
    const onSelect = vi.fn()
    const { host } = render(<div role="tablist"><MappingPlatformTab code="TRENDYOL" label="Trendyol" available selected={false} activeTitle="Trendyol göster" onSelect={onSelect} /></div>)
    const tab = byText(host, '[role="tab"]', 'Trendyol')
    expect(tab.hasAttribute('aria-disabled')).toBe(false)
    click(tab)
    expect(onSelect).toHaveBeenCalledOnce()
  })
})
