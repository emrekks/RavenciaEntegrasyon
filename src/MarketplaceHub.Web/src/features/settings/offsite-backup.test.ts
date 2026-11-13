// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { Encrypter } from 'age-encryption'
import { decryptBackup, decryptedFileName, generateBackupKeyPair, identityFromKeyFile } from './offsite-backup-crypto'
import { formatBackupSize, offsiteCopyPending, offsiteSetupDone, shortAgeKey, offsiteBackupDraftError, offsiteBackupSaveBody, offsiteBackupState, readGoogleCallback, type OffsiteBackupDraft, type OffsiteBackupView } from './offsite-backup'

const draft: OffsiteBackupDraft = {
  enabled: true, ageRecipient: 'age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p', folderMode: 'NAME', folderValue: '', keepDays: '14', keepMonths: '12',
  clientId: '123456789-abcdefghijklmnop.apps.googleusercontent.com', clientSecret: ''
}

describe('offsite backup rules', () => {
  it('shows the compact view only when the client, the key and the connection are all there', () => {
    const view = { clientId: '123456789-abcdefghijklmnop.apps.googleusercontent.com', clientSecretSaved: true, ageRecipient: draft.ageRecipient, connected: true } as OffsiteBackupView
    expect(offsiteSetupDone(view)).toBe(true)
    expect(offsiteSetupDone({ ...view, connected: false })).toBe(false)
    expect(offsiteSetupDone({ ...view, clientSecretSaved: false })).toBe(false)
    expect(offsiteSetupDone({ ...view, ageRecipient: null })).toBe(false)
    expect(offsiteSetupDone({ ...view, clientId: null })).toBe(false)
    expect(shortAgeKey(draft.ageRecipient)).toBe('age1ql3z7h…aqmcac8p'.replace('aqmcac8p', draft.ageRecipient.slice(-6)))
    expect(shortAgeKey(null)).toBe('')
  })

  it('keeps following a copy that was asked for or is running, and stops when it is finished', () => {
    const idle = { runningSince: null, requestedAt: null, lastAttemptAt: '2026-11-13T15:28:56Z' } as OffsiteBackupView
    expect(offsiteCopyPending(undefined, null)).toBe(false)
    expect(offsiteCopyPending(idle, null)).toBe(false)
    expect(offsiteCopyPending({ ...idle, runningSince: '2026-11-13T15:29:00Z' }, null)).toBe(true)
    expect(offsiteCopyPending({ ...idle, requestedAt: '2026-11-13T15:30:00+00:00' }, null)).toBe(true)
    expect(offsiteCopyPending({ ...idle, requestedAt: '2026-11-13T15:28:00Z' }, null)).toBe(false)
    // Asked a moment ago, the server has not shown a newer attempt yet.
    expect(offsiteCopyPending(idle, Date.now())).toBe(true)
    expect(offsiteCopyPending({ ...idle, lastAttemptAt: new Date(Date.now() + 60_000).toISOString() }, Date.now())).toBe(false)
  })

  it('refuses a private key, a short key and an incomplete client before asking the server', () => {
    expect(offsiteBackupDraftError(draft, true)).toBeNull()
    expect(offsiteBackupDraftError({ ...draft, ageRecipient: 'AGE-SECRET-KEY-1ABC' }, true)).toContain('ÖZEL')
    expect(offsiteBackupDraftError({ ...draft, ageRecipient: 'age1abc' }, true)).toContain('62 karakter')
    expect(offsiteBackupDraftError({ ...draft, clientId: 'nope' }, true)).toContain('googleusercontent')
    expect(offsiteBackupDraftError(draft, false)).toContain('gizli anahtar')
    expect(offsiteBackupDraftError({ ...draft, keepDays: '1' }, true)).toContain('3–365')
  })

  it('sends the default folder name and the secret only when it changed', () => {
    expect(offsiteBackupSaveBody({ ...draft, clientSecret: 'GOCSPX-secret-value' }, false)).toMatchObject({ folderValue: 'Ravencia Yedek', clientSecret: null, keepDays: 14 })
    expect(offsiteBackupSaveBody({ ...draft, clientSecret: ' GOCSPX-secret-value ' }, true).clientSecret).toBe('GOCSPX-secret-value')
  })

  it('names the state from what the server reports', () => {
    const view = { connected: true, authorizationInvalid: false, enabled: true, runningSince: null, consecutiveFailures: 0, lastSuccessAt: '2026-11-13T09:31:00Z' } as OffsiteBackupView
    expect(offsiteBackupState(view).label).toBe('Çalışıyor')
    expect(offsiteBackupState({ ...view, authorizationInvalid: true }).label).toBe('Yeniden Bağlanın')
    expect(offsiteBackupState({ ...view, connected: false }).label).toBe('Bağlı Değil')
    expect(offsiteBackupState({ ...view, consecutiveFailures: 2 }).tone).toBe('bad')
    expect(formatBackupSize(157286400)).toBe('150 MB')
    expect(readGoogleCallback('?state=s1&code=c1')).toEqual({ code: 'c1', state: 's1', error: null })
  })
})

describe('offsite backup keys and restore in the browser', () => {
  it('makes an X25519 key pair whose file holds the private key and whose public key encrypts a copy it opens again', async () => {
    const pair = await generateBackupKeyPair(new Date('2026-11-13T10:00:00Z'))
    expect(pair.recipient).toMatch(/^age1[02-9ac-hj-np-z]{58}$/u)
    expect(pair.file).toContain(`# public key: ${pair.recipient}`)
    expect(identityFromKeyFile(pair.file)).toBe(pair.identity)
    expect(identityFromKeyFile('# no key here\n')).toBeNull()

    const encrypter = new Encrypter()
    encrypter.addRecipient(pair.recipient)
    const plain = new TextEncoder().encode('20261113T085901Z/database.dump')
    const ciphertext = await encrypter.encrypt(plain)
    const stream = await decryptBackup(new Blob([new Uint8Array(ciphertext)]), pair.identity)
    expect(new Uint8Array(await new Response(stream).arrayBuffer())).toEqual(plain)

    const other = await generateBackupKeyPair()
    await expect(decryptBackup(new Blob([new Uint8Array(ciphertext)]), other.identity).then(result => new Response(result).arrayBuffer())).rejects.toBeTruthy()
    expect(decryptedFileName('20261113T085901Z.tar.age')).toBe('20261113T085901Z.tar')
  })
})
