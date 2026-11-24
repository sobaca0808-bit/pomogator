import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { ArrowDown, ArrowLeft, ArrowUp, Bell, CalendarDays, ChevronRight, Download, Ellipsis, FileText, FolderOpen, Pencil, Plus, Search, Trash2, Undo2, Upload, X } from 'lucide-react'
import { downloadFile } from './attachments'
import { exportBackup, importBackup } from './backup'
import { CardEditor } from './CardEditor'
import { CardView } from './CardView'
import { blocksOf, cardStatus, createCard, kindName, kinds, kindOf, preferences, rootFor, setStatus, widgetNames, type Kind, type Preferences } from './cards'
import { Dialog, Glyph, PlaceEditor } from './editors'
import { contents, deleteEntry, deletePlace, orderPlace, pathTo, restoreDeleted, saveEntry, savePlace, search,
  type Change, type Data, type Entry, type Place } from './model'
import { CenterTile, MyCenter, ShortcutEditor } from './MyCenter'
import { Disclosure, OrganizerHeader } from './OrganizerHeader'
import { moveShortcut, removeShortcut, saveShortcut, type Shortcut } from './shortcuts'
import { Calendar, Finance, Overview, WidgetSection } from './Overview'
import { dayKey, dueReminders, fromDay } from './schedule'
import { useNavigation, type View } from './navigation'
import { buildLabel } from './version'
import { openStorage, transact } from './storage'

type Modal = { kind: 'place'; value: Place; editing: boolean } |
  { kind: 'entry'; value: Entry; editing: boolean } |
  { kind: 'quick'; day: string } |
  { kind: 'shortcut'; value: Shortcut; previous?: Shortcut; generation?: string } |
  { kind: 'confirm'; title: string; message: string; action: Change; done?: () => void }
const databaseName = import.meta.env.VITE_POMOGATOR_STORAGE || 'pomogator'
const messageOf = (cause: unknown) => cause instanceof Error ? cause.message : 'Неизвестная ошибка.'

