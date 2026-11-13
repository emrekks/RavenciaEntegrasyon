// Sistem Ayarları › Google Drive Yedeği (OffsiteBackupEndpoints): what the server says, and the small rules the section
// shows before it asks the server. The server checks everything again; the client secret and the Google token never
// come back to the browser.

export type OffsiteBackupView = {
  enabled: boolean
  ageRecipient: string | null
  folderMode: 'NAME' | 'ID'
  folderValue: string
  keepDays: number
  keepMonths: number
  clientId: string | null
  clientSecretSaved: boolean
  redirectUri: string
  scopes: string
  connected: boolean
  account: string | null
  connectedAt: string | null
  authorizationInvalid: boolean
  authorizationError: string | null
  folderId: string | null
  lastAttemptAt: string | null
  lastSuccessAt: string | null
  lastSet: string | null
  lastSize: number | null
  lastError: string | null
  lastErrorAt: string | null
  consecutiveFailures: number
  requestedAt: string | null
  runningSince: string | null
  nextRunAt: string | null
  remoteCopies: number | null
  updatedAt: string | null
  version: number
}

export type OffsiteBackupDraft = {
  enabled: boolean
  ageRecipient: string
  folderMode: 'NAME' | 'ID'
  folderValue: string
  keepDays: string
  keepMonths: string
  clientId: string
  clientSecret: string
}

export const offsiteBackupCallbackPath = '/settings/google-drive-callback'
export const offsiteBackupDefaultFolder = 'Ravencia Yedek'
const agePattern = /^age1[02-9ac-hj-np-z]{58}$/u
const clientIdPattern = /^[0-9]{6,30}-[a-z0-9]{10,64}\.apps\.googleusercontent\.com$/u

export function offsiteBackupDraft(view: OffsiteBackupView): OffsiteBackupDraft {
  return {
    enabled: view.enabled,
    ageRecipient: view.ageRecipient ?? '',
    folderMode: view.folderMode,
    folderValue: view.folderValue,
    keepDays: String(view.keepDays),
    keepMonths: String(view.keepMonths),
    clientId: view.clientId ?? '',
    clientSecret: ''
  }
}

export function offsiteBackupDirty(view: OffsiteBackupView, draft: OffsiteBackupDraft) {
  const saved = offsiteBackupDraft(view)
  return draft.enabled !== saved.enabled || draft.ageRecipient.trim() !== saved.ageRecipient || draft.folderMode !== saved.folderMode
    || draft.folderValue.trim() !== saved.folderValue || Number(draft.keepDays) !== view.keepDays || Number(draft.keepMonths) !== view.keepMonths
    || draft.clientId.trim() !== saved.clientId || draft.clientSecret.trim().length > 0
}

/** The person's message for what cannot be saved as it is; null when the server may check it. */
export function offsiteBackupDraftError(draft: OffsiteBackupDraft, secretSaved: boolean): string | null {
  const key = draft.ageRecipient.trim()
  if (/^AGE-SECRET-KEY-/iu.test(key)) return 'Bu bir age ÖZEL anahtarı: buraya yalnız age1 ile başlayan açık anahtar girilir. Özel anahtarı sunucuya hiç yazmayın.'
  if (key && !agePattern.test(key.toLowerCase())) return 'age açık anahtarı age1 ile başlayan 62 karakterdir; satırın tamamını yapıştırın.'
  if (draft.enabled && !key) return 'Yedeği açmak için age açık anahtarını girin.'
  const clientId = draft.clientId.trim()
  if (clientId && !clientIdPattern.test(clientId)) return 'İstemci kimliği …apps.googleusercontent.com ile biter.'
  if (draft.enabled && !clientId) return 'Yedeği açmak için Google OAuth istemcisini girin.'
  if (clientId && !secretSaved && !draft.clientSecret.trim()) return 'İstemci gizli anahtarını girin.'
  const days = Number(draft.keepDays)
  if (!Number.isInteger(days) || days < 3 || days > 365) return 'Günlük saklama 3–365 gün olmalı.'
  const months = Number(draft.keepMonths)
  if (!Number.isInteger(months) || months < 0 || months > 120) return 'Aylık saklama 0–120 ay olmalı.'
  if (draft.folderMode === 'ID' && !draft.folderValue.trim()) return 'Klasör ID\'sini girin.'
  return null
}

export function offsiteBackupSaveBody(draft: OffsiteBackupDraft, secretChanged: boolean) {
  return {
    enabled: draft.enabled,
    ageRecipient: draft.ageRecipient.trim(),
    folderMode: draft.folderMode,
    folderValue: draft.folderMode === 'NAME' ? (draft.folderValue.trim() || offsiteBackupDefaultFolder) : draft.folderValue.trim(),
    keepDays: Number(draft.keepDays),
    keepMonths: Number(draft.keepMonths),
    clientId: draft.clientId.trim(),
    clientSecret: secretChanged ? draft.clientSecret.trim() : null
  }
}

export type OffsiteBackupState = { tone: 'done' | 'pending' | 'none' | 'bad'; label: string }

/** The one-word state the section list and the status line show. */
export function offsiteBackupState(view: OffsiteBackupView): OffsiteBackupState {
  if (view.connected && view.authorizationInvalid) return { tone: 'bad', label: 'Yeniden Bağlanın' }
  if (!view.connected) return { tone: 'none', label: 'Bağlı Değil' }
  if (!view.enabled) return { tone: 'none', label: 'Kapalı' }
  if (view.runningSince) return { tone: 'pending', label: 'Gönderiliyor' }
  if (view.consecutiveFailures > 0) return { tone: 'bad', label: 'Gönderilemedi' }
  if (view.lastSuccessAt) return { tone: 'done', label: 'Çalışıyor' }
  return { tone: 'pending', label: 'İlk Gönderim Bekleniyor' }
}

export function formatBackupSize(bytes: number | null | undefined) {
  if (bytes == null || !Number.isFinite(bytes) || bytes <= 0) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++ }
  return `${value.toLocaleString('tr-TR', { maximumFractionDigits: unit >= 2 ? 1 : 0 })} ${units[unit]}`
}

/** What Google put on the callback address: a code and state, or an error. */
export function readGoogleCallback(search: string): { code: string | null; state: string | null; error: string | null } {
  const params = new URLSearchParams(search)
  return { code: params.get('code'), state: params.get('state'), error: params.get('error') }
}

// The owner's one-time Google Cloud set-up, as the section lists it (docs/CI-CD-DEPLOY.md "Sunucu dışı yedek").
export const googleCloudSteps: string[] = [
  'console.cloud.google.com adresinde yeni bir proje açın (ör. “Ravencia Yedek”).',
  'API’ler ve Hizmetler › Kitaplık: “Google Drive API”yi etkinleştirin.',
  'OAuth izin ekranı: kullanıcı türü “Harici (External)”, uygulama adı ve e-postanızı girin; kapsam olarak openid, email ve …/auth/drive.file ekleyin.',
  'Test kullanıcısı olarak kendi Gmail hesabınızı ekleyin, sonra “Uygulamayı Yayınla” ile durumu “Üretimde (In production)” yapın. Test durumunda Google bağlantıyı 7 günde bir düşürür.',
  'Kimlik Bilgileri › Kimlik bilgisi oluştur › OAuth istemci kimliği: uygulama türü “Web uygulaması”.',
  'Yetkili yönlendirme URI’leri alanına aşağıdaki Yönlendirme Adresi’ni olduğu gibi ekleyin.',
  'Oluşan İstemci Kimliği ve İstemci Gizli Anahtarı’nı buraya girip kaydedin, sonra “Google ile Bağlan”a basın.'
]
