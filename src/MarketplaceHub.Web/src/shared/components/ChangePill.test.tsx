import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ChangePill, newChange, trendChange } from './ChangePill'

describe('ChangePill', () => {
  it('writes the glyph, the sign and the tone like the Raporlar cards', () => {
    expect(renderToStaticMarkup(<ChangePill change={{ text: '+%49', direction: 'up' }} />)).toContain('rv-change is-good')
    expect(renderToStaticMarkup(<ChangePill change={{ text: '+%49', direction: 'up' }} />)).toContain('▲ +%49')
    expect(renderToStaticMarkup(<ChangePill change={{ text: '−%12', direction: 'down' }} />)).toContain('▼ −%12')
    expect(renderToStaticMarkup(<ChangePill change={{ text: '−%12', direction: 'down' }} />)).toContain('is-bad')
  })
  it('turns the colours round when up is bad and reads a flat zero without a comparison', () => {
    expect(renderToStaticMarkup(<ChangePill change={{ text: '+%5', direction: 'up' }} invert />)).toContain('is-bad')
    const none = renderToStaticMarkup(<ChangePill change={null} />)
    expect(none).toContain('■ %0')
    expect(none).toContain('is-flat')
  })
  it('turns a dashboard trend into the same signed change', () => {
    expect(trendChange(['%10,7', true])).toEqual({ text: '+%10,7', direction: 'up' })
    expect(trendChange(['-%33,3', false])).toEqual({ text: '−%33,3', direction: 'down' })
    expect(trendChange(['%0', true])).toEqual({ text: '%0', direction: 'flat' })
    expect(trendChange(['Yeni', true])).toEqual({ text: 'Yeni', direction: 'up' })
  })
  it('reads a rise from nothing as a green Yeni and a previous period without data as a neutral dash', () => {
    const fresh = renderToStaticMarkup(<ChangePill change={newChange} />)
    expect(fresh).toContain('▲ Yeni')
    expect(fresh).toContain('is-good')
    const none = renderToStaticMarkup(<ChangePill change={newChange} noData />)
    expect(none).toContain('—')
    expect(none).toContain('is-flat')
    expect(none).toContain('Önceki dönem için veri yok')
  })
})
