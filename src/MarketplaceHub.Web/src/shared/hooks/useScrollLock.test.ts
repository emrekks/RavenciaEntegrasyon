import { afterEach, describe, expect, it } from 'vitest'
import { lockPageScroll, releaseOrphanedScrollLock } from './useScrollLock'

afterEach(() => { releaseOrphanedScrollLock(); document.body.style.overflow = '' })

describe('root lock (a screen that must fit the viewport)', () => {
  it('holds the document still while any root lock is held and gives back the page’s own values', () => {
    document.documentElement.style.height = 'auto'
    const overlay = lockPageScroll()
    const first = lockPageScroll(false, { root: true })
    const second = lockPageScroll(false, { root: true })
    const html = document.documentElement.style
    expect([html.overflow, html.height, html.overscrollBehavior, document.body.style.height, document.body.style.overscrollBehavior]).toEqual(['hidden', '100%', 'none', '100%', 'none'])
    first()
    expect(html.overflow).toBe('hidden')
    second()
    expect([html.overflow, html.height, html.overscrollBehavior, document.body.style.height]).toEqual(['', 'auto', '', ''])
    expect(document.body.style.overflow).toBe('hidden')
    overlay()
    expect(document.body.style.overflow).toBe('')
    document.documentElement.style.height = ''
  })

  it('stays while held and is cleared when nobody holds it any more', () => {
    const release = lockPageScroll(false, { root: true })
    const html = document.documentElement.style
    releaseOrphanedScrollLock()
    expect(html.overflow).toBe('hidden')
    release()
    // a lock style left behind without its owner (the page came back from the browser cache)
    html.overflow = 'hidden'; html.height = '100%'; document.body.style.overscrollBehavior = 'none'
    releaseOrphanedScrollLock()
    expect([html.overflow, html.height, document.body.style.overscrollBehavior]).toEqual(['', '', ''])
  })
})

describe('page scroll lock', () => {
  it('stays locked until the last overlay closes', () => {
    const first = lockPageScroll()
    const second = lockPageScroll()
    first()
    expect(document.body.style.overflow).toBe('hidden')
    second()
    expect(document.body.style.overflow).toBe('')
  })

  it('leaves a held lock alone and clears a lock style nobody holds', () => {
    const release = lockPageScroll()
    releaseOrphanedScrollLock()
    expect(document.body.style.overflow).toBe('hidden')
    release()
    document.body.style.overflow = 'hidden'
    releaseOrphanedScrollLock()
    expect(document.body.style.overflow).toBe('')
  })
})
