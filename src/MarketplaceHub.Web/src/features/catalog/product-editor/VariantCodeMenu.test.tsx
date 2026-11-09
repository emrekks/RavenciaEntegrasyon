import { afterEach, describe, expect, it, vi } from 'vitest'
import { act } from 'react'
import { cleanup, click, flush, render } from '../../../test/render'
import { VariantCodeMenu, type VariantCodeMenuProps } from './VariantCodeMenu'

afterEach(() => cleanup())

function props(overrides: Partial<VariantCodeMenuProps> = {}): VariantCodeMenuProps {
  return {
    noun: 'Barkod', rowCount: 10, emptyCount: 0, filledCount: 10, canGenerate: true,
    generatePreview: { all: ['MZ028IH-01', 'MZ028IH-10'], missing: null },
    onGenerate: vi.fn(), onPaste: vi.fn(), onClear: vi.fn(), ...overrides,
  }
}
const tab = (host: HTMLElement, name: string) => Array.from(host.querySelectorAll<HTMLButtonElement>('[role=tab]')).find(button => button.textContent === name)!
const submit = (host: HTMLElement) => host.querySelector<HTMLButtonElement>('.variant-code-menu-submit')!
const radios = (host: HTMLElement) => Array.from(host.querySelectorAll<HTMLInputElement>('input[type=radio]'))

describe('variant Barkod / Stok Kodu menu', () => {
  it('defaults to all variants when none is empty and disables the empty scope', async () => {
    const p = props()
    const { host } = render(<VariantCodeMenu {...p} />)
    await flush()
    expect(radios(host)[1].disabled).toBe(true)
    expect(radios(host)[0].checked).toBe(true)
    expect(host.querySelector('.variant-code-menu-preview')?.textContent).toBe('MZ028IH-01 … MZ028IH-10')
    expect(submit(host).textContent).toBe('10 Barkodu Üret')
    click(submit(host))
    expect(p.onGenerate).toHaveBeenCalledWith('all')
  })

  it('defaults to the empty scope when there are empty rows and fires the same handlers per tab and scope', async () => {
    const p = props({ emptyCount: 2, generatePreview: { all: ['A-01', 'A-10'], missing: ['A-03', 'A-07'] } })
    const { host } = render(<VariantCodeMenu {...p} />)
    await flush()
    expect(radios(host)[1].checked).toBe(true)
    expect(submit(host).textContent).toBe('2 Boş Barkodu Üret')
    click(submit(host))
    expect(p.onGenerate).toHaveBeenLastCalledWith('missing')

    click(tab(host, 'Yapıştır'))
    expect(host.querySelector('.variant-code-menu-preview')?.textContent).toBe('Panodaki sıra ile 2 varyant')
    expect(submit(host).textContent).toBe('2 Boş Barkodu Doldur')
    click(submit(host))
    expect(p.onPaste).toHaveBeenLastCalledWith('missing')
    click(radios(host)[0])
    expect(submit(host).textContent).toBe('10 Barkodu Yapıştır')
    click(submit(host))
    expect(p.onPaste).toHaveBeenLastCalledWith('all')

    click(tab(host, 'Temizle'))
    expect(host.querySelector('[role=radiogroup]')).toBeNull()
    expect(host.querySelector('.variant-code-menu-preview')?.textContent).toBe('10 barkod boşalır')
    expect(host.querySelector('.variant-code-menu-preview.is-danger')).not.toBeNull()
    expect(submit(host).textContent).toBe('10 Barkodu Temizle')
    expect(submit(host).classList.contains('is-danger')).toBe(true)
    click(submit(host))
    expect(p.onClear).toHaveBeenCalledTimes(1)
  })

  it('blocks generating without a model code and clearing with nothing filled', async () => {
    const { host } = render(<VariantCodeMenu {...props({ canGenerate: false, filledCount: 0 })} />)
    await flush()
    expect(submit(host).disabled).toBe(true)
    click(tab(host, 'Temizle'))
    expect(submit(host).disabled).toBe(true)
  })

  it('uses Stok Kodu wording and offers the barcode copy tab', async () => {
    const onApply = vi.fn()
    const p = props({ noun: 'Stok Kodu', emptyCount: 3, fromBarcode: { missingCount: 4, allCount: 9, onApply } })
    const { host } = render(<VariantCodeMenu {...p} />)
    await flush()
    expect(submit(host).textContent).toBe('3 Boş Stok Kodunu Üret')
    click(tab(host, 'Temizle'))
    expect(host.querySelector('.variant-code-menu-preview')?.textContent).toBe('10 stok kodu boşalır')
    click(tab(host, 'Barkoddan'))
    expect(submit(host).textContent).toBe('4 Boş Stok Kodunu Doldur')
    click(submit(host))
    expect(onApply).toHaveBeenLastCalledWith('missing')
    click(radios(host)[0])
    expect(submit(host).textContent).toBe('9 Satıra Barkodu Uygula')
    click(submit(host))
    expect(onApply).toHaveBeenLastCalledWith('all')
  })

  it('moves between tabs with the arrow keys', async () => {
    const { host } = render(<VariantCodeMenu {...props()} />)
    await flush()
    expect(document.activeElement).toBe(tab(host, 'Üret'))
    act(() => { tab(host, 'Üret').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true })) })
    expect(tab(host, 'Yapıştır').getAttribute('aria-selected')).toBe('true')
    expect(document.activeElement).toBe(tab(host, 'Yapıştır'))
    act(() => { tab(host, 'Yapıştır').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true })) })
    expect(tab(host, 'Üret').getAttribute('aria-selected')).toBe('true')
  })
})
