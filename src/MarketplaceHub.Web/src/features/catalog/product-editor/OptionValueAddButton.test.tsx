import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, click, flush, render } from '../../../test/render'
import { OptionValueAddButton } from './OptionValueAddButton'

afterEach(() => cleanup())

const colours = [{ id: 'siyah', value: 'Siyah' }, { id: 'murdum', value: 'Mürdüm' }]

describe('adding a value from an option group search box', () => {
  it('offers the typed colour the group lacks, in Title Case, and hands it over on click', async () => {
    const onAdd = vi.fn()
    const { host } = render(<OptionValueAddButton attributeName="Renk" values={colours} query="zümrüt" adding={false} onAdd={onAdd} />)
    await flush()
    const button = host.querySelector<HTMLButtonElement>('button.option-value-add-button')!
    expect(button.textContent).toContain('“Zümrüt” Değerini Ekle')
    expect(host.textContent).toContain('otomatik eşlenmez')
    click(button)
    expect(onAdd).toHaveBeenCalledWith('Zümrüt')
  })

  it('shows nothing for a value the group already holds, in any spelling', async () => {
    const { host } = render(<OptionValueAddButton attributeName="Renk" values={colours} query="MURDUM" adding={false} onAdd={vi.fn()} />)
    await flush()
    expect(host.querySelector('button')).toBeNull()
  })

  it('is disabled while the value is being added', async () => {
    const { host } = render(<OptionValueAddButton attributeName="Beden" values={[]} query="xxl" adding onAdd={vi.fn()} />)
    await flush()
    const button = host.querySelector<HTMLButtonElement>('button')!
    expect(button.disabled).toBe(true)
    expect(button.textContent).toContain('Ekleniyor')
  })
})
