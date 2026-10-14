import { describe, expect, it } from 'vitest'
import { RecentOperationEventIds } from './operations-event-deduplication'

describe('RecentOperationEventIds', () => {
  it('processes new events once and ignores a redelivered outbox event', () => {
    const recent = new RecentOperationEventIds()

    expect(recent.shouldProcess(['event-1'])).toBe(true)
    expect(recent.shouldProcess(['event-1'])).toBe(false)
  })

  it('processes a batch when at least one event is new', () => {
    const recent = new RecentOperationEventIds()
    expect(recent.shouldProcess(['event-1'])).toBe(true)

    expect(recent.shouldProcess(['event-1', 'event-2'])).toBe(true)
    expect(recent.shouldProcess(['event-1', 'event-2'])).toBe(false)
  })

  it('keeps messages without event identifiers processable', () => {
    const recent = new RecentOperationEventIds()

    expect(recent.shouldProcess([])).toBe(true)
    expect(recent.shouldProcess([undefined, ''])).toBe(true)
  })

  it('bounds memory by forgetting the oldest event identifier', () => {
    const recent = new RecentOperationEventIds(2)

    expect(recent.shouldProcess(['event-1'])).toBe(true)
    expect(recent.shouldProcess(['event-2'])).toBe(true)
    expect(recent.shouldProcess(['event-3'])).toBe(true)
    expect(recent.shouldProcess(['event-1'])).toBe(true)
  })

  it('rejects an invalid capacity', () => {
    expect(() => new RecentOperationEventIds(0)).toThrow('capacity must be a positive integer')
  })
})
