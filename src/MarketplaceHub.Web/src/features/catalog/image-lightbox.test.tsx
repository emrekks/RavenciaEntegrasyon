import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { ImageLightboxModal, lightboxCodes } from './shared'

// The product photo preview names the product first and puts its codes line under the name.
describe('ImageLightboxModal header', () => {
  afterEach(cleanup)

  it('formats the codes line from what is known', () => {
    expect(lightboxCodes({ modelCode: 'RYP004' })).toBe('Model Kodu: RYP004')
    expect(lightboxCodes({ modelCode: 'RYP004', color: 'Lacivert', size: 'M' })).toBe('Model Kodu: RYP004 · Renk: Lacivert · Beden: M')
    expect(lightboxCodes({ modelCode: ' ', color: 'Lacivert' })).toBe('')
    expect(lightboxCodes({ modelCode: '—' })).toBe('')
    expect(lightboxCodes({})).toBe('')
  })

  it('shows the model code under the product name', () => {
    render(<ImageLightboxModal image={{ url: '/a.jpg', title: 'Kadın Kazak', modelCode: 'RYP004', color: 'Lacivert' }} onClose={() => undefined} />)
    const head = document.querySelector('.rr-m-head > div')!
    const children = [...head.children]
    expect(children.map(node => node.tagName)).toEqual(['H2', 'P'])
    expect(children[0].textContent).toBe('Kadın Kazak')
    expect(children[1].textContent).toBe('Model Kodu: RYP004 · Renk: Lacivert')
    expect(head.querySelector('.rr-crumb')).toBeNull()
  })

  it('shows no codes line without a model code', () => {
    render(<ImageLightboxModal image={{ url: '/a.jpg', title: 'Kadın Kazak' }} onClose={() => undefined} />)
    expect(document.querySelector('.rr-m-head > div')!.children).toHaveLength(1)
  })
})
