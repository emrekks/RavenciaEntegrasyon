import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { sendWithVersionedIdempotencyKey, versionedIdempotencyKey } from '../../shared/idempotency'
import { Button, Modal, UiIcon } from '../../shared/components'
import './question-report.css'

// "Soruyu Bildir": a customer question (advertising, insults, no question at all) is reported to the marketplace
// instead of being answered. Each marketplace has its own rules, read from the server: whether it takes a report
// over its API, the reasons it offers and the states that allow it.
export type ReportableQuestion = {
  id: string; platformCode: string; status: string; version: number; externalQuestionId: string
  expiresAt?: string | null; answerSubmissionStatus?: string | null
}
export type QuestionReportReason = { code: string; label: string; noteRequired: boolean }
export type QuestionReportCapability = {
  platformCode: string; supported: boolean; unsupportedReason?: string | null; reasons: QuestionReportReason[]
  allowedStatuses: string[]; resultStatus?: string | null; noteMinLength: number; noteMaxLength: number
}
type ReportNotice = { message: string; kind: 'success' | 'error' | 'info' }

const platformNames: Record<string, string> = { TRENDYOL: 'Trendyol', HEPSIBURADA: 'Hepsiburada', N11: 'N11', PAZARAMA: 'Pazarama' }
const platformName = (code: string) => platformNames[code] ?? code

/** Why this question cannot be reported now, or null when it can. The same rules as the server's. */
export function questionReportBlock(question: ReportableQuestion, capability: QuestionReportCapability | undefined, now = Date.now()): string | null {
  if (!capability || !Array.isArray(capability.reasons)) return 'Bildirim kuralları yükleniyor…'
  if (!capability.supported) return capability.unsupportedReason || `${platformName(question.platformCode)} soru bildirmeyi desteklemiyor.`
  if (['SUBMITTING', 'UNKNOWN', 'SUBMITTED'].includes(question.answerSubmissionStatus ?? '')) return 'Gönderilen cevap pazaryerinde işleniyor; soru bildirilemez.'
  if (!capability.allowedStatuses.includes(question.status)) return 'Yalnızca cevap bekleyen sorular bildirilebilir.'
  if (question.expiresAt && new Date(question.expiresAt).getTime() <= now) return 'Cevap süresi dolan soru bildirilemez.'
  return null
}

export function QuestionReportAction({ question, onNotice }: { question: ReportableQuestion; onNotice: (notice: ReportNotice) => void }) {
  const client = useQueryClient()
  const [open, setOpen] = useState(false)
  const [reasonCode, setReasonCode] = useState('')
  const [note, setNote] = useState('')
  const capabilityQuery = useQuery({
    queryKey: ['questions', 'report-reasons', question.platformCode],
    queryFn: () => hubApi<QuestionReportCapability>(`/questions/report-reasons?platform=${encodeURIComponent(question.platformCode)}`),
    staleTime: 60 * 60_000,
  })
  const capability = capabilityQuery.data
  const blocked = capabilityQuery.isError ? 'Bildirim kuralları alınamadı. Sayfayı yenileyip tekrar deneyin.' : questionReportBlock(question, capability)
  const reason = capability?.reasons?.find(item => item.code === reasonCode)
  const trimmedNote = note.trim()
  const noteTooShort = Boolean(reason?.noteRequired) && trimmedNote.length < (capability?.noteMinLength ?? 0)
  const noteTooLong = trimmedNote.length > (capability?.noteMaxLength ?? 0)
  const close = () => { setOpen(false); setReasonCode(''); setNote('') }

  const report = useMutation({
    mutationFn: ({ id, version, code, text }: { id: string; version: number; code: string; text: string }) => {
      const body = JSON.stringify({ version, reasonCode: code, note: text || null })
      return sendWithVersionedIdempotencyKey(versionedIdempotencyKey('question-report', id, version, body), key =>
        hubApi<ReportableQuestion>(`/questions/${id}/report`, { method: 'POST', headers: { 'Idempotency-Key': key }, body }))
    },
    onSuccess: async () => {
      close()
      onNotice({ message: `Soru ${platformName(question.platformCode)}’ya bildirildi; cevaplanmadan kapatılacak.`, kind: 'success' })
      await client.invalidateQueries({ queryKey: ['questions'] })
    },
    onError: async error => {
      close()
      onNotice({ message: error instanceof Error && error.message ? error.message : 'Soru bildirilemedi.', kind: 'error' })
      await client.invalidateQueries({ queryKey: ['questions'] })
    },
  })
  const submit = () => {
    if (!reason || noteTooShort || noteTooLong || report.isPending) return
    report.mutate({ id: question.id, version: question.version, code: reason.code, text: trimmedNote })
  }

  return <>
    {/* A disabled button shows no tooltip of its own, so the reason sits on its wrapper. */}
    <span className="qi-report-trigger" title={blocked ?? undefined}>
      <Button variant="secondary" className="qi-report-button" disabled={blocked !== null} onClick={() => setOpen(true)} aria-label={blocked ? `Soruyu Bildir: ${blocked}` : undefined}>
        <UiIcon name="alert" size={15} />Soruyu Bildir
      </Button>
    </span>
    <Modal
      open={open && blocked === null}
      title="Soruyu Bildir"
      description={`#${question.externalQuestionId} ${platformName(question.platformCode)}’ya bildirilir ve cevaplanmadan kapatılır. Müşteri yalnızca sorusunun yanıtlanmadığını görür; seçtiğiniz neden ve açıklamanız müşteriye gösterilmez. Bu işlem geri alınamaz.`}
      className="qi-confirm-modal qi-report-modal"
      onClose={close}
      footer={<><Button variant="secondary" onClick={close}>Vazgeç</Button><Button variant="danger" loading={report.isPending} disabled={!reason || noteTooShort || noteTooLong} onClick={submit}>Soruyu Bildir</Button></>}
    >
      <fieldset className="qi-report-reasons">
        <legend>Bildirim Nedeni</legend>
        {(capability?.reasons ?? []).map(item => <label key={item.code} className={item.code === reasonCode ? 'is-on' : undefined}>
          <input type="radio" name="question-report-reason" value={item.code} checked={item.code === reasonCode} onChange={() => setReasonCode(item.code)} />
          <span>{item.label}</span>
        </label>)}
      </fieldset>
      <label className="qi-report-note">
        <span>{reason?.noteRequired ? 'Açıklama (zorunlu)' : 'Açıklama (isteğe bağlı)'}</span>
        <textarea rows={3} maxLength={capability?.noteMaxLength || undefined} value={note} onChange={event => setNote(event.target.value)} placeholder="Pazaryeri ekibinin göreceği kısa açıklama" aria-label="Bildirim açıklaması" />
        <small className={noteTooShort || noteTooLong ? 'is-invalid' : undefined}>{noteTooShort ? `En az ${capability?.noteMinLength} karakter yazın · ` : ''}{trimmedNote.length}/{capability?.noteMaxLength ?? 0}</small>
      </label>
    </Modal>
  </>
}
