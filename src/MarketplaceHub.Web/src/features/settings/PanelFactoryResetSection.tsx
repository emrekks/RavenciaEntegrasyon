import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, hubPoll } from '../../shared/api'
import { formatIstanbul } from '../../shared/format-date'
import { clearNotificationHistory } from '../../shared/notifications'
import { ReauthInputs, reauthBody, reauthReady, useSecondFactorEnabled } from '../../shared/reauth'
import { normalizePanelResetConfirmation, panelResetConfirmationWord, panelResetConfirmed, panelResetFailed, panelResetRunning, panelResetStepStates, type PanelFactoryResetView, type PanelResetStepState } from './panel-factory-reset'

const resetNumber = (value: number) => value.toLocaleString('tr-TR')
const queryKey = ['panel-factory-reset']

function StepGlyph({ state }: { state: PanelResetStepState }) {
  if (state === 'done') return <svg className="ss-st is-done" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="var(--rv-color-surface)" strokeWidth={1.7} strokeLinecap="round" strokeLinejoin="round" /></svg>
  if (state === 'running') return <svg className="ss-st ss-fr-spin" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" opacity=".3" /><path d="M8 1.8a6.2 6.2 0 0 1 6.2 6.2" /></svg>
  if (state === 'failed') return <svg className="ss-st is-bad" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /><path d="m5.8 5.8 4.4 4.4M10.2 5.8l-4.4 4.4" /></svg>
  return <svg className="ss-st" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeDasharray="2.6 2.2" aria-hidden="true"><circle cx="8" cy="8" r="6.2" /></svg>
}

function MatchGlyph() {
  return <svg className="ss-st ss-fr-ok" viewBox="0 0 16 16" aria-label="Eşleşti" role="img"><circle cx="8" cy="8" r="7" fill="currentColor" /><path d="m5 8.2 2.1 2.1 3.9-4.2" fill="none" stroke="#fff" strokeWidth={1.7} strokeLinecap="round" strokeLinejoin="round" /></svg>
}

function AlertGlyph() {
  return <svg className="ss-icon" width={16} height={16} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth={1.6} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M8 2.5 14 13H2z" /><path d="M8 6.5v3M8 11.5h.01" /></svg>
}

