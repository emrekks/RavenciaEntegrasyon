import { act, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { describe, expect, it, vi } from 'vitest'
import { selectionSaveKey, selectionSaveSettled, useSavedSelection } from './CommissionTariffsPanel'

// Leaving a tariff tab sends the waiting picks as the plan unmounts; coming straight back reads the picks again. That read
// must wait for the save, or it brings back the picks from before the last change.
describe('commission tariff selection save', () => {
  it('lets the next read of a tariff wait for the save its plan sent while leaving', async () => {
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    let finish = () => undefined as void
    const save = vi.fn(() => new Promise<void>(resolve => { finish = resolve }))
    let pick: (value: string) => void = () => undefined
    function Plan() {
      const [serialized, setSerialized] = useState('{"picks":[]}')
      pick = setSerialized
      useSavedSelection(save, serialized, selectionSaveKey('c1', 't1'))
      return null
    }
    const container = document.createElement('div')
    const root = createRoot(container)
    act(() => root.render(<Plan />))
    act(() => pick('{"picks":["row-1"]}'))
    // The tab changes before the 600 ms wait is over: the plan unmounts and sends what was waiting.
    act(() => root.unmount())
    await act(async () => { await Promise.resolve(); await Promise.resolve() })
    expect(save).toHaveBeenCalledWith('{"picks":["row-1"]}')

    let read = false
    const reading = selectionSaveSettled(selectionSaveKey('c1', 't1')).then(() => { read = true })
    await act(async () => { await Promise.resolve() })
    expect(read).toBe(false)
    await act(async () => { finish(); await reading })
    expect(read).toBe(true)
    // Another tariff never waited.
    await expect(selectionSaveSettled(selectionSaveKey('c1', 't2'))).resolves.toBeUndefined()
  })
})
