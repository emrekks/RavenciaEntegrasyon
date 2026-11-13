// The owner's age key pair and the restore of a copy, both in the browser so no extra program is needed (owner, 13 Nov):
// the private key is only ever handed to the person as a file and the server only receives the public key; a copy
// downloaded from Drive is decrypted here and saved as the original .tar. typage (npm "age-encryption", BSD-3-Clause,
// by the author of age) is loaded only when one of these is used.

const loadAge = () => import('age-encryption')

export const backupKeyFileName = 'ravencia-yedek-anahtari.txt'

export type BackupKeyPair = { identity: string; recipient: string; file: string }

/** A new X25519 key pair (the server's age 1.1 encrypts to X25519 recipients) in age-keygen's file layout. */
export async function generateBackupKeyPair(now = new Date()): Promise<BackupKeyPair> {
  const age = await loadAge()
  const identity = await age.generateX25519Identity()
  const recipient = await age.identityToRecipient(identity)
  const file = `# created: ${now.toISOString().replace(/\.\d{3}Z$/u, 'Z')}\n# public key: ${recipient}\n# Ravencia Google Drive yedeğinin ÖZEL anahtarı. Kaybolursa yedekler açılamaz; kimseyle paylaşmayın.\n${identity}\n`
  return { identity, recipient, file }
}

/** The private key in a key file (age-keygen's or this panel's): its AGE-SECRET-KEY-1 line; null when there is none. */
export function identityFromKeyFile(text: string): string | null {
  const line = text.split(/\r?\n/u).map(value => value.trim()).find(value => /^AGE-SECRET-KEY-1[0-9A-Z]+$/u.test(value))
  return line ?? null
}

/** The .tar inside a copy: "20261113T085901Z.tar.age" → "20261113T085901Z.tar". */
export function decryptedFileName(name: string) {
  const base = name.replace(/\.age$/iu, '')
  return base === name ? `${name}.tar` : base
}

/** Decrypts the copy as it is read; a wrong key fails before any output. */
export async function decryptBackup(file: Blob, identity: string): Promise<ReadableStream<Uint8Array>> {
  const age = await loadAge()
  const decrypter = new age.Decrypter()
  decrypter.addIdentity(identity)
  return decrypter.decrypt(file.stream())
}

type SaveFilePicker = (options: { suggestedName: string }) => Promise<{ createWritable: () => Promise<WritableStream<Uint8Array>> }>

/** Writes the stream to a file the person picks (streamed where the browser can, else through memory). */
export async function saveStream(stream: ReadableStream<Uint8Array>, name: string) {
  const picker = (window as unknown as { showSaveFilePicker?: SaveFilePicker }).showSaveFilePicker
  if (picker) {
    const handle = await picker({ suggestedName: name })
    await stream.pipeTo(await handle.createWritable())
    return
  }
  const blob = await new Response(stream).blob()
  downloadBlob(blob, name)
}

export function downloadText(text: string, name: string) {
  downloadBlob(new Blob([text], { type: 'text/plain;charset=utf-8' }), name)
}

function downloadBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  document.body.append(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}
