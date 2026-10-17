import { UiIcon } from './UiIcon'

// Loading and error states fill the area they replace (a page section, a modal body) like the full-window splash:
// the brand mark in a turning ring, a title, a short line and the loading bar; errors use the same frame.
export function Busy({ text = 'Veriler yükleniyor…' }: { text?: string }) {
  return <div className="rv-state rv-loading-feedback" role="status" aria-live="polite">
    <span className="rv-state-mark" aria-hidden="true"><img src="/pack/brand/ravencia-symbol-transparent.png" alt="" /></span>
    <strong>{text}</strong>
    <p>Bu birkaç saniye sürebilir.</p>
    <span className="rv-splash-loading-bar" aria-hidden="true"><span className="rv-splash-loading-progress" /></span>
  </div>
}

function errorText(error: unknown) {
  const message = error instanceof Error ? error.message : ''
  if (!message) return 'İşlem tamamlanamadı.'
  if (/failed to fetch|networkerror|load failed|network request failed/i.test(message)) return 'Sunucuya ulaşılamadı. İnternet bağlantınızı kontrol edip tekrar deneyin.'
  return message
}

export function ErrorBox({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  return <div className="rv-state is-error" role="alert">
    <span className="rv-state-mark" aria-hidden="true"><UiIcon name="alert" size={22} /></span>
    <strong>Veriler alınamadı</strong>
    <p>{errorText(error)}</p>
    {onRetry && <button type="button" className="rv-state-retry" onClick={onRetry}><UiIcon name="refresh" size={16} />Tekrar Dene</button>}
  </div>
}
