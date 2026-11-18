import { act, Profiler } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '../test/render'
import { DateRangePicker } from './DateRangePicker'

afterEach(() => { cleanup(); vi.restoreAllMocks(); document.body.innerHTML = '' })

describe('DateRangePicker scroll', () => {
  // Every scroll frame set a new, equal position and drew the open calendar again.
  it('DateRangePicker does not redraw on a scroll that does not move it', async () => {
    const frames: FrameRequestCallback[] = []
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
    const commits = vi.fn()
    render(<Profiler id="range" onRender={commits}><DateRangePicker from="" to="" onChange={() => undefined} /></Profiler>)
    act(() => document.querySelector<HTMLButtonElement>('.date-range-picker-trigger')!.click())
    await act(async () => { frames.splice(0).forEach(callback => callback(0)) })
    commits.mockClear()
    for (let index = 0; index < 3; index += 1) {
      await act(async () => { window.dispatchEvent(new Event('scroll')); frames.splice(0).forEach(callback => callback(0)) })
    }
    // At most the first frame after opening settles; three scrolls that move nothing draw nothing.
    expect(commits.mock.calls.length).toBeLessThanOrEqual(1)
  })
})
