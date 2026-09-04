export function Busy({ text = 'Veriler yükleniyor…' }: { text?: string }) {
  return <div className="status inline" role="status"><div className="spinner" /><strong>{text}</strong></div>
}

export function ErrorBox({ error }: { error: unknown }) {
  return <div role="alert" className="error">{error instanceof Error ? error.message : 'İşlem tamamlanamadı.'}</div>
}
