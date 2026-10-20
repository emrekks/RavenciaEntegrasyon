import { UiIcon } from './UiIcon'
import { autoRetryLine, useAutoRetry, type AutoRetry } from '../hooks/useAutoRetry'

// Loading and error states fill the area they replace (a page section, a modal body) like the full-window splash:
// the brand mark in a turning ring, a title, a short line and the loading bar; errors use the same frame.
export function Busy({ text = 'Veriler yükleniyor…' }: { text?: string }) {
  return <div className="rv-state rv-loading-feedback" role="status" aria-live="polite">
    <span className="rv-state-mark" aria-hidden="true"><img src="/pack/brand/ravencia-symbol-128.webp" alt="" /></span>
    <strong>{text}</strong>
    <p>Bu birkaç saniye sürebilir.</p>
    <span className="rv-splash-loading-bar" aria-hidden="true"><span className="rv-splash-loading-progress" /></span>
  </div>
}

export function errorText(error: unknown) {
  const message = error instanceof Error ? error.message : ''
  if (!message) return 'İşlem tamamlanamadı.'
  if (/failed to fetch|networkerror|load failed|network request failed/i.test(message)) return 'Sunucuya ulaşılamadı. İnternet bağlantınızı kontrol edin.'
  return message
}

// The countdown ring of an automatic retry: the seconds left in the middle, the ring empties as they run out and
// turns while the try is running.
export function RetryRing({ retry }: { retry: AutoRetry }) {
  const length = 2 * Math.PI * 26
  const share = retry.retrying ? 1 : retry.stopped ? 0 : retry.left / retry.total
  return <span className={`rv-retry-ring${retry.retrying ? ' is-busy' : ''}`} aria-hidden="true">
    <svg viewBox="0 0 60 60"><circle className="rv-retry-track" cx="30" cy="30" r="26" /><circle className="rv-retry-value" cx="30" cy="30" r="26" strokeDasharray={length} strokeDashoffset={length * (1 - share)} /></svg>
    {retry.retrying ? <UiIcon name="refresh" size={20} /> : retry.stopped ? <UiIcon name="alert" size={20} /> : <b>{retry.left}</b>}
  </span>
}

export function ErrorBox({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const retry = useAutoRetry(error, onRetry)
  return <div className="rv-state is-error" role="alert">
    {retry ? <RetryRing retry={retry} /> : <span className="rv-state-mark" aria-hidden="true"><UiIcon name="alert" size={22} /></span>}
    <strong>Veriler alınamadı</strong>
    <p>{errorText(error)}{retry && <span aria-hidden="true"> {autoRetryLine(retry)}</span>}</p>
    {retry && <div className="rv-state-actions">
      <button type="button" className="rv-state-retry is-primary" disabled={retry.retrying} onClick={retry.now}><UiIcon name="refresh" size={16} />{retry.stopped ? 'Tekrar Dene' : 'Şimdi Dene'}</button>
      {!retry.stopped && !retry.retrying && <button type="button" className="rv-state-retry is-quiet" onClick={retry.stop}>Durdur</button>}
    </div>}
  </div>
}
