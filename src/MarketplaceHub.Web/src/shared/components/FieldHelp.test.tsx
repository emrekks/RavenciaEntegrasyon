import { act } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { FieldLabel } from './FieldHelp'

afterEach(cleanup)

function tip() { return document.getElementById('store-help') }

describe('FieldHelp', () => {
  it('keeps the explanation in the page for the field, hidden until asked', () => {
    const { host } = render(<label><FieldLabel text="Mağaza ID" helpId="store-help" help="Entegratör ekranındaki mağaza ID." /><input aria-describedby="store-help" /></label>)
    expect(host.querySelector('.field-label')?.textContent).toBe('Mağaza ID')
    expect(tip()?.getAttribute('role')).toBe('tooltip')
    expect(tip()?.textContent).toBe('Entegratör ekranındaki mağaza ID.')
    expect(tip()?.hidden).toBe(true)
    expect(host.querySelector('button.field-help')?.getAttribute('aria-label')).toBe('Mağaza ID: açıklama')
  })

  it('opens on a tap and closes on Esc or a tap elsewhere', () => {
    const { host } = render(<label><FieldLabel text="Mağaza ID" helpId="store-help" help="Açıklama" /><input /></label>)
    const mark = host.querySelector<HTMLButtonElement>('button.field-help')!
    act(() => { mark.click() })
    expect(tip()?.hidden).toBe(false)
    expect(mark.getAttribute('aria-expanded')).toBe('true')
    act(() => { document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    expect(tip()?.hidden).toBe(true)
    act(() => { mark.click() })
    expect(tip()?.hidden).toBe(false)
    act(() => { document.body.dispatchEvent(new Event('pointerdown', { bubbles: true })) })
    expect(tip()?.hidden).toBe(true)
  })

  it('draws no mark when there is nothing to explain', () => {
    const { host } = render(<label><FieldLabel text="Bağlantı Adı" /><input /></label>)
    expect(host.querySelector('button.field-help')).toBeNull()
  })
})
