import { afterEach, describe, expect, it } from 'vitest'
import { lockPageScroll, releaseOrphanedScrollLock } from './useScrollLock'

afterEach(() => { document.body.style.overflow = '' })

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
