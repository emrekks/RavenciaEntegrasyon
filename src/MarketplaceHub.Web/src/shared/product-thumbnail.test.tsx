import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { previewSrcSet, thumbnailSources, thumbnailSrcSet, thumbnailUrl } from './product-thumbnail'
import { RrImagePreview, RrThumb } from '../features/marketplace/returns-ui'

const own = '/api/v1/files/product-media/019a1b2c-3d4e-7f00-8a9b-0c1d2e3f4a5b/content'

describe('our own product photos', () => {
  it('ask the server for the small WebP copy, absolute or relative', () => {
    expect(thumbnailUrl(own)).toBe(`${own}?w=168`)
    expect(thumbnailUrl(own, 480)).toBe(`${own}?w=480`)
    expect(thumbnailUrl(`https://panel.ravencia.com${own}`)).toBe(`https://panel.ravencia.com${own}?w=168`)
  })

  it('leave the marketplaces\' public address, an address with a query and unknown shapes alone', () => {
    const publicUrl = own.replace('/files/', '/public/')
    expect(thumbnailUrl(publicUrl)).toBe(publicUrl)
    expect(thumbnailUrl(`${own}?w=480`)).toBe(`${own}?w=480`)
    expect(thumbnailUrl('/api/v1/files/product-media/1/content')).toBe('/api/v1/files/product-media/1/content')
    expect(thumbnailUrl('blob:https://panel.ravencia.com/abc')).toBe('blob:https://panel.ravencia.com/abc')
  })

  it('offer both copies to the browser, and the original for a sharp preview', () => {
    expect(thumbnailSrcSet(`${own}?w=168`)).toBe(`${own}?w=168 168w, ${own}?w=480 480w`)
    expect(thumbnailSrcSet('https://cdn.dsmcdn.com/ty1/a.jpg')).toBeUndefined()
    expect(previewSrcSet(own)).toBe(`${own}?w=480 480w, ${own} 1200w`)
    expect(previewSrcSet('https://cdn.dsmcdn.com/ty1/a.jpg')).toBeUndefined()
  })

  it('fall back to the original when the copy does not load', () => {
    expect(thumbnailSources(own)).toEqual([`${own}?w=168`, own])
  })
})

describe('product frames', () => {
  let host: HTMLDivElement
  let root: Root
  beforeEach(() => { host = document.createElement('div'); document.body.append(host); root = createRoot(host) })
  afterEach(() => { act(() => root.unmount()); host.remove(); document.body.innerHTML = '' })

  it('load the first rows at once and first, with their size and both copies', () => {
    act(() => root.render(<RrThumb src={own} alt="Bluz" priority />))
    const image = host.querySelector('img')!
    expect(image.getAttribute('src')).toBe(`${own}?w=168`)
    expect(image.getAttribute('srcset')).toBe(`${own}?w=168 168w, ${own}?w=480 480w`)
    expect(image.getAttribute('sizes')).toBe('(max-width: 760px) 92px, 76px')
    expect(image.getAttribute('loading')).toBe('eager')
    expect(image.getAttribute('fetchpriority')).toBe('high')
    expect([image.getAttribute('width'), image.getAttribute('height')]).toEqual(['56', '84'])
  })

  it('leave the other rows lazy, and a marketplace photo without a srcset', () => {
    act(() => root.render(<RrThumb src="https://cdn.dsmcdn.com/ty1/a.jpg" alt="Bluz" />))
    const image = host.querySelector('img')!
    expect(image.getAttribute('loading')).toBe('lazy')
    expect(image.hasAttribute('fetchpriority')).toBe(false)
    expect(image.hasAttribute('srcset')).toBe(false)
  })

  it('open our photo large as the 480 px copy, the original for dense screens', () => {
    act(() => root.render(<RrImagePreview title="Bluz" sources={[own]} onClose={() => {}} />))
    const image = document.querySelector<HTMLImageElement>('.rr-img-preview img')!
    expect(image.getAttribute('src')).toBe(`${own}?w=480`)
    expect(image.getAttribute('srcset')).toBe(`${own}?w=480 480w, ${own} 1200w`)
  })
})
