import { afterEach, describe, expect, it } from 'vitest'
import { MemoryRouter } from 'react-router'
import { cleanup, render } from '../../test/render'
import { DockContext, type DockHost } from '../../shared/components/dock-actions'
import { BackTargetContext } from '../../shared/components/back-target'
import { Page } from './shared'

afterEach(cleanup)

const host = (phone: boolean): DockHost => ({ phone, actionsEl: null, infoEl: null, topId: null, register: () => () => undefined })
const page = (phone: boolean | null) => {
  const content = <MemoryRouter><Page title="Kadın Oversize Gabardin Trençkot" eyebrow="Ürün Düzenle" className="product-add-page"><p>içerik</p></Page></MemoryRouter>
  return phone === null ? content : <DockContext.Provider value={host(phone)}><BackTargetContext.Provider value={{ target: null, set: () => undefined }}>{content}</BackTargetContext.Provider></DockContext.Provider>
}

describe('the product page heading', () => {
  it('has its own back link on a wide screen (the top bar has none there)', () => {
    const { host: element } = render(page(false))
    expect(element.querySelector('a.product-heading-back')?.getAttribute('href')).toBe('/products')
    expect(element.querySelector('.product-page-heading-copy.is-solo')).toBeNull()
  })
  it('has no back link of its own on a phone (the shell draws the way back), and the title block is flush left', () => {
    const { host: element } = render(page(true))
    expect(element.querySelector('a.product-heading-back')).toBeNull()
    expect(element.querySelector('.product-page-heading-copy.is-solo > .product-page-heading-title h1')?.textContent).toBe('Kadın Oversize Gabardin Trençkot')
  })
  it('keeps the link where there is no shell around the page', () => {
    expect(render(page(null)).host.querySelector('a.product-heading-back')).not.toBeNull()
  })
})
