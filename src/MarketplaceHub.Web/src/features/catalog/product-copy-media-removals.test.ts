import { describe, expect, it } from 'vitest'
import { copyRemovedMediaKeys, historyStateWithCopyRemovedMedia, withCopyRemovedMedia, withoutCopyRemovedMedia, withoutCopyRemovedMediaRefs } from './product-copy-media-removals'

const front = 'https://cdn.example.com/products/front.jpg'
const back = 'https://cdn.example.com/products/back.jpg'
const stored = '/api/v1/files/product-media/0f8fad5b-d9cb-469f-a165-70867728950e/content'

describe('product copy media removals', () => {
  it('reads only string keys from the route state', () => {
    expect(copyRemovedMediaKeys({ duplicateProductId: 'p1', copyRemovedMediaKeys: ['a', 3, '', 'b'] })).toEqual(['a', 'b'])
    expect(copyRemovedMediaKeys({ duplicateProductId: 'p1' })).toEqual([])
    expect(copyRemovedMediaKeys(null)).toEqual([])
  })

  it('remembers a removed image once, whatever its spelling', () => {
    const keys = withCopyRemovedMedia([], [front])
    expect(withCopyRemovedMedia(keys, ['https://CDN.example.com/products/front.jpg/'])).toEqual(keys)
    expect(withCopyRemovedMedia(keys, [stored])).toHaveLength(2)
  })

  it('leaves a removed image out of the copy when it is seeded again', () => {
    const keys = withCopyRemovedMedia([], [front, stored])
    expect(withoutCopyRemovedMedia([front, back, stored], keys)).toEqual([back])
    expect(withoutCopyRemovedMediaRefs([`url|${front}`, `url|${back}`, 'file|local-1'], keys)).toEqual([`url|${back}`, 'file|local-1'])
  })

  it('keeps everything when nothing was removed', () => {
    expect(withoutCopyRemovedMedia([front, back], [])).toEqual([front, back])
    expect(withoutCopyRemovedMediaRefs([`url|${front}`], [])).toEqual([`url|${front}`])
  })

  it('writes the keys into the router state and keeps its other fields', () => {
    const next = historyStateWithCopyRemovedMedia({ key: 'k1', idx: 2, usr: { duplicateProductId: 'p1' } }, ['a'])
    expect(next).toEqual({ key: 'k1', idx: 2, usr: { duplicateProductId: 'p1', copyRemovedMediaKeys: ['a'] } })
    expect(copyRemovedMediaKeys(next.usr)).toEqual(['a'])
    expect(historyStateWithCopyRemovedMedia(null, ['a'])).toEqual({ usr: { copyRemovedMediaKeys: ['a'] } })
  })
})
