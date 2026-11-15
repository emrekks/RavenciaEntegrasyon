import { useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiRequestError, hubApi, settingsIfMatch } from '../../../shared/api'
import { FieldHelp, LoadingState } from '../../../shared/components'
import { SettingsNotice, type SettingsNoticeState } from '../SettingsNotifications'
import { PanelSelect } from '../../../shared/PanelSelect'
import { queryKeys } from '../../../shared/api/query-keys'
import { formatSettingsDate, SettingsIcon, SettingsGlyph, SettingsPaneHead, SettingsBar } from '../settings-ui'

type AiSkillView = { key: string; title: string; description: string; bundleHash: string; uploaded: { skillId: string; version: string; uploadedAt: string; bundleHash: string; source?: AiSkillSource; fileName?: string | null } | null; upToDate: boolean }
// FILE: a .skill file the owner uploaded (it is the one generation uses); BUNDLED: the copy that comes with the panel.
type AiSkillSource = 'FILE' | 'BUNDLED'
const maxSkillFileBytes = 10 * 1024 * 1024
type AiSettings = { apiKeySaved: boolean; defaultModel: string; updatedAt: string | null; version: number; skills: AiSkillView[] }
type AiUsageEntry = { id: string; createdAt: string; feature: string; featureLabel: string; userName: string | null; subjectLabel: string | null; model: string; inputTokens: number | null; outputTokens: number | null; costUsd: number | null; status: 'SUCCEEDED' | 'FAILED' | 'CANCELLED'; error: string | null; durationMs: number }
const defaultAiModel = 'claude-sonnet-5-5'
// The Claude models the panel offers, with their list price per million tokens (input / output, USD).
const aiModelOptions = [
  { value: 'claude-sonnet-5-5', label: 'Claude Sonnet 5.5 — dengeli (2 $ / 10 $)' },
  { value: 'claude-opus-5-5', label: 'Claude Opus 5.5 — güçlü (4 $ / 20 $)' },
  { value: 'claude-haiku-5-5', label: 'Claude Haiku 5.5 — en ucuz (0,10 $ / 0,50 $)' }
]

function aiCost(value: number | null) {
  return value == null ? '—' : `$${value.toFixed(4)}`
}

function aiTokens(entry: AiUsageEntry) {
  return entry.inputTokens == null && entry.outputTokens == null ? '—' : `${(entry.inputTokens ?? 0).toLocaleString('tr-TR')} / ${(entry.outputTokens ?? 0).toLocaleString('tr-TR')}`
}