export function App() {
  const [data, setData] = useState<Data>()
  const writing = useRef(false)
  const navigation = useNavigation<Modal>(() => !writing.current)
  const { view, folder: current, query } = navigation.frame
  const modal = navigation.dialog
  const setQuery = navigation.search
  const [theme, setTheme] = useState<'dark' | 'light'>(() => {
    try { return localStorage.getItem(databaseName + '-theme') === 'light' ? 'light' : 'dark' }
    catch { return 'dark' }
  })
  useLayoutEffect(() => {
    const root = document.documentElement
    root.dataset.theme = theme
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', getComputedStyle(root).getPropertyValue('--page').trim())
    try { localStorage.setItem(databaseName + '-theme', theme) } catch { /* The theme still applies to this session. */ }
  }, [theme])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [modalError, setModalError] = useState('')
  const [notice, setNotice] = useState('')
  const [undo, setUndo] = useState<Change>()
  const [now, setNow] = useState(Date.now)
  const [showScrollTop, setShowScrollTop] = useState(false)
  const database = useRef<IDBDatabase>(null)
  const channel = useRef<BroadcastChannel>(null)
  const input = useRef<HTMLInputElement>(null)
  const searchInput = useRef<HTMLInputElement>(null)
  const pageTitle = useRef<HTMLHeadingElement>(null)
  const notified = useRef(new Set<string>())
  const today = dayKey(new Date(now))
  const reminders = data ? dueReminders(data, now, preferences(data).seenReminders) : []

  useEffect(() => {
    let active = true
    const report = (cause: unknown) => { if (active) setError(`Ошибка чтения базы. ${messageOf(cause)}`) }
    async function refresh() {
      if (!database.current) return
      try {
        const next = await transact(database.current)
        if (active) setData(next)
      } catch (cause) { report(cause) }
    }
    openStorage(databaseName).then(db => {
      if (!active) return db.close()
      database.current = db
      if (typeof BroadcastChannel !== 'undefined') {
        channel.current = new BroadcastChannel(databaseName + '-data')
        channel.current.onmessage = () => void refresh()
      }
      void refresh()
    }).catch(report)
    window.addEventListener('focus', refresh)
    const timer = setInterval(() => setNow(Date.now()), 15000)
    return () => {
      active = false
      window.removeEventListener('focus', refresh)
      clearInterval(timer)
      channel.current?.close()
      database.current?.close()
    }
  }, [])

  useEffect(() => {
    function updateScrollTop() { setShowScrollTop(window.scrollY > window.innerHeight) }
    window.addEventListener('scroll', updateScrollTop, { passive: true })
    window.addEventListener('resize', updateScrollTop)
    updateScrollTop()
    return () => {
      window.removeEventListener('scroll', updateScrollTop)
      window.removeEventListener('resize', updateScrollTop)
    }
  }, [])

  useEffect(() => {
    const shortcut = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k' && !modal && !document.querySelector('dialog[open]')) {
        event.preventDefault()
        searchInput.current?.focus()
      }
    }
    window.addEventListener('keydown', shortcut)
    return () => window.removeEventListener('keydown', shortcut)
  }, [modal])

  useEffect(() => {
    if (!data || typeof Notification === 'undefined' || Notification.permission !== 'granted') return
    const due = dueReminders(data, now, preferences(data).seenReminders)
    for (const reminder of due.slice(0, 3)) {
      if (notified.current.has(reminder.key)) continue
      notified.current.add(reminder.key)
      try { new Notification('ПОМОГАТОР', { body: reminder.entry.title, tag: reminder.key }) }
      catch { /* Встроенные напоминания остаются доступны. */ }
    }
  }, [data, now])

  function show(next: Modal) {
    setModalError('')
    navigation.show(next)
  }

  async function apply(change: Change) {
    if (!database.current) throw new Error('Локальная база недоступна.')
    const next = await transact(database.current, change)
    setData(next)
    channel.current?.postMessage('updated')
    return next
  }

  async function commit(change: Change, fromModal = false, done?: () => void) {
    if (!database.current || writing.current) return
    writing.current = true
    setBusy(true)
    setError('')
    setModalError('')
    setNotice('')
    try {
      await apply(change)
      setNotice('Изменения сохранены')
      if (fromModal) navigation.dismiss(done)
      else done?.()
    } catch (cause) {
      const message = `Не удалось сохранить изменения. ${messageOf(cause)}`
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
      downloadFile(new Blob([await exportBackup(snapshot)], { type: 'application/json' }), `pomogator-${dayKey()}.json`)
      setNotice('Резервная копия экспортирована')
    } catch (cause) { setError(`Не удалось экспортировать базу. ${messageOf(cause)}`) }
  }

  async function upload(file: File) {
    setError('')
    try {
      const replacement = importBackup(await file.text())
      show({ kind: 'confirm', title: 'Заменить локальные данные?',
        message: 'Разделы, карточки, ярлыки, вложения и настройки будут заменены содержимым файла. Для сохранения текущей базы сначала экспортируйте JSON.',
        action: () => ({ ...replacement, generation: crypto.randomUUID() }), done: () => { setUndo(undefined); openView('home') } })
    } catch (cause) { setError(messageOf(cause)) }
  }

  function openView(next: View) { navigation.open(next) }

  function navigate(id: string | null) { navigation.open('folders', id) }

  const path = data && current ? pathTo(data, current) : []
  const place = path.at(-1)
  const parent = place?.id ?? null
  const roots = data?.places.filter(item => item.parent === null) ?? []
  const children = data?.places.filter(item => item.parent === parent) ?? []
  const entries = data?.entries.filter(entry => entry.parent === parent) ?? []
  const result = data && query.trim() ? search(data, query) : undefined
  const home = view === 'home' && !result
  const heading = result ? 'Результаты поиска' : view === 'home' ? 'Обзор' : view === 'calendar' ? 'Календарь' : view === 'settings' ? 'Настройки' : view === 'center' ? 'Мой центр' : view === 'folders' ? place?.title ?? 'Все разделы' : widgetNames[view]

  function editShortcut(item?: Shortcut) {
    show({ kind: 'shortcut', previous: item, generation: data?.generation,
      value: item ?? { id: crypto.randomUUID(), title: '', url: '', icon: 'custom' } })
  }

  function newPlace(parentId: string | null) {
    show({ kind: 'place', editing: false, value: { id: crypto.randomUUID(), parent: parentId,
      title: '', icon: parentId === null ? 'star' : 'folder' } })
  }

  function newEntry(parentId: string, type?: Kind, date = '', parentCard?: string) {
    const kind = type ?? kindOf(data!, { parent: parentId })
    show({ kind: 'entry', editing: false, value: createCard(kind, parentId, date, parentCard) })
  }

  function open(entry: Entry) {
    show({ kind: 'entry', editing: true, value: entry })
  }

  function updatePrefs(change: (value: Preferences) => Preferences) {
    void commit(snapshot => ({ ...snapshot, preferences: change(preferences(snapshot)) }))
  }

  function confirmation(title: string, message: string, remove: Change, done?: () => void) {
    let recover: Change | undefined
    show({ kind: 'confirm', title, message, action: snapshot => {
      const next = remove(snapshot)
      const places = new Set(next.places.map(item => item.id))
      const entries = new Set(next.entries.map(item => item.id))
      const droppedPlaces = snapshot.places.filter(item => !places.has(item.id))
      const droppedEntries = snapshot.entries.filter(item => !entries.has(item.id))
      recover = latest => {
        if (latest.generation !== snapshot.generation) throw new Error('База уже заменена из резервной копии.')
        return restoreDeleted(latest, droppedPlaces, droppedEntries)
      }
      return next
    }, done: () => { setUndo(() => recover); setNotice('Удалено'); done?.() } })
  }

  function removePlace(item: Place) {
    if (!data) return
    const nested = contents(data, item.id)
    const hasContent = nested.ids.size > 1 || nested.entries.length > 0
    const shownEntries = new Set(nested.entries.map(entry => entry.id))
    confirmation(`Удалить «${item.title}»?`, hasContent
      ? `Вложенных папок: ${nested.ids.size - 1}. Карточек: ${nested.entries.length}. Они тоже будут удалены.`
      : 'Папка будет удалена из локальной базы.', snapshot => {
      const latest = contents(snapshot, item.id)
      if ([...latest.ids].some(id => !nested.ids.has(id)) || latest.entries.some(entry => !shownEntries.has(entry.id)))
        throw new Error('Появилось новое содержимое. Закройте окно и проверьте папку перед удалением.')
      return deletePlace(snapshot, item.id, hasContent)
    },
    () => { if (current && nested.ids.has(current)) navigate(item.parent) })
  }

  function mutate(entry: Entry, change: (latest: Entry) => Entry) {
    void commit(snapshot => {
      const latest = snapshot.entries.find(item => item.id === entry.id)
      if (!latest) throw new Error('Карточка уже удалена.')
      return saveEntry(snapshot, change(latest), true, entry.revision ?? 0)
    })
  }

  function renderPlace(item: Place) {
    const siblings = data!.places.filter(candidate => candidate.parent === item.parent)
    const index = siblings.findIndex(candidate => candidate.id === item.id)
    const folderCount = data!.places.filter(candidate => candidate.parent === item.id).length
    const entryCount = data!.entries.filter(entry => entry.parent === item.id).length
    return <article className={`place-card glass-surface tone-${item.icon}`} key={item.id}>
      <button className="place-open" onClick={() => navigate(item.id)}>
        <span className="tile"><Glyph icon={item.icon} /></span>
        <span className="place-label"><strong>{item.title}</strong>
          <small>{folderCount} пап. · {entryCount} карт.</small>
        </span>
        <ChevronRight className="card-chevron" size={16} aria-hidden="true" />
      </button>
      <Disclosure className="place-menu" label={`Действия «${item.title}»`} icon={Ellipsis}>
        <button aria-label={`Настроить «${item.title}»`} onClick={() => show({ kind: 'place', value: item, editing: true })}><Pencil size={16} /> Настроить</button>
        <button aria-label={`Передвинуть «${item.title}» выше`} disabled={busy || index === 0} onClick={() => void commit(snapshot => orderPlace(snapshot, item.id, -1))}><ArrowUp size={17} /> Выше</button>
        <button aria-label={`Передвинуть «${item.title}» ниже`} disabled={busy || index === siblings.length - 1} onClick={() => void commit(snapshot => orderPlace(snapshot, item.id, 1))}><ArrowDown size={17} /> Ниже</button>
        <button className="remove-place" aria-label={`Удалить «${item.title}»`} onClick={() => removePlace(item)}><Trash2 size={16} /> Удалить</button>
      </Disclosure>
    </article>
  }

  const close = () => { if (!writing.current) navigation.dismiss() }
  return <div className="shell" data-workspace={view === 'home' || (view === 'folders' && !place) || undefined}>
    <OrganizerHeader roots={roots} activeView={result ? undefined : view} activeRoot={result ? undefined : path[0]?.id}
      openView={openView} navigate={navigate} focusSearch={() => searchInput.current?.focus()}>
      {!home && <div className="section-bar">
        {navigation.canBack && view !== 'home' &&
          <button className="back-button" onClick={navigation.back}><ArrowLeft size={18} /> Назад</button>}
        <h1 ref={pageTitle} tabIndex={-1}>{heading}</h1>
      </div>}
    </OrganizerHeader>
    <main>
      <div className={home ? 'overview-intro glass-surface' : 'workspace-search'}>
        {home && <div className="overview-title">
          <p><CalendarDays size={16} aria-hidden="true" />
            {fromDay(today).toLocaleDateString('ru-RU', { weekday: 'short', day: 'numeric', month: 'long' })}</p>
          <h1 ref={pageTitle} tabIndex={-1}>{heading}</h1>
        </div>}
        <div className="search">
          <Search aria-hidden="true" size={20} />
          <input ref={searchInput} type="search" aria-label="Поиск по органайзеру" placeholder="Поиск по органайзеру" value={query}
            onChange={event => setQuery(event.target.value)} />
          {query ? <button className="icon-button" aria-label="Очистить поиск" onClick={() => setQuery('')}><X size={18} /></button> : <kbd>Ctrl K</kbd>}
        </div>
        {home && <button className="primary new-card" disabled={!data} onClick={() =>
          roots.length ? show({ kind: 'quick', day: '' }) : newPlace(null)}>
          <Plus size={19} aria-hidden="true" />{roots.length ? 'Новая карточка' : 'Новый раздел'}
        </button>}
      </div>
      {showScrollTop && <button className="scroll-top glass-surface" aria-label="Наверх" onClick={() => {
          pageTitle.current?.focus({ preventScroll: true })
          window.scrollTo({ top: 0, behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' })
        }}><ArrowUp size={19} aria-hidden="true" /><span>Наверх</span></button>}
      <div className="workspace">
        {error && <p className="error banner" role="alert">{error}</p>}
        {(notice || undo) && <div className={undo || notice.includes('копия') || notice === 'Удаление отменено' ? 'feedback' : 'sr-only'}>
          <span role="status">{notice || 'Доступна отмена последнего удаления'}</span>
          {undo && <button disabled={busy} onClick={() => void commit(undo, false, () => { setUndo(undefined); setNotice('Удаление отменено') })}><Undo2 size={16} /> Отменить удаление</button>}
        </div>}
        {!data && !error && <p role="status">Загрузка локальной базы…</p>}
        {data && <>
          {!result && place && <nav className="breadcrumbs" aria-label="Расположение">
            <button onClick={() => navigate(null)}>Все разделы</button>
            {path.map(part => <span key={part.id}><ChevronRight size={14} />
              {part.id === parent ? <span aria-current="page">{part.title}</span> :
                <button onClick={() => navigate(part.id)}>{part.title}</button>}</span>)}
          </nav>}
          {!result && !home && view !== 'settings' && view !== 'center' && <div className="page-header">
            <div className="actions">
              {place && <button onClick={() => newPlace(parent)}><Plus size={18} /> Папка</button>}
              <button className="primary" onClick={() => {
                if (parent) newEntry(parent)
                else if (view === 'folders' || !roots.length) newPlace(null)
                else show({ kind: 'quick', day: '' })
              }}><Plus size={20} /> {view === 'folders' && !place || !roots.length ? 'Новый раздел' : 'Новая карточка'}</button>
            </div>
          </div>}
          {!result && reminders.slice(0, 3).map(reminder => <div className="reminder" key={reminder.key}>
            <Bell size={18} /><button onClick={() => open(reminder.entry)}>{reminder.entry.title}</button>
            <button className="icon-button" aria-label={`Скрыть напоминание «${reminder.entry.title}»`} disabled={busy}
              onClick={() => updatePrefs(prefs => ({ ...prefs, seenReminders: [...prefs.seenReminders, reminder.key] }))}><X size={17} /></button>
          </div>)}
          {result ? <div className="results">
            {result.places.map(item => <button key={item.id} className="result" onClick={() => navigate(item.id)}>
              <Glyph icon={item.icon} /><span><strong>{item.title}</strong>
                <small>{pathTo(data, item.id).slice(0, -1).map(part => part.title).join(' / ') || 'Раздел'}</small></span>
            </button>)}
            {result.entries.map(entry => <button key={entry.id} className="result" onClick={() => { navigate(entry.parent); open(entry) }}>
              <FileText size={22} /><span><strong>{entry.title}</strong>
                <small>{pathTo(data, entry.parent).map(part => part.title).join(' / ')}</small></span></button>)}
            {!result.places.length && !result.entries.length && <p className="empty">Совпадений не найдено</p>}
          </div> : view === 'home' ? <div className="overview-layout">
            <section className="overview-sections" aria-labelledby="overview-sections-title">
              <CenterTile open={() => openView('center')} count={data.shortcuts?.length ?? 0} />
              <div className="overview-section-heading">
                <h2 id="overview-sections-title">Разделы</h2>
                <button className="glass-surface" aria-label="Добавить раздел" onClick={() => newPlace(null)}><Plus size={16} aria-hidden="true" /> Раздел</button>
              </div>
              {roots.length > 0 && <div className="grid overview-places">{roots.map(renderPlace)}</div>}
            </section>
            <Overview data={data} today={today} open={open} navigate={openView} update={updatePrefs} busy={busy} />
          </div> :
            view === 'center' ? <MyCenter shortcuts={data.shortcuts ?? []} busy={busy} edit={editShortcut}
              move={(item, offset) => void commit(snapshot => moveShortcut(snapshot, item.id, offset))}
              remove={item => {
                const generation = data.generation
                show({ kind: 'confirm', title: `Удалить ярлык «${item.title}»?`, message: 'Ярлык будет удалён из «Моего центра».',
                  action: snapshot => {
                    if (snapshot.generation !== generation) throw new Error('База заменена в другой вкладке.')
                    return removeShortcut(snapshot, item)
                  } })
              }} /> :
            view === 'calendar' ? <Calendar data={data} today={today} open={open} create={day => show({ kind: 'quick', day })} /> :
            view === 'settings' ? <section className="widget app-settings">
              <label className="check-item"><input type="checkbox" checked={theme === 'light'}
                onChange={event => setTheme(event.target.checked ? 'light' : 'dark')} /> Светлая тема</label>
              <div className="actions">
                <button disabled={busy} onClick={() => input.current?.click()}><Upload size={18} /> Импорт JSON</button>
                <button disabled={busy} onClick={() => void download()}><Download size={18} /> Экспорт JSON</button>
                <input ref={input} hidden type="file" accept=".json,application/json" onChange={event => {
                  const file = event.target.files?.[0]
                  event.target.value = ''
                  if (file) void upload(file)
                }} />
              </div>
            </section> :
            view !== 'folders' ? <WidgetSection data={data} today={today} widget={view} open={open} /> : <>
              {place?.id === 'finance' && <Finance data={data} open={open} />}
              {children.length > 0 && <div className="grid">{children.map(renderPlace)}</div>}
              {entries.length > 0 && <div className="entries">{entries.map(entry => <CardView key={entry.id} entry={entry} data={data} today={today}
                open={open} remove={() => confirmation(`Удалить «${entry.title}»?`, 'Карточка и её вложения будут удалены.',
                  snapshot => deleteEntry(snapshot, entry.id))}
                favorite={() => mutate(entry, latest => ({ ...latest, blocks: blocksOf(latest), favorite: !latest.favorite }))}
                complete={() => mutate(entry, latest => setStatus(latest, cardStatus(latest) === 'done' ? 'active' : 'done'))}
                toggle={(id, index) => mutate(entry, latest => ({ ...latest, blocks: blocksOf(latest).map(block =>
                  block.type === 'checklist' && block.id === id ? { ...block, items: block.items.map((item, i) =>
                    i === index ? { ...item, done: !item.done } : item) } : block) }))} />)}</div>}
              {!children.length && !entries.length && place?.id !== 'finance' && <div className="empty"><FolderOpen size={28} />
                <p>{place ? 'Создайте папку или карточку в этом разделе.' : 'Создайте раздел, чтобы начать работу.'}</p></div>}
            </>}
        </>}
      </div>
      <footer role="contentinfo" className="build-label">{buildLabel}</footer>
    </main>
    {modal?.kind === 'quick' && <Dialog title="Выберите тип карточки" close={close}>
      <div className="quick-list">{kinds.map(kind => <button key={kind} onClick={() => {
        const selected = roots.find(root => root.id === rootFor[kind]) ?? roots[0]
        if (selected) newEntry(selected.id, kind, modal.day)
      }}>{kindName[kind]}</button>)}</div>
    </Dialog>}
    {modal?.kind === 'place' && <PlaceEditor place={modal.value} close={close} guard={navigation.guard}
      busy={busy} error={modalError} save={value => void commit(snapshot => savePlace(snapshot, value, modal.editing), true)} />}
    {modal?.kind === 'shortcut' && <ShortcutEditor key={modal.value.id} value={modal.value} editing={Boolean(modal.previous)}
      close={close} guard={navigation.guard} busy={busy} error={modalError} save={value => void commit(snapshot => {
        if (snapshot.generation !== modal.generation) throw new Error('База заменена в другой вкладке. Откройте ярлык заново.')
        return saveShortcut(snapshot, value, modal.previous)
      }, true)} />}
    {modal?.kind === 'entry' && data && <CardEditor key={modal.value.id} entry={modal.value} data={data} editing={modal.editing}
      close={close} guard={navigation.guard} child={entry => newEntry(entry.parent, entry.kind, '', entry.id)} persist={async (value, editing, revision, generation) => {
        const next = await apply(snapshot => {
          if (snapshot.generation !== generation) throw new Error('База заменена в другой вкладке. Откройте карточку заново.')
          return saveEntry(snapshot, value, editing, revision)
        })
        return next.entries.find(item => item.id === value.id)!
      }} />}
    {modal?.kind === 'confirm' && <Dialog title={modal.title} close={close}>
      <p>{modal.message}</p>
      {modalError && <p className="error" role="alert">{modalError}</p>}
      <div className="actions">
        <button autoFocus disabled={busy} onClick={close}>Отмена</button>
        <button className="danger" disabled={busy} onClick={() => void commit(modal.action, true, modal.done)}>Подтвердить</button>
      </div>
    </Dialog>}
  </div>
}
