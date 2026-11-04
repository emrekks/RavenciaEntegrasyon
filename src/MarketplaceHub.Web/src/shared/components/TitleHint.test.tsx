import { act } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { TitleHint } from './TitleHint'

afterEach(cleanup)

describe('TitleHint', () => {
  it('keeps the explanation out of the title and opens it on a tap', () => {
    const { host } = render(<h2>Görseller<TitleHint label="Görseller">JPEG/PNG dosyası yükleyebilirsiniz.</TitleHint></h2>)
    const mark = host.querySelector<HTMLButtonElement>('button.title-hint')!
    const tip = document.getElementById(mark.getAttribute('aria-describedby')!)!
    expect(host.querySelector('h2')?.textContent).toBe('Görseller')
    expect(mark.getAttribute('aria-label')).toBe('Görseller hakkında')
    expect(tip.getAttribute('role')).toBe('tooltip')
    expect(tip.textContent).toBe('JPEG/PNG dosyası yükleyebilirsiniz.')
    expect(tip.hidden).toBe(true)
    act(() => { mark.click() })
    expect(tip.hidden).toBe(false)
    act(() => { document.body.dispatchEvent(new Event('pointerdown', { bubbles: true })) })
    expect(tip.hidden).toBe(true)
  })

  it('opens on the pointer on desktop but only on a tap on a touch screen', () => {
    const hoverOver = (mark: Element) => {
      const event = new MouseEvent('pointerover', { bubbles: true })
      Object.defineProperty(event, 'pointerType', { value: 'mouse' })
      act(() => { mark.dispatchEvent(event) })
    }
    const desktop = render(<h2>Görseller<TitleHint label="Görseller">Açıklama</TitleHint></h2>)
    const desktopMark = desktop.host.querySelector<HTMLButtonElement>('button.title-hint')!
    hoverOver(desktopMark)
    expect(document.getElementById(desktopMark.getAttribute('aria-describedby')!)!.hidden).toBe(false)
    cleanup()

    const matchMedia = window.matchMedia
    window.matchMedia = (query: string) => ({ ...matchMedia(query), matches: query === '(hover: none)' })
    try {
      const { host } = render(<h2>Görseller<TitleHint label="Görseller">Açıklama</TitleHint></h2>)
      const mark = host.querySelector<HTMLButtonElement>('button.title-hint')!
      const tip = document.getElementById(mark.getAttribute('aria-describedby')!)!
      hoverOver(mark)
      expect(tip.hidden).toBe(true)
      act(() => { mark.click() })
      expect(tip.hidden).toBe(false)
      act(() => { mark.click() })
      expect(tip.hidden).toBe(true)
      act(() => { mark.click() })
      act(() => { window.dispatchEvent(new Event('scroll')) })
      expect(tip.hidden).toBe(true)
    } finally {
      window.matchMedia = matchMedia
    }
  })

  it('keeps the card inside a narrow window when the mark is near its right edge', () => {
    const width = window.innerWidth
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 360 })
    try {
      const { host } = render(<h2>Mağaza<TitleHint label="Mağaza">Açıklama</TitleHint></h2>)
      const mark = host.querySelector<HTMLButtonElement>('button.title-hint')!
      mark.getBoundingClientRect = () => ({ left: 330, right: 350, top: 100, bottom: 120, width: 20, height: 20, x: 330, y: 100, toJSON: () => ({}) })
      act(() => { mark.click() })
      const tip = document.getElementById(mark.getAttribute('aria-describedby')!)!
      expect(parseFloat(tip.style.left)).toBe(24)
      expect(parseFloat(tip.style.top)).toBe(128)
    } finally {
      Object.defineProperty(window, 'innerWidth', { configurable: true, value: width })
    }
  })
})
