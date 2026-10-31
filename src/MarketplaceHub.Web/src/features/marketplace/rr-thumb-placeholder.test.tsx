import { act } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '../../test/render'
import { RrThumb } from './returns-ui'

// An order line with no known photo (or none that loads) shows a deliberate no-photo frame, not an empty grey box,
// and keeps its quantity badge.
describe('RrThumb', () => {
  afterEach(cleanup)

  it('shows the no-photo placeholder when no address is known', () => {
    const { host } = render(<RrThumb src={null} quantity={2} alt="Bluz" onOpen={() => undefined} />)
    expect(host.querySelector('.rr-thumb-none')?.getAttribute('title')).toBe('Görsel yok')
    expect(host.querySelector('img')).toBeNull()
    expect(host.querySelector('.rr-qb')?.textContent).toBe('2')
  })

  it('falls back to the placeholder after every address failed to load', () => {
    const { host } = render(<RrThumb src="/a.jpg" fallbackSrc="/b.jpg" alt="Bluz" />)
    for (const expected of ['/a.jpg', '/b.jpg']) {
      const image = host.querySelector('img')!
      expect(image.getAttribute('src')).toBe(expected)
      expect(host.querySelector('.rr-thumb-none')).toBeNull()
      act(() => { image.dispatchEvent(new Event('error')) })
    }
    expect(host.querySelector('img')).toBeNull()
    expect(host.querySelector('.rr-thumb-none')).not.toBeNull()
  })
})
