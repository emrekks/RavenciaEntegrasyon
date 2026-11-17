import { Component, type ErrorInfo, type ReactNode } from 'react'
import { UiIcon } from './UiIcon'
import { errorText } from './AsyncState'
import { isChunkLoadError, retryFailedLazyPages } from '../lazy-page'

type Props = {
  children: ReactNode
  /** A new value (the page address) clears the error, so leaving a broken screen works without a reload. */
  resetKey?: unknown
  title?: string
}
type State = { failed: boolean; error: unknown }

// A screen that throws while drawing shows this box in its place; the menu and the other screens keep working.
export class ErrorBoundary extends Component<Props, State> {
  state: State = { failed: false, error: null }

  static getDerivedStateFromError(error: unknown): State {
    return { failed: true, error }
  }

  componentDidUpdate(previous: Props) {
    if (this.state.failed && previous.resetKey !== this.props.resetKey) this.retry()
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    console.error('Ekran çizilirken hata oluştu.', error, info.componentStack)
  }

  // A screen whose code failed to download is downloaded again (lazy-page.ts), not served the stored failure.
  private retry() {
    retryFailedLazyPages()
    this.setState({ failed: false, error: null })
  }

  render() {
    if (!this.state.failed) return this.props.children
    return <div className="rv-state is-error" role="alert">
      <span className="rv-state-mark" aria-hidden="true"><UiIcon name="alert" size={22} /></span>
      <strong>{this.props.title ?? 'Bu ekran açılamadı'}</strong>
      <p>{isChunkLoadError(this.state.error)
        ? 'Ekranın kodu indirilemedi; bağlantınızı kontrol edip tekrar deneyin.'
        : <>{errorText(this.state.error)} Tekrar deneyebilir ya da sayfayı yenileyebilirsiniz.</>}</p>
      <div className="rv-state-actions">
        <button type="button" className="rv-state-retry is-primary" onClick={() => this.retry()}><UiIcon name="refresh" size={16} />Tekrar Dene</button>
        <button type="button" className="rv-state-retry is-quiet" onClick={() => window.location.reload()}>Sayfayı Yenile</button>
      </div>
    </div>
  }
}
