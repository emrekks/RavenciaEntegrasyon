import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { SyncPolicyStalledAlert } from './sync-policy-stalled-alert'

describe('SyncPolicyStalledAlert', () => {
  it('renders an accessible, actionable warning in the sync flow card', () => {
    const markup = renderToStaticMarkup(<SyncPolicyStalledAlert compact lastCursorAdvancedAt="2026-10-07T10:00:00.000Z" />)

    expect(markup).toContain('role="alert"')
    expect(markup).toContain('Yeni kayıt alındı ancak imleç ilerlemedi.')
    expect(markup).toContain('<time dateTime="2026-10-07T10:00:00.000Z">')
  })

  it('shows both stagnation start and last progress in the details alert', () => {
    const markup = renderToStaticMarkup(<SyncPolicyStalledAlert cursorStagnantSince="2026-10-07T09:00:00.000Z" lastCursorAdvancedAt="2026-10-07T08:00:00.000Z" />)

    expect(markup).toContain('role="alert"')
    expect(markup).toContain('Takip başlangıcı:')
    expect(markup).toContain('Son imleç ilerlemesi:')
    expect(markup.match(/<time dateTime=/g)).toHaveLength(2)
  })

  it('handles missing timestamps without producing invalid date text', () => {
    const markup = renderToStaticMarkup(<SyncPolicyStalledAlert />)

    expect(markup).toContain('Takip başlangıcı: Henüz yok')
    expect(markup).toContain('Son imleç ilerlemesi: Henüz yok')
    expect(markup).not.toContain('Invalid Date')
  })
})
