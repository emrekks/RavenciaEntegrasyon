// @vitest-environment jsdom
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { CardBackgroundIcon } from './CardBackgroundIcon'

describe('CardBackgroundIcon', () => {
  it('is a decorative clipping layer holding one svg, with no tone by default', () => {
    const html = renderToStaticMarkup(createElement(CardBackgroundIcon, { name: 'truck' }))
    expect(html).toContain('class="rv-card-bg"')
    expect(html).toContain('aria-hidden="true"')
    expect(html).not.toContain('data-tone')
    expect(html.match(/<svg/g)).toHaveLength(1)
  })

  it('carries the tone the card is in', () => {
    expect(renderToStaticMarkup(createElement(CardBackgroundIcon, { name: 'alert', tone: 'bad' }))).toContain('data-tone="bad"')
  })
})
