import { describe, expect, it } from 'vitest'
import { isSettledPublicationJob, publicationJobPollInterval, publicationJobPollMs } from './publication-job-poll'

describe('publication job card state', () => {
  it('shows a blocked or manual-review job as finished, like the poll that stops reading it', () => {
    for (const status of ['BLOCKED', 'MANUAL_REVIEW', ' blocked ', 'SUCCEEDED', 'DEAD', 'CANCELLED']) expect(isSettledPublicationJob(status)).toBe(true)
    for (const status of ['QUEUED', 'LEASED', 'RETRY_SCHEDULED', undefined]) expect(isSettledPublicationJob(status)).toBe(false)
  })
})

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
