import { UiIcon } from './UiIcon'

export function Busy({ text = 'Veriler yükleniyor…' }: { text?: string }) {
  return <div className="status inline rv-loading-feedback" role="status" aria-live="polite"><span className="rv-loading-feedback-icon" aria-hidden="true"><UiIcon name="sync" /></span><span className="rv-loading-feedback-copy"><strong>{text}</strong><small>Veriler hazırlanıyor</small></span><span className="rv-loading-feedback-lines" aria-hidden="true"><i /><i /><i /></span></div>
}

export function ErrorBox({ error }: { error: unknown }) {
  return <div role="alert" className="error">{error instanceof Error ? error.message : 'İşlem tamamlanamadı.'}</div>
}
