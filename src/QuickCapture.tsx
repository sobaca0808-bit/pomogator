import { useState } from 'react'
import { Inbox, Paperclip, Plus, X } from 'lucide-react'
import { emptyCapture, inboxTitle, type CaptureDraft } from './capture'
import { Dialog } from './editors'
import { pathTo, type Data } from './model'

export function useCaptureDraft(key: string) {
  const [draft, update] = useState<CaptureDraft>(() => {
    try {
      const value = JSON.parse(sessionStorage.getItem(key) ?? 'null')
      if (value && typeof value.text === 'string' && typeof value.parent === 'string' &&
          (value.kind === 'task' || value.kind === 'note')) {
        return { text: value.text, parent: value.parent, kind: value.kind }
      }
    } catch { /* The draft still works in memory if browser storage is blocked. */ }
    return emptyCapture
  })
  function change(value: CaptureDraft) {
    update(value)
    try {
      if (value === emptyCapture) sessionStorage.removeItem(key)
      else sessionStorage.setItem(key, JSON.stringify({ text: value.text, parent: value.parent, kind: value.kind }))
    } catch { /* Keep the in-memory draft. */ }
  }
  return { draft, change }
}

export function CaptureActions({ count, open, capture, active }: {
  count: number; open: () => void; capture: () => void; active: boolean
}) {
  return <div className="capture-actions actions">
    <button className="primary capture-trigger" type="button" title="Быстрый захват" aria-label="Быстрый захват" onClick={capture}>
      <Plus size={21} aria-hidden="true" />
    </button>
    <button className="glass-surface inbox-trigger" type="button" onClick={open} aria-current={active ? 'page' : undefined}>
      <Inbox size={19} aria-hidden="true" />{inboxTitle}<span className="inbox-count">{count}</span>
    </button>
  </div>
}

export function QuickCapture({ draft, change, data, inboxId, busy, error, save, close }: {
  draft: CaptureDraft; change: (value: CaptureDraft) => void; data: Data; inboxId: string; busy: boolean;
  error: string; save: () => void; close: () => void
}) {
  return <Dialog className="capture-dialog" title="Быстрый захват" close={close}>
    <form onSubmit={event => { event.preventDefault(); save() }}>
      <label>Текст<textarea aria-label="Текст" autoFocus rows={4} value={draft.text} disabled={busy}
        onChange={event => change({ ...draft, text: event.target.value })} /></label>
      <div className="form-columns">
        <label>Тип записи<select value={draft.kind} disabled={busy} onChange={event =>
          change({ ...draft, kind: event.target.value === 'task' ? 'task' : 'note' })}>
          <option value="note">Заметка</option><option value="task">Задача</option>
        </select></label>
        <label>Куда сохранить<select value={draft.parent} disabled={busy} onChange={event =>
          change({ ...draft, parent: event.target.value })}>
          <option value="">Входящие</option>
          {draft.parent && !data.places.some(place => place.id === draft.parent) &&
            <option value={draft.parent}>Папка недоступна</option>}
          {data.places.filter(place => place.id !== inboxId).map(place =>
            <option key={place.id} value={place.id}>{pathTo(data, place.id).map(item => item.title).join(' / ')}</option>)}
        </select></label>
      </div>
      <label className="capture-file"><span><Paperclip size={16} aria-hidden="true" /> Фото или файл</span>
        <input type="file" disabled={busy} onChange={event => {
          const file = event.target.files?.[0]
          if (file) change({ ...draft, file })
          event.target.value = ''
        }} />
      </label>
      {draft.file && <div className="capture-attachment"><span>{draft.file.name}</span>
        <button type="button" className="icon-button" aria-label="Убрать вложение" disabled={busy}
          onClick={() => change({ ...draft, file: undefined })}><X size={18} /></button>
      </div>}
      {error && <p className="error" role="alert">{error}</p>}
      <div className="editor-footer">
        <span className="muted" role="status">{busy ? 'Сохранение…' : draft.text ? 'Черновик' : ''}</span>
        <button className="primary" type="submit" disabled={busy || !draft.text.trim() && !draft.file}>Сохранить запись</button>
      </div>
    </form>
  </Dialog>
}
