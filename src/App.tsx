import { useEffect, useRef, useState } from 'react'
import { ArrowDown, ArrowUp, ChevronRight, Download, FileText, FolderOpen, LayoutGrid, Pencil, Plus, Search, Trash2, Upload, X } from 'lucide-react'
import { Dialog, EntryEditor, Glyph, PlaceEditor, formatTime } from './editors'
import { contents, deleteEntry, deletePlace, exportData, importData, orderPlace, pathTo, saveEntry, savePlace, search,
  type Change, type Data, type Entry, type Place } from './model'
import { openStorage, transact } from './storage'

type Modal = { kind: 'place'; value: Place; editing: boolean } |
  { kind: 'entry'; value: Entry; editing: boolean } |
  { kind: 'confirm'; title: string; message: string; action: Change; done?: () => void }

function EntryCard({ entry, edit, remove, toggle }: {
  entry: Entry; edit: () => void; remove: () => void; toggle: (index: number) => void
}) {
  return <article className="entry">
    <div className="entry-heading"><h3>{entry.title}</h3><div className="tools">
      <button className="icon-button" aria-label={`Редактировать запись «${entry.title}»`} onClick={edit}><Pencil size={17} /></button>
      <button className="icon-button" aria-label={`Удалить запись «${entry.title}»`} onClick={remove}><Trash2 size={17} /></button>
    </div></div>
    {entry.text && <p className="entry-text">{entry.text}</p>}
    {entry.links.length > 0 && <ul className="entry-links">{entry.links.map((link, index) =>
      <li key={index}><a href={link} target="_blank" rel="noreferrer">{link}</a></li>)}</ul>}
    {entry.checklist.map((item, index) => <label className="check-item" key={index}>
      <input type="checkbox" checked={item.done} onChange={() => toggle(index)} /><span>{item.text}</span>
    </label>)}
    {(entry.date || entry.amount) && <p className="detail">
      {[entry.date && `Дата: ${entry.date.split('-').reverse().join('.')}`, entry.amount && `Сумма: ${entry.amount.replace('.', ',')}`].filter(Boolean).join(' · ')}
    </p>}
    <p className="metadata">Создано {formatTime(entry.created)} · Изменено {formatTime(entry.updated)}</p>
  </article>
}

