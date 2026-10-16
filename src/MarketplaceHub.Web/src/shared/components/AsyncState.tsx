import { Callout } from './DesignSystem'

export function Busy({ text = 'Veriler yükleniyor…' }: { text?: string }) {
  return <div className="status inline rv-loading-feedback" role="status" aria-live="polite"><strong>{text}</strong><span className="rv-splash-loading-bar" aria-hidden="true"><span className="rv-splash-loading-progress" /></span></div>
}

export function ErrorBox({ error }: { error: unknown }) {
  return <Callout tone="danger">{error instanceof Error ? error.message : 'İşlem tamamlanamadı.'}</Callout>
}
