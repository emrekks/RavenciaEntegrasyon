import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, click, render, type } from '../../../test/render'
import { groupSavedSelections, runInBatches, selectionsDiffer, valueDraftOperations } from './value-drafts'
import { ValuePicker, pickerOptions } from './ValuePicker'

afterEach(cleanup)

describe('value drafts', () => {
  const saved = [{ localId: 'red', externalId: 'r1', version: 3, snapshotId: 'snap' }, { localId: 'navy', externalId: 'r2', version: 1, snapshotId: 'old' }]

  it('plans removals of dropped pairs and the new or outdated pairs, skipping values the list no longer has', () => {
    const operations = valueDraftOperations({ valueScope: 'c/renk', fieldName: 'Renk', saved, snapshotId: 'snap', knownExternalIds: ['r1', 'r2', 'r3'],
      selections: { r2: ['navy'], r3: ['red'], gone: ['blue'] } })
    expect(operations).toEqual([
      { kind: 'DELETE', valueScope: 'c/renk', localId: 'red', externalId: 'r1', version: 3 },
      { kind: 'PUT', valueScope: 'c/renk', localId: 'navy', externalId: 'r2', snapshotId: 'snap', version: 1 },
      { kind: 'PUT', valueScope: 'c/renk', localId: 'red', externalId: 'r3', snapshotId: 'snap', version: null }
    ])
  })

  it('tells a changed choice from the saved one', () => {
    const grouped = groupSavedSelections(saved)
    expect(selectionsDiffer({ r1: ['red'], r2: ['navy'] }, grouped)).toBe(false)
    expect(selectionsDiffer({ r1: ['red'] }, grouped)).toBe(true)
  })

  it('attempts every request and reports the refused ones', async () => {
    const result = await runInBatches([1, 2, 3, 4, 5], 2, async item => { if (item === 4) throw new Error('refused') })
    expect(result.saved).toBe(4)
    expect(result.failures.map(failure => failure.item)).toEqual([4])
  })
})

describe('value picker', () => {
  it('searches the marketplace values with Turkish letters and punctuation folded, and clears a choice', () => {
    const onChange = vi.fn()
    const options = pickerOptions([{ externalId: 'a', name: 'Kırık Beyaz' }, { externalId: 'b', name: 'Lacivert - Kahverengi' }, { externalId: 'c', name: 'Siyah' }])
    render(<ValuePicker value="" options={options} label="Renk" onChange={onChange} />)
    const trigger = document.querySelector<HTMLButtonElement>('.vp-trigger')!
    expect(trigger.classList.contains('is-empty')).toBe(true)
    click(trigger)
    type(document.querySelector<HTMLInputElement>('.vp-search')!, 'kirik')
    expect(Array.from(document.querySelectorAll('.vp-list [role="option"]')).map(item => item.textContent)).toEqual(['Kırık Beyaz'])
    type(document.querySelector<HTMLInputElement>('.vp-search')!, 'lacivert kahve')
    click(document.querySelector<HTMLButtonElement>('.vp-list [role="option"]')!)
    expect(onChange).toHaveBeenCalledWith('b')
  })

  // 26 Oct 2026 (live): Escape in the list also closed the mapping window around it, and its unsaved choices.
  it('closes only the list on Escape, so the window around it stays open', () => {
    const windowEscape = vi.fn()
    document.addEventListener('keydown', windowEscape)
    render(<ValuePicker value="" options={pickerOptions([{ externalId: 'a', name: 'Siyah' }])} label="Renk" onChange={() => undefined} />)
    click(document.querySelector<HTMLButtonElement>('.vp-trigger')!)
    document.querySelector<HTMLInputElement>('.vp-search')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    document.removeEventListener('keydown', windowEscape)
    expect(windowEscape).not.toHaveBeenCalled()
  })

  it('draws only the first two hundred values of a long list', () => {
    const options = pickerOptions(Array.from({ length: 1500 }, (_, index) => ({ externalId: `v${index}`, name: `Renk ${index}` })))
    render(<ValuePicker value="v3" options={options} label="Renk" onChange={() => undefined} />)
    click(document.querySelector<HTMLButtonElement>('.vp-trigger')!)
    expect(document.querySelectorAll('.vp-list [role="option"]').length).toBe(200)
    expect(document.querySelector('.vp-clear')?.textContent).toBe('Seçimi Kaldır')
  })
})
