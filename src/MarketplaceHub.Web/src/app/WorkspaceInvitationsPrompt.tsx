// API-GUVENLIK-6: after signing in, an account that another workspace invited sees the invitations here and answers each
// with Kabul Et or Reddet (WorkspaceInvitationEndpoints). Accepting adds the membership; the workspace is chosen at the
// next sign-in, so the window offers to sign out. Loaded only when there is an invitation (Shell).
import type { ApiAnswer } from '../shared/api/api-answer'
import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../shared/api'
import { queryKeys } from '../shared/api/query-keys'
import { RrIcon, RrModal } from '../shared/ui/rr-components'
import { userRoleLabel } from '../features/settings/user-management'

type Invitation = ApiAnswer<'getAuthInvitations'>[number]
type Accepted = ApiAnswer<'postAuthInvitationsByIdAccept'>

const formatDay = (value: string) => new Date(value).toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' })

export function WorkspaceInvitationsPrompt({ invitations, onClose, onSignOut }: { invitations: Invitation[]; onClose: () => void; onSignOut: () => void }) {
  const client = useQueryClient()
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [accepted, setAccepted] = useState<Accepted | null>(null)

  async function answer(invitation: Invitation, accept: boolean) {
    setBusy(invitation.id); setError('')
    try {
      // An accepted invitation keeps this window open on its result; the list is read again when the window closes.
      if (accept) { setAccepted(await hubApi<Accepted>(`/auth/invitations/${invitation.id}/accept`, { method: 'POST' })); return }
      await hubApi(`/auth/invitations/${invitation.id}/decline`, { method: 'POST' })
      await client.invalidateQueries({ queryKey: queryKeys.myInvitations() })
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Davet yanıtlanamadı.')
      await client.invalidateQueries({ queryKey: queryKeys.myInvitations() })
    } finally {
      setBusy(null)
    }
  }

  function close() {
    void client.invalidateQueries({ queryKey: queryKeys.myInvitations() })
    onClose()
  }

  const foot = accepted
    ? <><span className="rr-note" /><button type="button" className="rr-btn" onClick={close}>Burada Kal</button><button type="button" className="rr-btn rr-primary" onClick={onSignOut}>Oturumu Kapat ve Geç</button></>
    : <><span className="rr-note" /><button type="button" className="rr-btn" disabled={busy !== null} onClick={close}>Sonra Karar Ver</button></>

  return <RrModal title={accepted ? 'Davet Kabul Edildi' : 'Çalışma Alanı Daveti'} desc={accepted ? undefined : 'Sizi bir çalışma alanına davet ettiler. Kabul edene kadar o çalışma alanının hiçbir bilgisini göremezsiniz.'} size="md" locked={busy !== null} onClose={close} foot={foot}>
    {accepted
      ? <div className="rr-callout" role="status"><RrIcon name="check" /><span><b>{accepted.workspaceName}</b> çalışma alanına katıldınız. Geçmek için oturumu kapatıp yeniden girin ve girişte bu çalışma alanını seçin.</span></div>
      : invitations.map(invitation => <section className="rr-dsec" key={invitation.id} aria-label={`${invitation.workspaceName} daveti`}>
        <header><div className="rr-h3">{invitation.workspaceName}</div></header>
        <dl className="rr-kv">
          <dt>Davet Eden</dt><dd>{invitation.invitedBy}</dd>
          <dt>Rol</dt><dd>{userRoleLabel(invitation.role)}</dd>
          <dt>Son Geçerlilik</dt><dd>{formatDay(invitation.expiresAt)}</dd>
          <dt>Yanıtınız</dt><dd>
            <button type="button" className="rr-btn" disabled={busy !== null} onClick={() => void answer(invitation, false)}>{busy === invitation.id ? 'Yanıtlanıyor…' : 'Reddet'}</button>
            <button type="button" className="rr-btn rr-primary" disabled={busy !== null} onClick={() => void answer(invitation, true)}>Kabul Et</button>
          </dd>
        </dl>
      </section>)}
    {error && <div className="rr-callout rr-danger" role="alert"><RrIcon name="alert" /><span>{error}</span></div>}
  </RrModal>
}