function fileSize(bytes: number) {
  return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} MB` : `${Math.max(1, Math.round(bytes / 1024)).toLocaleString('tr-TR')} KB`
}

// Which copy of a skill Claude runs, as the Beceriler row says it.
function aiSkillState(skill: AiSkillView) {
  const uploaded = skill.uploaded
  if (!uploaded) return 'Yüklenmedi'
  if (uploaded.source === 'FILE') return `Yüklendi · Sürüm ${uploaded.version} · ${uploaded.fileName ?? 'beceri dosyası'} · ${formatSettingsDate(uploaded.uploadedAt)}`
  return skill.upToDate ? `Yüklendi · Sürüm ${uploaded.version} · Panelle gelen sürüm · ${formatSettingsDate(uploaded.uploadedAt)}` : `Güncel Değil · Sürüm ${uploaded.version} · Panelle gelen eski sürüm`
}

// Settings → Yapay Zekâ: the Claude API key every AI feature of the panel uses (the size chart is the first), the default
// model, the Agent Skills the server uploads, and the log of every run. Owner and administrators only. The key is
// write-only like the alert app password: the server never returns it, the form shows "Kayıtlı" and "Değiştir".
export function AiSettingsSection() {
  const client = useQueryClient()
  const query = useQuery({ queryKey: queryKeys.aiSettings(), queryFn: ({ signal }) => hubApi<AiSettings>('/settings/ai', { signal }), retry: false })
  const forbidden = query.error instanceof ApiRequestError && query.error.status === 403
  const usage = useQuery({ queryKey: queryKeys.aiUsage(), queryFn: ({ signal }) => hubApi<{ items: AiUsageEntry[] }>('/settings/ai/usage?limit=50', { signal }), retry: false, enabled: query.isSuccess })
  const saved = query.data
  const [apiKey, setApiKey] = useState('')
  const [changingKey, setChangingKey] = useState(false)
  const [model, setModel] = useState(defaultAiModel)
  const [busy, setBusy] = useState<string | null>(null)
  const [notice, setNotice] = useState<SettingsNoticeState | null>(null)
  const [testResult, setTestResult] = useState<{ kind: 'done' | 'bad'; message: string } | null>(null)
  const [skillResult, setSkillResult] = useState<{ key: string; kind: 'done' | 'bad'; message: string } | null>(null)
  const [skillFile, setSkillFile] = useState<{ key: string; label: string } | null>(null)
  const skillInputs = useRef(new Map<string, HTMLInputElement>())
  const [loadedVersion, setLoadedVersion] = useState<number | null>(null)
  // The form starts from the saved settings, and again after a save (a new version); the key field always starts empty.
  if (saved && loadedVersion !== saved.version) {
    setLoadedVersion(saved.version)
    setApiKey('')
    setChangingKey(!saved.apiKeySaved)
    setModel(saved.defaultModel || defaultAiModel)
  }

  const dirty = !!saved && (model.trim() !== (saved.defaultModel || defaultAiModel) || apiKey.trim().length > 0)
  const configured = !!saved?.apiKeySaved

  async function save() {
    if (!saved) return
    setBusy('save'); setNotice(null); setTestResult(null)
    try {
      const next = await hubApi<AiSettings>('/settings/ai', { method: 'PUT', headers: settingsIfMatch(saved.version), body: JSON.stringify({ apiKey: changingKey && apiKey.trim() ? apiKey.trim() : null, defaultModel: model.trim() }) })
      setApiKey('')
      client.setQueryData(queryKeys.aiSettings(), next)
      // O9: a new key belongs to another workspace; the skills uploaded with the old one are forgotten.
      const skillsForgotten = saved.skills.some(skill => skill.uploaded) && !next.skills.some(skill => skill.uploaded)
      setNotice({ kind: 'success', message: skillsForgotten ? 'Yapay zekâ ayarları kaydedildi. Yeni anahtar için becerileri “Beceriyi Yükle” ile yeniden yükleyin; beden tablosu ancak ondan sonra oluşturulabilir.' : 'Yapay zekâ ayarları kaydedildi.' })
    } catch (reason) {
      setNotice({ kind: 'error', message: reason instanceof Error ? reason.message : 'Yapay zekâ ayarları kaydedilemedi.' })
    } finally { setBusy(null) }
  }
  async function testConnection() {
    setBusy('test'); setTestResult(null)
    try {
      const result = await hubApi<{ ok: boolean; model: string; message: string }>('/settings/ai/test', { method: 'POST' })
      setTestResult({ kind: 'done', message: result.message || `Bağlantı çalışıyor (${result.model}).` })
    } catch (reason) {
      setTestResult({ kind: 'bad', message: reason instanceof Error ? reason.message : 'Bağlantı sınanamadı.' })
    } finally {
      setBusy(null)
      void client.invalidateQueries({ queryKey: queryKeys.aiUsage() })
    }
  }
  // file: the owner's .skill / .zip file; none: the copy bundled with the panel (only offered while nothing is uploaded).
  async function uploadSkill(skill: AiSkillView, file?: File) {
    setSkillResult(null)
    if (file && !/\.(skill|zip)$/i.test(file.name)) { setSkillResult({ key: skill.key, kind: 'bad', message: `${file.name}: yalnız .skill ya da .zip dosyası yüklenebilir.` }); return }
    if (file && (file.size === 0 || file.size > maxSkillFileBytes)) { setSkillResult({ key: skill.key, kind: 'bad', message: `${file.name} (${fileSize(file.size)}): dosya boş olamaz ve en fazla 10 MB olabilir.` }); return }
    setBusy(`skill:${skill.key}`)
    setSkillFile(file ? { key: skill.key, label: `${file.name} · ${fileSize(file.size)}` } : null)
    try {
      let body: FormData | undefined
      if (file) { body = new FormData(); body.append('file', file, file.name) }
      const next = await hubApi<AiSettings>(`/settings/ai/skills/${encodeURIComponent(skill.key)}/upload`, { method: 'POST', body })
      client.setQueryData(queryKeys.aiSettings(), next)
      const updated = next.skills.find(item => item.key === skill.key)
      setSkillResult({ key: skill.key, kind: 'done', message: updated ? `${aiSkillState(updated)}.` : 'Beceri yüklendi.' })
    } catch (reason) {
      setSkillResult({ key: skill.key, kind: 'bad', message: reason instanceof Error ? reason.message : 'Beceri yüklenemedi.' })
    } finally {
      setBusy(null)
      setSkillFile(null)
      void client.invalidateQueries({ queryKey: queryKeys.aiUsage() })
    }
  }

  const bar = <SettingsBar title="Yapay Zekâ" dirty={dirty} copy={dirty ? 'Kaydedilmemiş değişiklikler var' : saved?.updatedAt ? `Son kayıt ${formatSettingsDate(saved.updatedAt)}` : 'Henüz kaydedilmedi'}>
    <button type="button" className="ss-btn is-primary" disabled={!dirty || busy !== null || !model.trim()} onClick={() => void save()}><SettingsIcon name="save" />{busy === 'save' ? 'Kaydediliyor…' : 'Ayarları Kaydet'}</button>
  </SettingsBar>

  return <>
    {notice && <SettingsNotice {...notice} />}
    <SettingsPaneHead section="ai" description="Claude API anahtarı ve beceriler; panelin yapay zekâ özellikleri (ilk olarak beden tablosu) bunları kullanır." />
    <div className="ss-body">
      {query.isPending ? <LoadingState>Yapay zekâ ayarları yükleniyor…</LoadingState> : forbidden ? <div className="ss-callout" role="alert"><SettingsIcon name="lock" /><span>Yapay zekâ ayarlarını yalnız hesap sahibi ve yöneticiler görür ve değiştirir.</span></div> : query.isError || !saved ? <div className="ss-callout is-danger" role="alert"><SettingsIcon name="alert" /><span>Yapay zekâ ayarları alınamadı.</span></div> : <>
        <div className="ss-pair">
          <div className="ss-fld"><small>Claude API Anahtarı<FieldHelp label="Claude API Anahtarı">Anahtarı Claude Console’da (platform.claude.com) oluşturup yapıştırın. Şifreli saklanır; ekranda ve kayıtlarda görünmez.</FieldHelp></small>{saved.apiKeySaved && !changingKey
            ? <div className="ss-in ss-secret"><span className="ss-secret-dots" aria-hidden="true">••••••••••••••••</span><span className="ss-secret-state"><SettingsGlyph state="done" />Kayıtlı</span><button type="button" className="ss-btn is-xs" onClick={() => setChangingKey(true)}>Değiştir</button></div>
            : <div className="ss-alert-row ss-secret-edit"><input className="ss-in" type="password" autoComplete="new-password" spellCheck={false} aria-label="Claude API Anahtarı" value={apiKey} maxLength={300} placeholder="sk-ant-…" onChange={event => setApiKey(event.target.value)} />{saved.apiKeySaved && <button type="button" className="ss-btn is-xs" onClick={() => { setApiKey(''); setChangingKey(false) }}>Vazgeç</button>}</div>}
          </div>
          <label className="ss-fld"><small>Varsayılan Model</small><PanelSelect ariaLabel="Varsayılan Model" value={model} onChange={setModel} options={[...aiModelOptions.map(option => ({ value: option.value, label: option.label })), ...(aiModelOptions.some(option => option.value === model) ? [] : [{ value: model, label: model }])]} /></label>
        </div>
        <div className="ss-alert-row">
          <button type="button" className="ss-btn" disabled={!configured || dirty || busy !== null} onClick={() => void testConnection()}><SettingsIcon name="sparkle" />{busy === 'test' ? 'Sınanıyor…' : 'Bağlantıyı Sına'}</button>
          {testResult ? <span role="status" className="ss-help"><SettingsGlyph state={testResult.kind} />{testResult.message}</span>
            : <span className="ss-help">{dirty ? 'Sınamak için önce değişiklikleri kaydedin.' : configured ? '' : 'Sınamak için önce API anahtarını kaydedin.'}</span>}
        </div>
        <div className="ss-fld" role="group" aria-label="Beceriler"><small>Beceriler</small>
          <div className="ss-ai-skills">{saved.skills.map(skill => {
            const blocked = !configured || dirty || busy !== null
            return <div key={skill.key} className="ss-ai-skill">
              <span className="ss-ai-skill-copy"><b>{skill.title}</b><small>{skill.description}</small><span className="ss-tag"><SettingsGlyph state={!skill.uploaded ? 'none' : skill.upToDate ? 'done' : 'pending'} />{aiSkillState(skill)}</span></span>
              <input ref={element => { if (element) skillInputs.current.set(skill.key, element); else skillInputs.current.delete(skill.key) }} type="file" accept=".skill,.zip" hidden aria-label={`${skill.title} beceri dosyası`}
                onChange={event => { const file = event.currentTarget.files?.[0]; event.currentTarget.value = ''; if (file) void uploadSkill(skill, file) }} />
              <span className="ss-alert-row">
                <button type="button" className="ss-btn" disabled={blocked} onClick={() => skillInputs.current.get(skill.key)?.click()}><SettingsIcon name="plus" />{busy === `skill:${skill.key}` ? 'Yükleniyor…' : skill.uploaded ? 'Beceriyi Güncelle' : 'Beceriyi Yükle'}</button>
                {!skill.uploaded && <button type="button" className="ss-btn is-xs" disabled={blocked} onClick={() => void uploadSkill(skill)}>Panelle Gelen Sürümü Yükle</button>}
              </span>
              {skillFile?.key === skill.key && <span role="status" className="ss-help ss-ai-skill-result">{skillFile.label} yükleniyor…</span>}
              {skillResult?.key === skill.key && <span role="status" className="ss-help ss-ai-skill-result"><SettingsGlyph state={skillResult.kind} />{skillResult.message}</span>}
            </div>
          })}{saved.skills.length === 0 && <span className="ss-help">Yüklenecek beceri yok.</span>}</div>
        </div>
        <div className="ss-fld" role="group" aria-label="Kullanım Kaydı"><small>Kullanım Kaydı</small>
          {usage.isPending ? <LoadingState>Kullanım kaydı yükleniyor…</LoadingState> : usage.isError ? <span className="ss-help">Kullanım kaydı alınamadı.</span> : !usage.data?.items.length ? <span className="ss-help">Henüz yapay zekâ kullanımı yok.</span> : <div className="ss-ai-usage-wrap"><table className="ss-ai-usage">
            <thead><tr><th scope="col">Tarih</th><th scope="col">Özellik</th><th scope="col">Kişi</th><th scope="col">Konu</th><th scope="col">Token (Girdi / Çıktı)</th><th scope="col">Maliyet</th><th scope="col">Durum</th></tr></thead>
            <tbody>{usage.data.items.map(entry => <tr key={entry.id}>
              <td>{formatSettingsDate(entry.createdAt)}</td>
              <td>{entry.featureLabel || entry.feature}</td>
              <td>{entry.userName ?? '—'}</td>
              <td>{entry.subjectLabel ?? '—'}</td>
              <td className="is-num">{aiTokens(entry)}</td>
              <td className="is-num">{aiCost(entry.costUsd)}</td>
              <td title={entry.error ?? undefined}><span className="ss-tag"><SettingsGlyph state={entry.status === 'SUCCEEDED' ? 'done' : entry.status === 'CANCELLED' ? 'none' : 'bad'} />{entry.status === 'SUCCEEDED' ? 'Başarılı' : entry.status === 'CANCELLED' ? 'İptal Edildi' : 'Başarısız'}</span>{entry.error ? <small className="ss-ai-usage-error">{entry.error}</small> : null}</td>
            </tr>)}</tbody>
          </table></div>}
        </div>
      </>}
    </div>
    {saved && !forbidden ? bar : null}
  </>
}
