export class RecentOperationEventIds {
  private readonly seen = new Set<string>()
  private readonly insertionOrder: string[] = []

  constructor(private readonly capacity = 10_000) {
    if (!Number.isInteger(capacity) || capacity < 1) throw new Error('Event deduplication capacity must be a positive integer.')
  }

  shouldProcess(eventIds: readonly (string | undefined)[]): boolean {
    if (eventIds.length === 0) return true

    let hasNewEvent = false
    for (const rawEventId of eventIds) {
      const eventId = rawEventId?.trim()
      if (!eventId) {
        hasNewEvent = true
        continue
      }
      if (this.seen.has(eventId)) continue

      hasNewEvent = true
      this.seen.add(eventId)
      this.insertionOrder.push(eventId)
      if (this.insertionOrder.length > this.capacity) {
        const oldest = this.insertionOrder.shift()
        if (oldest) this.seen.delete(oldest)
      }
    }
    return hasNewEvent
  }
}
