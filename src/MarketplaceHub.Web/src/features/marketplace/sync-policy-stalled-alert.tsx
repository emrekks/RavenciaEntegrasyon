type SyncPolicyStalledAlertProps = {
  lastCursorAdvancedAt?: string | null
  cursorStagnantSince?: string | null
  compact?: boolean
}

function displayTime(value?: string | null) {
  if (!value) return 'Henüz yok'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? 'Henüz yok' : date.toLocaleString('tr-TR')
}

function TimeValue({ value }: { value?: string | null }) {
  if (!value || Number.isNaN(new Date(value).getTime())) return <>Henüz yok</>
  return <time dateTime={value}>{displayTime(value)}</time>
}

export function SyncPolicyStalledAlert({ lastCursorAdvancedAt, cursorStagnantSince, compact = false }: SyncPolicyStalledAlertProps) {
  if (compact) {
    return <small className="sync-policy-health-error" role="alert">
      Yeni kayıt alındı ancak imleç ilerlemedi. Son ilerleme: <TimeValue value={lastCursorAdvancedAt} />.
    </small>
  }

  return <div className="sync-policy-modal-stalled-alert" role="alert">
    <strong>Son başarılı kontrolde yeni kayıt alındı ama imleç ilerlemedi.</strong>
    <span>Takip başlangıcı: <TimeValue value={cursorStagnantSince} />. Son imleç ilerlemesi: <TimeValue value={lastCursorAdvancedAt} />. Bağlantı kuyruğu ve hata sayılarını inceleyin.</span>
  </div>
}
