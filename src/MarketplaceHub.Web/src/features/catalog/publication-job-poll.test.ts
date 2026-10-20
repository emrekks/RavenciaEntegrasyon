import { describe, expect, it } from 'vitest'
import { publicationJobPollInterval, publicationJobPollMs } from './publication-job-poll'

describe('publication job poll', () => {
  it('runs while the worker still moves the job on', () => {
    for (const status of [undefined, 'PENDING', 'LEASED', 'RETRY_SCHEDULED']) expect(publicationJobPollInterval(status, false)).toBe(publicationJobPollMs)
  })

  it('stops on every settled state and when the job cannot be read', () => {
    for (const status of ['SUCCEEDED', 'DEAD', 'CANCELLED', 'MANUAL_REVIEW', 'BLOCKED', ' manual_review ']) expect(publicationJobPollInterval(status, false)).toBe(false)
    expect(publicationJobPollInterval(undefined, true)).toBe(false)
    expect(publicationJobPollInterval('LEASED', true)).toBe(false)
  })
})
