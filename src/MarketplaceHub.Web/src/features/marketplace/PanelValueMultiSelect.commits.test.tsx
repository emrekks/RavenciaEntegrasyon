import { act, Profiler } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../../test/render'
import { PanelValueMultiSelect } from './PanelValueMultiSelect'

afterEach(() => { cleanup(); document.body.innerHTML = '' })

describe('PanelValueMultiSelect opening', () => {
  // Placed in a layout effect, the list was drawn twice before its first paint (without a place, then with it).
  it('PanelValueMultiSelect opens in one commit', () => {
    const commits = vi.fn()
    render(<Profiler id="multi" onRender={commits}><PanelValueMultiSelect label="Renk" options={[{ id: 'a', value: 'Kırmızı' }, { id: 'b', value: 'Mavi' }]} selectedIds={[]} assignedElsewhere={{}} onToggle={() => undefined} /></Profiler>)
    commits.mockClear()
    act(() => document.querySelector<HTMLButtonElement>('.panel-value-multi-trigger')!.click())
    expect(document.querySelector('.panel-value-multi-menu')).not.toBeNull()
    expect(commits).toHaveBeenCalledTimes(1)
  })

})
