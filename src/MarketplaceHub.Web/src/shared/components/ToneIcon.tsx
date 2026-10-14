export type ToneIconKind = 'success' | 'error' | 'info'

// One icon family for every notification surface (toasts and the bell history): a ring with a glyph
// that says what happened, so a tick, a cross and an "i" can never be confused at a glance.
export function ToneIcon({ kind, size = 20 }: { kind: ToneIconKind; size?: number }) {
  const glyph = kind === 'success'
    ? <path d="m8.4 12.3 2.5 2.5 4.7-5.3" />
    : kind === 'error'
      ? <path d="m9.2 9.2 5.6 5.6m0-5.6-5.6 5.6" />
      : <path d="M12 11.2v4.8m0-8.2h.01" />
  return <svg className="rv-tone-icon" viewBox="0 0 24 24" width={size} height={size} fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" focusable="false" aria-hidden="true">
    <circle cx="12" cy="12" r="9.25" />
    {glyph}
  </svg>
}
