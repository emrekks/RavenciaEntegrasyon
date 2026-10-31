import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ProductImage } from './CommissionTariffsPanel'

describe('commission product photo', () => {
  let host: HTMLDivElement
  let root: Root
  beforeEach(() => { host = document.createElement('div'); document.body.append(host); root = createRoot(host) })
  afterEach(() => { act(() => root.unmount()); host.remove(); document.body.innerHTML = '' })

  it('opens the full-size photo as the products page does, without reaching the row', () => {
    const rowClick = vi.fn()
    const url = 'https://cdn.dsmcdn.com/ty1/prod/photo.jpg'
    document.body.addEventListener('click', rowClick)
    act(() => root.render(<ProductImage url={url} title="Penye Bluz · Lacivert" initial="P" />))
    expect(host.querySelector('img')?.getAttribute('src')).toContain('/mnresize/256/384/')

    const button = host.querySelector<HTMLButtonElement>('button[aria-label="Penye Bluz · Lacivert görselini büyüt"]')!
    act(() => button.click())

    expect(rowClick).not.toHaveBeenCalled()
    document.body.removeEventListener('click', rowClick)
    const preview = document.querySelector<HTMLImageElement>('.rr-img-preview img')
    expect(preview?.getAttribute('src')).toBe(url)
    expect(preview?.alt).toBe('Penye Bluz · Lacivert')
  })

  it('shows the first letter when there is no photo', () => {
    act(() => root.render(<ProductImage url={null} title="Penye Bluz" initial="P" />))
    expect(host.querySelector('button')).toBeNull()
    expect(host.textContent).toBe('P')
  })
})
