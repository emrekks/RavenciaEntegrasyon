import type { ReactNode } from 'react'
import { UiIcon } from './UiIcon'
import './data-reset-ledger.css'

export type DataResetOption<S extends string = string> = {
  scope: S
  label: string
  description: string
  // The noun the count is shown with (sipariş, dosya…).
  unit: string
  // The scopes the server removes with this one (an order takes its returns and invoices).
  implies?: S[]
}
export type DataResetGroup<S extends string = string> = { label: string; description: string; options: DataResetOption<S>[] }
export type DataResetNote = { tone: 'warn' | 'ok'; text: ReactNode }

type Props<S extends string> = {
  groups: DataResetGroup<S>[]
  // Undefined while the counts load; a scope missing from a loaded map has no count to show.
  counts?: Partial<Record<S, number>>
  countsFailed?: boolean
  picked: S[]
  onToggle: (scopes: S[], checked: boolean) => void
  notes: DataResetNote[]
  confirmWord: string
  confirmation: string
  onConfirmation: (value: string) => void
  busy: boolean
  onConfirm: () => void
  onCancel?: () => void
  idPrefix: string
}

const number = (value: number) => value.toLocaleString('tr-TR')

// Özetli Liste (the reset design picked on 26 Oct 2026): every data type in one list with what it holds, and beside it
// a receipt of what will go, its total, what stays and the typed confirmation.
export function DataResetLedger<S extends string>({ groups, counts, countsFailed = false, picked, onToggle, notes, confirmWord, confirmation, onConfirmation, busy, onConfirm, onCancel, idPrefix }: Props<S>) {
  const options = groups.flatMap(group => group.options)
  const impliedBy = (scope: S) => options.find(option => picked.includes(option.scope) && option.implies?.includes(scope))
  const going = options.filter(option => picked.includes(option.scope) || impliedBy(option.scope))
  const goingScopes = new Set(going.map(option => option.scope))
  const known = going.every(option => counts?.[option.scope] !== undefined)
  const total = going.reduce((sum, option) => sum + (counts?.[option.scope] ?? 0), 0)
  const confirmed = confirmation.trim() === confirmWord
  const countText = (option: DataResetOption<S>) => {
    const value = counts?.[option.scope]
    if (value !== undefined) return <><strong className="num">{number(value)}</strong>{option.unit}</>
    return <span className="drl-count-none">{countsFailed ? 'Sayı alınamadı' : counts ? '' : 'Sayılıyor…'}</span>
  }
  return <div className="drl">
    <div className="drl-list" role="group" aria-label="Sıfırlanacak veriler">
      {groups.map(group => {
        const all = group.options.every(option => goingScopes.has(option.scope))
        return <section className="drl-group" key={group.label}>
          <header><span><b>{group.label}</b><small>{group.description}</small></span>
            <button type="button" className="drl-all" disabled={busy} onClick={() => onToggle(group.options.map(option => option.scope), !all)}>{all ? 'Seçimi Kaldır' : 'Tümünü Seç'}</button></header>
          {group.options.map(option => {
            const by = picked.includes(option.scope) ? undefined : impliedBy(option.scope)
            const on = goingScopes.has(option.scope)
            return <label key={option.scope} className={`drl-row${on ? ' is-on' : ''}${by ? ' is-locked' : ''}`}>
              <input type="checkbox" checked={on} disabled={busy || Boolean(by)} onChange={event => onToggle([option.scope], event.target.checked)} />
              <i aria-hidden="true">{on && <UiIcon name="check" size={13} />}</i>
              <span className="drl-text"><b>{option.label}</b><small>{by && <em>{by.label} ile birlikte silinir. </em>}{option.description}</small></span>
              <span className="drl-count">{countText(option)}</span>
            </label>
          })}
        </section>
      })}
    </div>
    <aside className="drl-receipt" aria-label="Silinecekler">
      <h3>Silinecekler</h3>
      <div className="drl-lines" role="status">{going.length
        ? going.map(option => <div key={option.scope}><span>{option.label}</span><span className="num">{counts?.[option.scope] !== undefined ? `${number(counts[option.scope]!)} ${option.unit}` : ''}</span></div>)
        : <p>Soldan silinecek veri türünü seçin.</p>}</div>
      <div className="drl-total"><span>Toplam</span><span className="num">{going.length && known ? `${number(total)} kayıt` : going.length ? '' : '0 kayıt'}</span></div>
      <div className="drl-notes">{notes.map((note, index) => <p key={index} className={note.tone === 'warn' ? 'is-warn' : undefined}><UiIcon name={note.tone === 'warn' ? 'alert' : 'check'} size={15} /><span>{note.text}</span></p>)}</div>
      <div className="drl-confirm">
        <label htmlFor={`${idPrefix}-confirm`}>Onaylamak için <b>{confirmWord}</b> yazın</label>
        <span className={`drl-field${confirmed ? ' is-ok' : ''}`}><input id={`${idPrefix}-confirm`} value={confirmation} onChange={event => onConfirmation(event.target.value)} disabled={busy} autoComplete="off" placeholder={confirmWord} />{confirmed && <UiIcon name="check" size={16} />}</span>
        <button type="button" className="destructive drl-go" onClick={onConfirm} disabled={!picked.length || !confirmed || busy}>{busy ? 'Sıfırlanıyor…' : going.length ? `${going.length} Veri Türünü Sıfırla` : 'Seçilen Verileri Sıfırla'}</button>
        {onCancel && <button type="button" className="secondary drl-cancel" onClick={onCancel} disabled={busy}>Vazgeç</button>}
      </div>
    </aside>
  </div>
}
