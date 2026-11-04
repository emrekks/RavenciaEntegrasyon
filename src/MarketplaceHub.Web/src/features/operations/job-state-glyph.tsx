type JobGlyphState = 'success' | 'running' | 'waiting' | 'error' | 'cancelled'

// The shared state glyphs of İşlem Takibi (the job list and the send log): filled check, turning arc while running,
// clock arc while waiting, red cross, dashed circle.
export function JobStateGlyph({ tone }: { tone: string }) {
  const state: JobGlyphState = tone === 'success' || tone === 'running' || tone === 'error' || tone === 'cancelled' ? tone : 'waiting'
  if (state === 'success') return <svg className="jb-glyph is-success" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="var(--rv-color-surface)" strokeWidth={1.7} strokeLinecap="round" strokeLinejoin="round" /></svg>
  if (state === 'running') return <svg className="jb-glyph is-running" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.8} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6" opacity=".25" /><path d="M8 2a6 6 0 0 1 6 6" /></svg>
  if (state === 'error') return <svg className="jb-glyph is-error" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /><path d="m5.8 5.8 4.4 4.4M10.2 5.8l-4.4 4.4" /></svg>
  if (state === 'cancelled') return <svg className="jb-glyph is-cancelled" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeDasharray="2.6 2.2" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /></svg>
  return <svg className="jb-glyph is-waiting" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" opacity=".35" /><path d="M8 1.8a6.2 6.2 0 0 1 5.9 4.3" /><path d="M8 5.6v2.6l1.8 1.1" /></svg>
}
