import { describe, expect, it } from 'vitest'

// O6 guard: hubApi marks every read made while the person is away as a background poll (automaticReadInit), so timers
// and realtime invalidations cannot keep an idle session alive. That only holds while every request goes through it:
// no module outside the API client may call fetch, XMLHttpRequest or EventSource on its own.
const sources = import.meta.glob(['../../**/*.{ts,tsx}', '!../../**/*.test.{ts,tsx}', '!../../test/**'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>

describe('every panel request goes through hubApi', () => {
  it('finds the sources it checks', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(50)
  })

  it('has no direct fetch, XMLHttpRequest or EventSource outside the API client', () => {
    const offenders = Object.entries(sources)
      .filter(([path]) => path !== './client.ts')
      .filter(([, source]) => /(^|[^.\w])fetch\(|window\.fetch\(|new XMLHttpRequest\(|new EventSource\(/m.test(source))
      .map(([path]) => path)
    expect(offenders).toEqual([])
  })

  it('still sees the API client itself, so the check above is not empty', () => {
    expect(sources['./client.ts']).toMatch(/automaticReadInit\(init\)/)
  })
})
