import { isValidElement, type ReactElement } from 'react'
import { describe, expect, it } from 'vitest'
import { unknownCount, withRememberedCounts } from './tab-counts'

const stored = { ALL: '1.204', WAITING: 7 }

describe('remembered tab counts', () => {
  it('shows a remembered count only while the fresh one loads, dimmed and titled as being updated', () => {
    const [all] = withRememberedCounts([{ value: 'ALL', count: undefined }], stored)
    expect(isValidElement(all.count)).toBe(true)
    const element = all.count as ReactElement<{ className: string; title: string; children: unknown }>
    expect(element.props.className).toBe('rv-count-stale')
    expect(element.props.title).toBe('Güncelleniyor')
    expect(element.props.children).toBe('1.204')
  })

  it('shows "—" for a count that could not be read, never the old number', () => {
    const [waiting] = withRememberedCounts([{ value: 'WAITING', count: unknownCount }], stored)
    expect(waiting.count).toBe('—')
  })

  it('shows the fresh count as it is once it has arrived', () => {
    const [waiting] = withRememberedCounts([{ value: 'WAITING', count: 9 }], stored)
    expect(waiting.count).toBe(9)
  })

  it('leaves a tab with nothing remembered as it was', () => {
    const [other] = withRememberedCounts([{ value: 'OTHER', count: undefined }], stored)
    expect(other.count).toBeUndefined()
  })
})
