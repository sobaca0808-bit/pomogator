import { useLayoutEffect, useState, type RefObject } from 'react'
import { ArrowDown, ArrowUp, ChevronRight, ExternalLink, Globe, LayoutGrid, Link2, Mail, Music2, Pencil, Plus, Trash2 } from 'lucide-react'
import { siDiscord, siGithub, siGmail, siGooglemaps, siNotion, siSteam, siTelegram, siVk, siWhatsapp, siYoutube } from 'simple-icons'
import { Dialog } from './editors'
import { shortcutCatalog, shortcutUrl, type Shortcut, type ShortcutIcon } from './shortcuts'

const brands = {
  telegram: siTelegram, whatsapp: siWhatsapp, vk: siVk, youtube: siYoutube, github: siGithub,
  steam: siSteam, gmail: siGmail, discord: siDiscord, notion: siNotion, maps: siGooglemaps,
}
const generic = { custom: Link2, browser: Globe, mail: Mail, yandexMusic: Music2 }

function ShortcutGlyph({ icon }: { icon: ShortcutIcon }) {
  if (icon in brands) {
    const brand = brands[icon as keyof typeof brands]
    return <span className="shortcut-icon" aria-hidden="true">
      <svg viewBox="0 0 24 24" width="25" height="25" fill={'#' + brand.hex}><path d={brand.path} /></svg>
    </span>
  }
  const Icon = generic[icon as keyof typeof generic]
  return <span className={`shortcut-icon shortcut-generic shortcut-${icon}`} aria-hidden="true"><Icon size={24} /></span>
}

export function CenterTile({ open, count }: { open: () => void; count: number }) {
  return <button className="center-tile glass-surface" type="button" onClick={open} aria-label="Мой центр">
    <span className="widget-icon center-tile-icon"><LayoutGrid size={24} aria-hidden="true" /></span>
    <span className="center-tile-label"><strong>Мой центр</strong><small>{count ? `Сохранено ярлыков: ${count}` : 'Быстрый доступ к сайтам и приложениям'}</small></span>
    <ChevronRight size={22} aria-hidden="true" />
  </button>
}

export function MyCenter({ shortcuts, busy, edit, remove, move }: {
  shortcuts: Shortcut[]; busy: boolean; edit: (item?: Shortcut) => void;
  remove: (item: Shortcut) => void; move: (item: Shortcut, offset: -1 | 1) => void
}) {
  const [arrange, setArrange] = useState(false)
  return <section aria-label="Мои ярлыки">
    <div className="center-toolbar actions">
      <button className="primary" onClick={() => edit()} disabled={busy}><Plus size={19} /> Добавить ярлык</button>
      {shortcuts.length > 0 && <button aria-pressed={arrange} disabled={busy} onClick={() => setArrange(!arrange)}>
        {arrange ? 'Готово' : 'Управлять ярлыками'}
      </button>}
    </div>
    {!shortcuts.length && <p className="empty">Добавьте нужные ярлыки из каталога или укажите свою HTTPS-ссылку.</p>}
    {shortcuts.length > 0 && <ul className="shortcut-grid">
      {shortcuts.map((item, index) => <li key={item.id} className="widget shortcut-card">
        <a className="shortcut-open" href={item.url} target="_blank" rel="noopener noreferrer">
          <ShortcutGlyph icon={item.icon} /><strong>{item.title}</strong><ExternalLink size={15} aria-hidden="true" />
        </a>
        {arrange && <div className="shortcut-tools tools">
          <button className="icon-button" disabled={busy || index === 0} onClick={() => move(item, -1)}
            aria-label={`Передвинуть «${item.title}» ближе к началу`}><ArrowUp size={17} /></button>
          <button className="icon-button" disabled={busy || index === shortcuts.length - 1} onClick={() => move(item, 1)}
            aria-label={`Передвинуть «${item.title}» ближе к концу`}><ArrowDown size={17} /></button>
          <button className="icon-button" disabled={busy} onClick={() => edit(item)}
            aria-label={`Изменить ярлык «${item.title}»`}><Pencil size={17} /></button>
          <button className="icon-button" disabled={busy} onClick={() => remove(item)}
            aria-label={`Удалить ярлык «${item.title}»`}><Trash2 size={17} /></button>
        </div>}
      </li>)}
    </ul>}
  </section>
}

const catalogIcons = Object.keys(shortcutCatalog) as ShortcutIcon[]

export function ShortcutEditor({ value, editing, busy, close, guard, error, save }: {
  value: Shortcut; editing: boolean; busy: boolean; close: () => void; error: string;
  guard: RefObject<(() => boolean | Promise<boolean>) | null>; save: (item: Shortcut) => void
}) {
  const [draft, setDraft] = useState(value)
  const dirty = draft.title !== value.title || draft.url !== value.url || draft.icon !== value.icon
  const url = shortcutUrl(draft.url)
  const canClose = () => !busy && (!dirty || window.confirm('Закрыть ярлык и потерять несохранённые изменения?'))
  useLayoutEffect(() => {
    guard.current = canClose
    return () => { guard.current = null }
  })
  const askClose = () => { if (canClose()) close() }

  function select(icon: ShortcutIcon) {
    setDraft(current => {
      const previous = shortcutCatalog[current.icon]
      const selected = shortcutCatalog[icon]
      return {
        ...current, icon,
        title: !current.title || (!editing && current.title === previous.name) ? (icon === 'custom' ? '' : selected.name) : current.title,
        url: !current.url || (!editing && current.url === previous.url) ? selected.url : current.url,
      }
    })
  }

  return <Dialog className="shortcut-dialog" title={editing ? 'Изменить ярлык' : 'Добавить ярлык'} close={askClose}>
    <form noValidate onSubmit={event => {
      event.preventDefault()
      if (!busy && draft.title.trim() && url) save({ ...draft, title: draft.title.trim(), url })
    }}>
      <fieldset disabled={busy}>
        <legend>Каталог значков</legend>
        <div className="shortcut-catalog">
          {catalogIcons.map(icon => <button type="button" key={icon} aria-pressed={draft.icon === icon} onClick={() => select(icon)}>
            <ShortcutGlyph icon={icon} /><span>{shortcutCatalog[icon].name}</span>
          </button>)}
        </div>
      </fieldset>
      <label>Название<input autoFocus={editing} value={draft.title} disabled={busy} onChange={event => setDraft({ ...draft, title: event.target.value })} /></label>
      <label>HTTPS-ссылка<input type="url" value={draft.url} disabled={busy} placeholder="https://"
        aria-invalid={Boolean(draft.url && !url)} aria-describedby="shortcut-url-help"
        onChange={event => setDraft({ ...draft, url: event.target.value })} /></label>
      <p id="shortcut-url-help" className={draft.url && !url ? 'error' : 'muted'}>
        {draft.url && !url ? 'Укажите полную HTTPS-ссылку без имени пользователя и пароля.' :
          'По ссылке откроется сайт или связанное с ним приложение, если это поддерживает устройство.'}
      </p>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="actions shortcut-editor-actions"><button type="button" onClick={askClose} disabled={busy}>Отмена</button>
        <button className="primary" disabled={busy || !draft.title.trim() || !url}>Сохранить ярлык</button></div>
    </form>
  </Dialog>
}