// Ayarlar › Veri › Paneli Tamamen Sıfırla: the danger zone at the bottom of the Veri card. Owner and administrators only
// (the server answers others 403 and the section stays hidden).
export function PanelFactoryResetSection() {
  const queryClient = useQueryClient()
  const view = useQuery({
    queryKey,
    queryFn: () => hubPoll<PanelFactoryResetView>('/settings/factory-reset'),
    staleTime: 0,
    retry: false,
    refetchInterval: query => panelResetRunning(query.state.data?.job?.status) ? 2000 : false
  })
  const [confirmation, setConfirmation] = useState('')
  // The reset takes the password again (and the authenticator code when it is on).
  const mfa = useSecondFactorEnabled()
  const [password, setPassword] = useState('')
  const [factor, setFactor] = useState('')
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const job = view.data?.job ?? null
  const running = panelResetRunning(job?.status)
  const word = view.data?.confirmationWord ?? panelResetConfirmationWord
  const confirmed = panelResetConfirmed(confirmation, word)
  const ready = confirmed && reauthReady(password, factor, mfa)

  // Once the job ends, every screen reads again: nothing of what they cached is left.
  const wasRunning = useRef(false)
  useEffect(() => {
    if (running) { wasRunning.current = true; return }
    if (wasRunning.current && job) {
      wasRunning.current = false
      // The notification history lives in this browser: the Bildirimler category takes it too.
      if (job.status === 'SUCCEEDED' && job.categories.includes('NOTIFICATIONS')) clearNotificationHistory()
      void queryClient.invalidateQueries({ predicate: query => query.queryKey[0] !== queryKey[0] })
    }
  }, [running, job, queryClient])

  if (view.error instanceof ApiRequestError && view.error.status === 403) return null

  const start = async () => {
    if (!ready || sending || running) return
    setSending(true)
    setError(null)
    try {
      const next = await hubApi<PanelFactoryResetView>('/settings/factory-reset', { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify({ confirmation: normalizePanelResetConfirmation(confirmation), ...reauthBody(password, factor) }) })
      queryClient.setQueryData(queryKey, next)
      setConfirmation(''); setPassword(''); setFactor('')
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Sıfırlama başlatılamadı.')
    } finally {
      setSending(false)
    }
  }

  const steps = job?.steps ?? view.data?.steps ?? []
  const states = panelResetStepStates(steps, job)
  const failed = panelResetFailed(job?.status)
  const showProgress = job !== null && (running || failed || job.status === 'SUCCEEDED')
  const percent = job?.status === 'SUCCEEDED' ? 100 : job?.progressPercent ?? 0

  return <section className="ss-dr-group ss-fr" aria-labelledby="panel-factory-reset-title">
    <header className="ss-dr-gh">
      <span className="ss-tile is-sm ss-fr-ico"><AlertGlyph /></span>
      <div><h3 id="panel-factory-reset-title">Paneli Tamamen Sıfırla</h3><p>Panelin bütün kayıtları ve logları silinir; hesap, kullanıcılar ve entegrasyon bağlantıları kalır.</p></div>
      <span className="ss-tag ss-fr-total">{view.data ? `${resetNumber(view.data.total)} Kayıt` : view.isError ? 'Sayı Alınamadı' : 'Sayılıyor…'}</span>
    </header>
    <div className="ss-fr-cols">
      <div className="ss-fr-col" aria-label="Silinecekler">
        <h4>Silinecekler</h4>
        <ul>{(view.data?.groups ?? []).map(group => <li key={group.key}><span>{group.label}</span><b>{resetNumber(group.count)}</b></li>)}</ul>
      </div>
      <div className="ss-fr-col is-kept" aria-label="Kalanlar">
        <h4>Kalanlar</h4>
        <ul>{(view.data?.kept ?? []).map(item => <li key={item}><span>{item}</span></li>)}</ul>
      </div>
    </div>
    <div className="ss-callout ss-dr-note ss-fr-note">
      <p className="is-warn"><AlertGlyph /><span><b>Geri alınamaz.</b> Silinen kayıtlar hiçbir yerden geri getirilemez; pazaryerlerine ve e-fatura sağlayıcısına hiçbir şey gönderilmez. Bu oturum açık kalır, diğer cihazlardaki oturumlar kapanır.</span></p>
      <p><AlertGlyph /><span>Açık bağlantılar sıfırlamadan sonra pazaryerinin izin verdiği tarih aralığındaki siparişleri yeniden çeker; hiç veri gelmesin istiyorsanız önce bağlantıları kapatın.</span></p>
    </div>
    {showProgress && job && <div className="ss-fr-progress" role="status" aria-live="polite">
      <div className="ss-fr-bar" aria-hidden="true"><i style={{ width: `${percent}%` }} /></div>
      <div className="ss-fr-progress-head"><strong>{job.full ? 'Tam Sıfırlama İlerlemesi' : 'Sıfırlama İlerlemesi'}</strong><span>%{percent}{job.status === 'SUCCEEDED' && job.completedAt ? ` · ${formatIstanbul(job.completedAt, { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' })}` : ''}</span></div>
      <ol className="ss-fr-steps">{steps.map((step, index) => <li key={step} className={`is-${states[index]}`}><StepGlyph state={states[index]} /><span>{step}</span></li>)}</ol>
      {failed && <p className="ss-fr-error">{job.error ?? 'Sıfırlama tamamlanamadı.'}</p>}
    </div>}
    {!running && <div className="ss-fr-confirm">
      <label htmlFor="panel-factory-reset-confirm" className={confirmed ? 'is-match' : undefined}>{confirmed ? <MatchGlyph /> : null}<span>Onaylamak için <b>{word}</b> yazın ve parolanızı{mfa ? ' ile doğrulama kodunuzu' : ''} girin</span></label>
      <div className="ss-fr-confirm-row">
        <input id="panel-factory-reset-confirm" className={`ss-in ss-fr-in${confirmed ? ' is-match' : ''}`} value={confirmation} onChange={event => setConfirmation(event.target.value)} disabled={sending || !view.data} autoComplete="off" spellCheck={false} />
        <ReauthInputs bare idPrefix="panel-factory-reset" password={password} onPassword={setPassword} factor={factor} onFactor={setFactor} mfa={mfa} disabled={sending || !view.data} inputClassName="ss-in ss-fr-in ss-fr-auth" />
        <button type="button" className="ss-btn is-danger is-solid ss-fr-go" disabled={!ready || sending || !view.data} onClick={() => void start()}>{sending ? 'Başlatılıyor…' : 'Paneli Sıfırla'}</button>
      </div>
      {error && <p className="ss-fr-error" role="alert">{error}</p>}
    </div>}
  </section>
}
