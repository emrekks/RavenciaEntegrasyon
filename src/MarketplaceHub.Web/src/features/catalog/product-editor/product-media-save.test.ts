import { describe, expect, it } from 'vitest'
import { mapWithConcurrency, mediaSaveProgressMessage, storedProductMediaUrl, variantMediaReconcilePlan } from './product-media-save'

describe('variantMediaReconcilePlan', () => {
  const stored = new Map([['a', storedProductMediaUrl('11111111-1111-1111-1111-111111111111')], ['b', storedProductMediaUrl('22222222-2222-2222-2222-222222222222')]])

  it('references the stored copy of each uploaded file at its own position, links as they are', () => {
    const plan = variantMediaReconcilePlan(['file|b', 'url|https://cdn.example.com/x.jpg', 'file|a'], stored, new Set(['a', 'b']))
    expect(plan.items).toEqual([
      { url: '/api/v1/files/product-media/22222222-2222-2222-2222-222222222222/content', sortOrder: 0 },
      { url: 'https://cdn.example.com/x.jpg', sortOrder: 1 },
      { url: '/api/v1/files/product-media/11111111-1111-1111-1111-111111111111/content', sortOrder: 2 }
    ])
    expect(plan.uploads).toEqual([])
  })

  it('leaves a file with no stored copy for its own upload and drops a file that is gone', () => {
    const plan = variantMediaReconcilePlan(['file|a', 'file|c', 'file|gone'], stored, new Set(['a', 'c']))
    expect(plan.items).toEqual([{ url: stored.get('a'), sortOrder: 0 }])
    expect(plan.uploads).toEqual([{ fileId: 'c', sortOrder: 1 }])
  })

  it('makes 18 Beden × Renk rows sharing 3 files per colour need no variant upload', () => {
    const files = new Map(Array.from({ length: 18 }, (_, index) => [`f${index}`, storedProductMediaUrl(`0000000${index}`)]))
    const rows = Array.from({ length: 18 }, (_, row) => [0, 1, 2].map(offset => `file|f${(Math.floor(row / 3) * 3) + offset}`))
    const uploads = rows.flatMap(refs => variantMediaReconcilePlan(refs, files, new Set(files.keys())).uploads)
    expect(uploads).toHaveLength(0)
  })
})

describe('mapWithConcurrency', () => {
  it('keeps at most the limit running and returns results in input order', async () => {
    let running = 0; let peak = 0
    const results = await mapWithConcurrency([5, 1, 4, 2, 3, 0], 3, async value => {
      running++; peak = Math.max(peak, running)
      await new Promise(resolve => setTimeout(resolve, value))
      running--
      return value * 10
    })
    expect(results).toEqual([50, 10, 40, 20, 30, 0])
    expect(peak).toBe(3)
  })

  it('throws the first failure and starts no new task after it', async () => {
    const started: number[] = []
    await expect(mapWithConcurrency([0, 1, 2, 3, 4], 2, async value => {
      started.push(value)
      if (value === 1) throw new Error('boom')
      await new Promise(resolve => setTimeout(resolve, 5))
      return value
    })).rejects.toThrow('boom')
    expect(started).toEqual([0, 1])
  })

  it('handles an empty list', async () => {
    expect(await mapWithConcurrency([], 4, async () => 1)).toEqual([])
  })
})

it('names the step and the count', () => {
  expect(mediaSaveProgressMessage('upload', 5, 18)).toBe('Görseller yükleniyor (5/18)…')
})