export function App() {
  const [data, setData] = useState<Data>()
  const [current, setCurrent] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [modal, setModal] = useState<Modal>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [modalError, setModalError] = useState('')
  const [notice, setNotice] = useState('')
  const database = useRef<IDBDatabase>(null)
  const channel = useRef<BroadcastChannel>(null)
  const input = useRef<HTMLInputElement>(null)
  const searchInput = useRef<HTMLInputElement>(null)
  const writing = useRef(false)

  useEffect(() => {
    let active = true
    const report = (cause: unknown) => {
      if (active) setError(`Ошибка чтения базы. ${cause instanceof Error ? cause.message : ''}`)
    }
    async function refresh() {
      if (!database.current) return
      try {
        const next = await transact(database.current)
        if (active) setData(next)
      } catch (cause) { report(cause) }
    }
    openStorage().then(db => {
      if (!active) return db.close()
      database.current = db
      if (typeof BroadcastChannel !== 'undefined') {
        channel.current = new BroadcastChannel('pomogator-data')
        channel.current.onmessage = () => void refresh()
      }
      void refresh()
    }).catch(report)
    window.addEventListener('focus', refresh)
    return () => {
      active = false
      window.removeEventListener('focus', refresh)
      channel.current?.close()
      database.current?.close()
    }
  }, [])

  useEffect(() => {
    const shortcut = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k' && !modal) {
        event.preventDefault()
        searchInput.current?.focus()
      }
    }
    window.addEventListener('keydown', shortcut)
    return () => window.removeEventListener('keydown', shortcut)
  }, [modal])

  function show(next: Modal) {
    setModalError('')
    setModal(next)
  }

  async function commit(change: Change, fromModal = false, done?: () => void) {
    if (!database.current || writing.current) return
    writing.current = true
    setBusy(true)
    setError('')
    setModalError('')
    setNotice('')
    try {
      setData(await transact(database.current, change))
      channel.current?.postMessage('updated')
      if (fromModal) setModal(undefined)
      done?.()
      setNotice('Изменения сохранены')
    } catch (cause) {
      const message = `Не удалось сохранить изменения. ${cause instanceof Error ? cause.message : ''}`
      if (fromModal) setModalError(message)
      else setError(message)
    } finally {
      writing.current = false
      setBusy(false)
    }
  }

  async function download() {
    if (!database.current) return
    setError('')
    try {
      const snapshot = await transact(database.current)
      const url = URL.createObjectURL(new Blob([exportData(snapshot)], { type: 'application/json' }))
      const link = document.createElement('a')
      link.href = url
      link.download = `pomogator-${new Date().toISOString().slice(0, 10)}.json`
      link.click()
      setTimeout(() => URL.revokeObjectURL(url), 1000)
      setNotice('Резервная копия экспортирована')
    } catch { setError('Не удалось экспортировать базу.') }
  }

  async function upload(file: File) {
    setError('')
    try {
      const replacement = importData(await file.text())
      show({ kind: 'confirm', title: 'Заменить локальные данные?',
        message: 'Текущие разделы, папки и записи будут заменены содержимым файла.',
        action: () => replacement, done: () => navigate(null) })
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Не удалось прочитать файл.') }
  }

  function navigate(id: string | null) {
    setCurrent(id)
    setQuery('')
  }

  const path = data && current ? pathTo(data, current) : []
  const place = path.at(-1)
  const parent = place?.id ?? null
  const roots = data?.places.filter(item => item.parent === null) ?? []
  const children = data?.places.filter(item => item.parent === parent) ?? []
  const entries = data?.entries.filter(entry => entry.parent === parent) ?? []
  const result = data && query.trim() ? search(data, query) : undefined

  function newPlace(parentId: string | null) {
    show({ kind: 'place', editing: false, value: { id: crypto.randomUUID(), parent: parentId,
      title: '', icon: parentId === null ? 'star' : 'folder' } })
  }

  function newEntry(parentId: string) {
    show({ kind: 'entry', editing: false, value: { id: crypto.randomUUID(), parent: parentId,
      title: '', text: '', links: [], checklist: [], date: '', amount: '', created: '', updated: '' } })
  }

  function removePlace(item: Place) {
    if (!data) return
    const nested = contents(data, item.id)
    const hasContent = nested.ids.size > 1 || nested.entries.length > 0
    show({ kind: 'confirm', title: `Удалить «${item.title}»?`,
      message: hasContent ? `Вложенных папок: ${nested.ids.size - 1}. Записей: ${nested.entries.length}. Всё содержимое будет удалено.` : 'Действие нельзя отменить.',
      action: snapshot => deletePlace(snapshot, item.id, hasContent),
      done: () => { if (current && nested.ids.has(current)) navigate(item.parent) } })
  }

  function renderPlace(item: Place) {
    const siblings = data!.places.filter(candidate => candidate.parent === item.parent)
    const index = siblings.findIndex(candidate => candidate.id === item.id)
    const folderCount = data!.places.filter(candidate => candidate.parent === item.id).length
    const entryCount = data!.entries.filter(entry => entry.parent === item.id).length
    return <article className={`place-card tone-${item.icon}`} key={item.id}>
      <button className="place-open" onClick={() => navigate(item.id)}>
        <span className="tile"><Glyph icon={item.icon} /></span><ChevronRight className="card-chevron" size={19} />
        <strong>{item.title}</strong>
        <span className="muted">{folderCount} пап. · {entryCount} зап.</span>
      </button>
      <div className="tools card-tools">
        <button className="icon-button" aria-label={`Редактировать «${item.title}»`} onClick={() => show({ kind: 'place', value: item, editing: true })}><Pencil size={16} /></button>
        <button className="icon-button" aria-label={`Передвинуть «${item.title}» выше`} disabled={busy || index === 0} onClick={() => void commit(snapshot => orderPlace(snapshot, item.id, -1))}><ArrowUp size={17} /></button>
        <button className="icon-button" aria-label={`Передвинуть «${item.title}» ниже`} disabled={busy || index === siblings.length - 1} onClick={() => void commit(snapshot => orderPlace(snapshot, item.id, 1))}><ArrowDown size={17} /></button>
        <button className="icon-button" aria-label={`Удалить «${item.title}»`} onClick={() => removePlace(item)}><Trash2 size={16} /></button>
      </div>
    </article>
  }

  return <div className="shell">
    <aside className="sidebar">
      <button className="brand" onClick={() => navigate(null)}><span className="brand-icon"><LayoutGrid size={21} /></span>ПОМОГАТОР</button>
      <nav aria-label="Разделы">
        <button className={!place && !result ? 'selected' : ''} onClick={() => navigate(null)}><LayoutGrid size={19} /> Все разделы</button>
        {roots.map(root => <button key={root.id} className={path[0]?.id === root.id && !result ? 'selected' : ''}
          onClick={() => navigate(root.id)}><Glyph icon={root.icon} />{root.title}</button>)}
      </nav>
      <div className="sidebar-bottom">
        <button disabled={!data} onClick={() => input.current?.click()}><Upload size={18} /> Импорт JSON</button>
        <button disabled={!data} onClick={() => void download()}><Download size={18} /> Экспорт JSON</button>
        <input ref={input} hidden type="file" accept=".json,application/json" onChange={event => {
          const file = event.target.files?.[0]
          event.target.value = ''
          if (file) void upload(file)
        }} />
      </div>
    </aside>
    <main>
      <div className="topbar">
        <div className="search">
          <Search aria-hidden="true" size={20} />
          <input ref={searchInput} type="search" aria-label="Поиск по органайзеру" placeholder="Поиск по органайзеру" value={query}
            onChange={event => setQuery(event.target.value)} />
          {query ? <button className="icon-button" aria-label="Очистить поиск" onClick={() => setQuery('')}><X size={18} /></button> : <kbd>Ctrl K</kbd>}
        </div>
        <span className="local"><span /> Локальная база</span>
      </div>
      {error && <p className="error banner" role="alert">{error}</p>}
      <p className="sr-only" role="status">{notice}</p>
      {!data && !error && <p role="status">Загрузка локальной базы…</p>}
      {data && <div className="workspace">
        {!result && place && <nav className="breadcrumbs" aria-label="Расположение">
          <button onClick={() => navigate(null)}>Все разделы</button>
          {path.map(part => <span key={part.id}><ChevronRight size={14} />
            {part.id === parent ? <span aria-current="page">{part.title}</span> :
              <button onClick={() => navigate(part.id)}>{part.title}</button>}</span>)}
        </nav>}
        <header className="page-header">
          <div><h1>{result ? 'Результаты поиска' : place?.title ?? 'Все разделы'}</h1>
            {!result && !place && <p>Один раз настроил — всё под рукой</p>}</div>
          {!result && <div className="actions">
            {place && <button onClick={() => newPlace(parent)}><Plus size={18} /> Папка</button>}
            <button className="primary" onClick={() => parent === null ? newPlace(null) : newEntry(parent)}>
              <Plus size={20} /> {place ? 'Новая запись' : 'Новый раздел'}
            </button>
          </div>}
        </header>
        {result ? <div className="results">
          {result.places.map(item => <button key={item.id} className="result" onClick={() => navigate(item.id)}>
            <Glyph icon={item.icon} /><span><strong>{item.title}</strong>
              <small>{pathTo(data, item.id).slice(0, -1).map(part => part.title).join(' / ') || 'Раздел'}</small></span>
          </button>)}
          {result.entries.map(entry => <button key={entry.id} className="result" onClick={() => {
            navigate(entry.parent)
            show({ kind: 'entry', value: entry, editing: true })
          }}><FileText size={22} /><span><strong>{entry.title}</strong>
            <small>{pathTo(data, entry.parent).map(part => part.title).join(' / ')}</small></span></button>)}
          {!result.places.length && !result.entries.length && <p className="empty">Совпадений не найдено</p>}
        </div> : <>
          {children.length > 0 && <div className="grid">{children.map(renderPlace)}</div>}
          {entries.length > 0 && <div className="entries">{entries.map(entry => <EntryCard key={entry.id} entry={entry}
            edit={() => show({ kind: 'entry', value: entry, editing: true })}
            remove={() => show({ kind: 'confirm', title: `Удалить «${entry.title}»?`, message: 'Действие нельзя отменить.',
              action: snapshot => deleteEntry(snapshot, entry.id) })}
            toggle={index => void commit(snapshot => {
              const latest = snapshot.entries.find(item => item.id === entry.id)
              if (!latest || JSON.stringify(latest.checklist) !== JSON.stringify(entry.checklist)) {
                throw new Error('Чек-лист уже изменён в другой вкладке.')
              }
              return saveEntry(snapshot, { ...latest, checklist: latest.checklist.map((row, i) =>
                i === index ? { ...row, done: !row.done } : row) }, true)
            })} />)}</div>}
          {!children.length && !entries.length && <div className="empty"><FolderOpen size={28} />
            <p>{place ? 'Здесь пока пусто' : 'Разделов пока нет'}</p></div>}
        </>}
      </div>}
    </main>
    {modal?.kind === 'place' && <PlaceEditor place={modal.value} close={() => { if (!busy) setModal(undefined) }}
      busy={busy} error={modalError} save={value => void commit(snapshot => savePlace(snapshot, value, modal.editing), true)} />}
    {modal?.kind === 'entry' && data && <EntryEditor entry={modal.value} data={data} close={() => { if (!busy) setModal(undefined) }}
      busy={busy} error={modalError} save={value => void commit(snapshot => {
        if (modal.editing && JSON.stringify(snapshot.entries.find(item => item.id === value.id)) !== JSON.stringify(modal.value)) {
          throw new Error('Запись изменилась. Закройте редактор и откройте её снова.')
        }
        return saveEntry(snapshot, value, modal.editing)
      }, true, () => navigate(value.parent))} />}
    {modal?.kind === 'confirm' && <Dialog title={modal.title} close={() => { if (!busy) setModal(undefined) }}>
      <p>{modal.message}</p>
      {modalError && <p className="error" role="alert">{modalError}</p>}
      <div className="actions">
        <button autoFocus disabled={busy} onClick={() => setModal(undefined)}>Отмена</button>
        <button className="danger" disabled={busy} onClick={() => void commit(modal.action, true, modal.done)}>Подтвердить</button>
      </div>
    </Dialog>}
  </div>
}
