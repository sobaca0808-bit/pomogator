import { useRef, type ReactNode } from 'react'
import { CalendarDays, House, LayoutGrid, Menu, Search, Settings, type LucideIcon } from 'lucide-react'
import { Glyph } from './editors'
import type { Place } from './model'
import type { View } from './navigation'

export function Disclosure({ label, className, icon: Icon, children }: {
  label: string; className: string; icon: LucideIcon; children: ReactNode
}) {
  const details = useRef<HTMLDetailsElement>(null)
  const toggle = useRef<HTMLElement>(null)
  const panel = useRef<HTMLDivElement>(null)

  function close() {
    if (!details.current?.open) return
    details.current.open = false
    toggle.current?.focus({ preventScroll: true })
  }

  return <details name="organizer-menu" ref={details} className={`disclosure ${className}`}
    onToggle={event => {
      if (!event.currentTarget.open || !toggle.current || !panel.current) return
      const anchor = toggle.current.getBoundingClientRect()
      const height = panel.current.offsetHeight + 8
      const viewport = window.visualViewport
      const top = viewport?.offsetTop ?? 0
      const bottom = top + (viewport?.height ?? window.innerHeight)
      event.currentTarget.toggleAttribute('data-above', anchor.bottom + height > bottom && anchor.top - height >= top)
    }}
    onBlur={event => {
      if (!event.currentTarget.contains(event.relatedTarget)) event.currentTarget.open = false
    }}
    onKeyDown={event => {
      if (event.key === 'Escape' && event.currentTarget.open) {
        event.preventDefault()
        close()
      }
    }}>
    <summary ref={toggle} aria-label={label} title={label}><Icon size={20} aria-hidden="true" /></summary>
    <div ref={panel} className="disclosure-panel" onClick={event => {
      if (event.target instanceof Element && event.target.closest('button:not(:disabled)')) close()
    }}>{children}</div>
  </details>
}

export function OrganizerHeader({ roots, activeView, activeRoot, openView, navigate, focusSearch, children }: {
  roots: Place[]; activeView?: View; activeRoot?: string;
  openView: (view: View) => void; navigate: (folder: string) => void; focusSearch: () => void; children: ReactNode
}) {
  const mainLinks = [
    { view: 'home', title: 'Обзор', icon: House },
    { view: 'folders', title: 'Все разделы', icon: LayoutGrid },
    { view: 'calendar', title: 'Календарь', icon: CalendarDays },
  ] as const

  const renderMainLinks = () => mainLinks.map(({ view, title, icon: Icon }) => <button key={view}
    aria-current={activeView === view && (view !== 'folders' || !activeRoot) ? 'page' : undefined}
    onClick={() => openView(view)}><Icon size={18} aria-hidden="true" />{title}</button>)

  return <header className="app-header glass-surface" role="banner">
    <div className="app-header-row">
      <button className="brand" onClick={() => openView('home')}>
        <span className="brand-icon"><LayoutGrid size={20} aria-hidden="true" /></span>ПОМОГАТОР
      </button>
      <nav className="primary-navigation" aria-label="Главное меню">{renderMainLinks()}</nav>
      <div className="header-actions">
        <button className="header-button" aria-label="Найти" onClick={focusSearch}><Search size={19} aria-hidden="true" /></button>
        <button className="header-button" aria-label="Настройки" aria-current={activeView === 'settings' ? 'page' : undefined}
          onClick={() => openView('settings')}><Settings size={19} aria-hidden="true" /></button>
        <Disclosure className="navigation-menu" label="Меню" icon={Menu}>
          <nav aria-label="Разделы">
            <div className="compact-navigation">{renderMainLinks()}</div>
            {roots.map(root => <button key={root.id} aria-current={activeRoot === root.id ? 'page' : undefined}
              onClick={() => navigate(root.id)}><Glyph icon={root.icon} />{root.title}</button>)}
          </nav>
        </Disclosure>
      </div>
    </div>
    {children}
  </header>
}
