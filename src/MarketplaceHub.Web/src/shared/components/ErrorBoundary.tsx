import { Component, type ErrorInfo, type ReactNode } from 'react'
import { UiIcon } from './UiIcon'
import { errorText } from './AsyncState'

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
    if (this.state.failed && previous.resetKey !== this.props.resetKey) this.setState({ failed: false, error: null })
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    console.error('Ekran çizilirken hata oluştu.', error, info.componentStack)
  }

  render() {
    if (!this.state.failed) return this.props.children
    return <div className="rv-state is-error" role="alert">
      <span className="rv-state-mark" aria-hidden="true"><UiIcon name="alert" size={22} /></span>
      <strong>{this.props.title ?? 'Bu ekran açılamadı'}</strong>
      <p>{errorText(this.state.error)} Tekrar deneyebilir ya da sayfayı yenileyebilirsiniz.</p>
      <div className="rv-state-actions">
        <button type="button" className="rv-state-retry is-primary" onClick={() => this.setState({ failed: false, error: null })}><UiIcon name="refresh" size={16} />Tekrar Dene</button>
        <button type="button" className="rv-state-retry is-quiet" onClick={() => window.location.reload()}>Sayfayı Yenile</button>
      </div>
    </div>
  }
}
