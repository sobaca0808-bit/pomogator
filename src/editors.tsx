import { useEffect, useRef, useState, type ReactNode } from 'react'
import { X, BriefcaseBusiness, Wallet, ListTodo, NotebookPen, FileText, PanelsTopLeft, Folder, Star } from 'lucide-react'
import { icons, type Place } from './model'

const glyphs = { projects: BriefcaseBusiness, finance: Wallet, tasks: ListTodo,
  notes: NotebookPen, docs: FileText, services: PanelsTopLeft, folder: Folder, star: Star }
const iconNames = ['Проект', 'Кошелёк', 'Список дел', 'Блокнот', 'Документ', 'Сервисы', 'Папка', 'Звезда']

export function Glyph({ icon }: { icon: Place['icon'] }) {
  const Icon = glyphs[icon]
  return <Icon size={22} aria-hidden="true" />
}

export function Dialog({ title, close, children, className }: { title: string; close: () => void; children: ReactNode; className?: string }) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const dialog = ref.current!
    dialog.showModal()
    return () => dialog.close()
  }, [])
  return <dialog className={className} ref={ref} onCancel={event => { event.preventDefault(); close() }} aria-labelledby="dialog-title">
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

export const formatTime = (value: string) => new Date(value).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' })
