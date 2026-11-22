import { useEffect, useRef, useState, type ReactNode } from 'react'
import { X, Plus, Trash2, BriefcaseBusiness, Wallet, ListTodo, NotebookPen, FileText, PanelsTopLeft, Folder, Star } from 'lucide-react'
import { icons, safeLink, validDate, validAmount, pathTo, type Data, type Entry, type Place } from './model'

const glyphs = { projects: BriefcaseBusiness, finance: Wallet, tasks: ListTodo,
  notes: NotebookPen, docs: FileText, services: PanelsTopLeft, folder: Folder, star: Star }
const iconNames = ['Проект', 'Кошелёк', 'Список дел', 'Блокнот', 'Документ', 'Сервисы', 'Папка', 'Звезда']

export function Glyph({ icon }: { icon: Place['icon'] }) {
  const Icon = glyphs[icon]
  return <Icon size={22} aria-hidden="true" />
}

export function Dialog({ title, close, children }: { title: string; close: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const dialog = ref.current!
    dialog.showModal()
    return () => dialog.close()
  }, [])
  return <dialog ref={ref} onCancel={event => { event.preventDefault(); close() }} aria-labelledby="dialog-title">
    <header className="dialog-header">
      <h2 id="dialog-title">{title}</h2>
      <button type="button" className="icon-button" aria-label="Закрыть" onClick={close}><X /></button>
    </header>
    {children}
  </dialog>
}

export function PlaceEditor({ place, close, save, error, busy }: {
  place: Place; close: () => void; save: (place: Place) => void; error: string; busy: boolean
}) {
  const [title, setTitle] = useState(place.title)
  const [icon, setIcon] = useState(place.icon)
  return <Dialog title={place.title ? 'Настройки' : place.parent === null ? 'Новый раздел' : 'Новая папка'} close={close}>
    <form onSubmit={event => {
      event.preventDefault()
      if (title.trim()) save({ ...place, title: title.trim(), icon })
    }}>
      <label>Название<input autoFocus required value={title} onChange={event => setTitle(event.target.value)} /></label>
      <fieldset>
        <legend>Иконка</legend>
        <div className="icon-options">{icons.map((item, index) =>
          <button key={item} type="button" className="icon-button" aria-label={iconNames[index]}
            aria-pressed={item === icon} onClick={() => setIcon(item)}><Glyph icon={item} /></button>
        )}</div>
      </fieldset>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="actions"><button type="button" onClick={close}>Отмена</button>
        <button className="primary" disabled={busy || !title.trim()}>Сохранить</button></div>
    </form>
  </Dialog>
}

export function EntryEditor({ entry, data, close, save, busy, error }: {
  entry: Entry; data: Data; close: () => void; save: (entry: Entry) => void; busy: boolean; error: string
}) {
  const [draft, setDraft] = useState(entry)
  const [links, setLinks] = useState(entry.links.join('\n'))
  const [validation, setValidation] = useState('')
  const update = (patch: Partial<Entry>) => setDraft(current => ({ ...current, ...patch }))
  function submit(event: React.FormEvent) {
    event.preventDefault()
    const parsedLinks = links.split('\n').map(link => link.trim()).filter(Boolean)
    const amount = draft.amount.trim().replace(',', '.')
    if (!parsedLinks.every(safeLink)) return setValidation('Укажите полные ссылки с http:// или https://.')
    if (!validAmount(amount)) return setValidation('Введите сумму с точностью до двух знаков после запятой.')
    if (!validDate(draft.date)) return setValidation('Укажите корректную дату.')
    setValidation('')
    if (draft.title.trim()) save({ ...draft, amount, title: draft.title.trim(), links: parsedLinks,
      checklist: draft.checklist.map(row => ({ ...row, text: row.text.trim() })).filter(row => row.text) })
  }
  return <Dialog title={entry.title ? 'Редактировать запись' : 'Новая запись'} close={close}>
    <form onSubmit={submit}>
      <label>Название<input autoFocus required value={draft.title} onChange={event => update({ title: event.target.value })} /></label>
      <label>Расположение<select required value={draft.parent} onChange={event => update({ parent: event.target.value })}>
        {data.places.map(place => <option key={place.id} value={place.id}>
          {pathTo(data, place.id).map(part => part.title).join(' / ')}
        </option>)}
      </select></label>
      <label>Текст<textarea rows={4} value={draft.text} onChange={event => update({ text: event.target.value })} /></label>
      <label>Ссылки, по одной на строку<textarea rows={2} value={links} onChange={event => setLinks(event.target.value)} /></label>
      <fieldset>
        <legend>Чек-лист</legend>
        {draft.checklist.map((row, index) => <div className="check-edit" key={index}>
          <input type="checkbox" aria-label={`Отметить пункт ${index + 1}`} checked={row.done}
            onChange={event => update({ checklist: draft.checklist.map((item, i) => i === index ? { ...item, done: event.target.checked } : item) })} />
          <input aria-label={`Пункт ${index + 1}`} value={row.text}
            onChange={event => update({ checklist: draft.checklist.map((item, i) => i === index ? { ...item, text: event.target.value } : item) })} />
          <button type="button" className="icon-button" aria-label={`Удалить пункт ${index + 1}`}
            onClick={() => update({ checklist: draft.checklist.filter((_, i) => i !== index) })}><Trash2 size={17} /></button>
        </div>)}
        <button type="button" onClick={() => update({ checklist: [...draft.checklist, { text: '', done: false }] })}><Plus size={18} /> Добавить пункт</button>
      </fieldset>
      <div className="form-columns">
        <label>Дата<input type="date" value={draft.date} onChange={event => update({ date: event.target.value })} /></label>
        <label>Сумма<input inputMode="decimal" placeholder="0,00" value={draft.amount}
          onChange={event => update({ amount: event.target.value })} /></label>
      </div>
      {entry.created && <p className="metadata">Создано {formatTime(entry.created)} · Изменено {formatTime(entry.updated)}</p>}
      {(validation || error) && <p className="error" role="alert">{validation || error}</p>}
      <div className="actions"><button type="button" onClick={close}>Отмена</button>
        <button className="primary" disabled={busy || !draft.title.trim()}>Сохранить</button></div>
    </form>
  </Dialog>
}

export const formatTime = (value: string) => new Date(value).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' })
