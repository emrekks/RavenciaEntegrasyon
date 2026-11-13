import { useEffect, useRef } from 'react'
import { useLocation, useNavigate } from 'react-router'
import { useQueryClient } from '@tanstack/react-query'
import { hubApi } from '../../shared/api'
import { LoadingState } from '../../shared/components'
import { readGoogleCallback, type OffsiteBackupView } from './offsite-backup'

// Google sends the person back here (the redirect URI registered in the owner's OAuth client) after "Google ile Bağlan".
// The code and state go to the server with this session and its CSRF token, which checks that the state is the one this
// user started, unexpired and unused; then the section opens with the outcome. The code never stays in the address bar.
export function GoogleDriveCallbackPage() {
  const location = useLocation()
  const navigate = useNavigate()
  const client = useQueryClient()
  const sent = useRef(false)
  useEffect(() => {
    if (sent.current) return
    sent.current = true
    const { code, state, error } = readGoogleCallback(location.search)
    const done = (kind: 'success' | 'error', message: string) => navigate('/settings?tab=offsite', { replace: true, state: { offsiteNotice: { kind, message } } })
    hubApi<OffsiteBackupView>('/settings/offsite-backup/google/complete', { method: 'POST', body: JSON.stringify({ code, state, error }) })
      .then(view => {
        client.setQueryData(['offsite-backup'], view)
        done('success', `Google Drive bağlandı${view.account ? `: ${view.account}` : ''}. “Bağlantıyı Test Et” ile deneyebilirsiniz.`)
      })
      .catch((reason: unknown) => done('error', reason instanceof Error ? reason.message : 'Google Drive bağlanamadı.'))
  }, [client, location.search, navigate])
  return <section className="content"><LoadingState>Google Drive bağlanıyor…</LoadingState></section>
}
