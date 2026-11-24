import { useState } from 'react'
import { ArrowDown, ArrowUp, Bell, CalendarDays, ChevronLeft, ChevronRight, Clock3, Download, ListTodo, Plus, Settings2, Star, Wallet, BriefcaseBusiness } from 'lucide-react'
import { downloadFile } from './attachments'
import { cardStatus, firstValue, kindName, kindOf, preferences, progress, reorder, widgetNames, type Preferences, type Widget } from './cards'
import { shortDate } from './CardView'
import { Dialog } from './editors'
import { financeCsv, financeTotals, money, operations } from './finance'
import type { Data, Entry } from './model'
import { dayEntries, dayKey, fromDay, shiftDay, upcoming } from './schedule'

const icons = { today: CalendarDays, tasks: ListTodo, payments: Wallet, projects: BriefcaseBusiness, recent: Clock3, favorites: Star }
const longDate = { dateStyle: 'full' } as const

function Row({ data, entry, detail, open, ratio }: { data: Data; entry: Entry; detail?: string; open?: (entry: Entry) => void; ratio?: number }) {
  const content = <>
    <span><strong>{entry.title}</strong><small>{detail || kindName[kindOf(data, entry)]}</small></span>
    {ratio !== undefined ? <small className="ratio">{ratio}%</small> : <ChevronRight size={16} />}
  </>
  return open ? <button className="summary-row" onClick={() => open(entry)}>{content}</button> :
    <div className="summary-row">{content}</div>
}

export type WidgetView = Exclude<Widget, 'today'>
const empty = { today: 'На сегодня ничего не запланировано', tasks: 'Ближайших дел нет', payments: 'Предстоящих платежей нет',
  projects: 'Активных проектов нет', recent: 'Здесь появятся ваши карточки', favorites: 'Отметьте звёздочкой нужную ссылку' }

function widgetRows(data: Data, today: string, widget: Widget, limit?: number): { entry: Entry; detail?: string; ratio?: number }[] {
  const matches = (kind: 'task' | 'payment', after: string) => upcoming(data, kind, after, limit ?? Infinity)
  switch (widget) {
    case 'today': return dayEntries(data, today).filter(entry => cardStatus(entry) !== 'done').slice(0, limit).map(entry => ({
      entry, detail: [firstValue(entry, 'time'), kindName[kindOf(data, entry)]].filter(Boolean).join(' · '),
    }))
    case 'tasks': return matches('task', shiftDay(today, 1)).map(({ entry, day }) => ({ entry, detail: shortDate(day) }))
    case 'payments': return matches('payment', today).map(({ entry, day }) => {
      const value = firstValue(entry, 'amount')
      return { entry, detail: [shortDate(day), value && value.replace('.', ',') + ' ₽'].filter(Boolean).join(' · ') }
    })
    case 'projects': return data.entries.filter(entry => kindOf(data, entry) === 'project' && cardStatus(entry) !== 'done')
      .slice(0, limit).map(entry => ({ entry, ratio: progress(entry), detail: firstValue(entry, 'date') ? shortDate(firstValue(entry, 'date')) : 'Проект' }))
    case 'recent': return [...data.entries].sort((a, b) => b.updated.localeCompare(a.updated)).slice(0, limit).map(entry => ({ entry }))
    case 'favorites': return data.entries.filter(entry => kindOf(data, entry) === 'link' && entry.favorite).slice(0, limit)
      .map(entry => ({ entry, detail: firstValue(entry, 'link') }))
  }
}

export function WidgetSection({ data, today, widget, open }: {
  data: Data; today: string; widget: WidgetView; open: (entry: Entry) => void
}) {
  const rows = widgetRows(data, today, widget)
  return <section className="widget">
    {rows.length ? <div className="summary-list">{rows.map(row => <Row key={row.entry.id} data={data} open={open} {...row} />)}</div> :
      <p className="widget-empty">{empty[widget]}</p>}
  </section>
}

export function Overview({ data, open, navigate, update, busy, today }: {
  data: Data; open: (entry: Entry) => void; navigate: (widget: WidgetView) => void;
  update: (change: (value: Preferences) => Preferences) => void; busy: boolean; today: string
}) {
  const [configure, setConfigure] = useState(false)
  const prefs = preferences(data)
  const visible = prefs.order.filter(widget => !prefs.hidden.includes(widget))
  return <>
    <div className="overview-toolbar">
      <p><CalendarDays size={17} /> {fromDay(today).toLocaleDateString('ru-RU', longDate)}</p>
      <button disabled={busy} onClick={() => setConfigure(true)}><Settings2 size={17} /> Виджеты</button>
    </div>
    <div className="overview-grid">{visible.map(widget => {
      const Icon = icons[widget]
      const rows = widgetRows(data, today, widget, 4)
      return <section className={`widget widget-${widget}`} key={widget}>
        <header><span className="widget-icon"><Icon size={19} /></span><h2>{widgetNames[widget]}</h2></header>
        {rows.length ? <div className="summary-list">{rows.map(row => <Row key={row.entry.id} data={data} open={widget === 'today' ? open : undefined} {...row} />)}</div> :
          <p className="widget-empty">{empty[widget]}</p>}
        {widget !== 'today' && <button className="widget-open" aria-label={widgetNames[widget]} onClick={() => navigate(widget)} />}
      </section>
    })}</div>
    {!visible.length && <p className="empty">Все виджеты скрыты. Откройте настройки виджетов, чтобы вернуть их.</p>}
    {configure && <Dialog title="Настроить виджеты" close={() => setConfigure(false)}>
      <p className="muted">Видимость и порядок сохраняются автоматически.</p>
      <div className="widget-settings">{prefs.order.map((widget, index) => <div className="widget-setting" key={widget}>
        <label className="check-item"><input type="checkbox" checked={!prefs.hidden.includes(widget)} disabled={busy}
          onChange={() => update(current => ({ ...current, hidden: current.hidden.includes(widget)
            ? current.hidden.filter(item => item !== widget) : [...current.hidden, widget] }))} /><span>{widgetNames[widget]}</span></label>
        <div className="tools">
          <button className="icon-button" aria-label={`Поднять «${widgetNames[widget]}»`} disabled={busy || index === 0}
            onClick={() => update(current => ({ ...current, order: reorder(current.order, current.order.indexOf(widget), -1) }))}><ArrowUp size={17} /></button>
          <button className="icon-button" aria-label={`Опустить «${widgetNames[widget]}»`} disabled={busy || index === prefs.order.length - 1}
            onClick={() => update(current => ({ ...current, order: reorder(current.order, current.order.indexOf(widget), 1) }))}><ArrowDown size={17} /></button>
        </div>
      </div>)}</div>
      <div className="actions"><button className="primary" onClick={() => setConfigure(false)}>Готово</button></div>
    </Dialog>}
  </>
}

export function Calendar({ data, open, create, today }: {
  data: Data; open: (entry: Entry) => void; create: (day: string) => void; today: string
}) {
  const [month, setMonth] = useState(today.slice(0, 7) + '-01')
  const [selected, setSelected] = useState(today)
  const [notification, setNotification] = useState(typeof Notification === 'undefined' ? 'unsupported' : Notification.permission)
  const first = fromDay(month)
  const start = shiftDay(month, -((first.getDay() + 6) % 7))
  const shift = (offset: number) => {
    const date = fromDay(month)
    date.setMonth(date.getMonth() + offset)
    setMonth(dayKey(date))
    setSelected(dayKey(date))
  }
  const entries = dayEntries(data, selected)
  return <div className="calendar-layout">
    <section className="widget calendar">
      <header>
        <h2>{first.toLocaleDateString('ru-RU', { month: 'long', year: 'numeric' })}</h2>
        <div className="tools">
          <button className="icon-button" aria-label="Предыдущий месяц" onClick={() => shift(-1)}><ChevronLeft size={19} /></button>
          <button className="icon-button" aria-label="Следующий месяц" onClick={() => shift(1)}><ChevronRight size={19} /></button>
        </div>
      </header>
      <div className="calendar-grid">
        {['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'].map(day => <small key={day}>{day}</small>)}
        {Array.from({ length: 42 }, (_, index) => {
          const day = shiftDay(start, index)
          const count = dayEntries(data, day).length
          return <button className={day.slice(0, 7) !== month.slice(0, 7) ? 'outside' : ''} key={day}
            aria-pressed={day === selected} aria-current={day === today ? 'date' : undefined}
            aria-label={`${fromDay(day).toLocaleDateString('ru-RU', longDate)} · карточек: ${count}`}
            onClick={() => { setMonth(day.slice(0, 7) + '-01'); setSelected(day) }}>
            {Number(day.slice(8))}{count > 0 && <span>{count}</span>}
          </button>
        })}
      </div>
    </section>
    <div className="calendar-aside">
      <section className="widget">
        <header><span className="widget-icon"><CalendarDays size={19} /></span><h2>{shortDate(selected)}</h2></header>
        {entries.length ? entries.map(entry => <Row key={entry.id} data={data} entry={entry} open={open}
          detail={[firstValue(entry, 'time'), kindName[kindOf(data, entry)]].filter(Boolean).join(' · ')} />) :
          <p className="widget-empty">На эту дату нет карточек.</p>}
        <button disabled={!data.places.length} onClick={() => create(selected)}><Plus size={17} /> Карточка на эту дату</button>
      </section>
      <section className="widget">
        <header><span className="widget-icon"><Bell size={19} /></span><h2>Напоминания</h2></header>
        <p className="widget-empty">Срабатывают, пока органайзер открыт.</p>
        {notification === 'default' && <button onClick={() => void Notification.requestPermission().then(setNotification,
          () => setNotification('unsupported'))}>Разрешить уведомления</button>}
        <small>{notification === 'granted' ? 'Уведомления браузера разрешены.' :
          notification === 'denied' ? 'Уведомления запрещены в настройках браузера.' :
          notification === 'unsupported' ? 'Напоминания доступны внутри органайзера.' : ''}</small>
      </section>
    </div>
  </div>
}

export function Finance({ data, open }: { data: Data; open: (entry: Entry) => void }) {
  const [start, setStart] = useState('')
  const [end, setEnd] = useState('')
  const items = operations(data).filter(item => (!start || item.day >= start) && (!end || (item.day && item.day <= end)))
  const totals = financeTotals(items)
  return <section className="widget finance-panel" aria-label="Финансовая сводка">
    <header><span className="widget-icon"><Wallet size={19} /></span><h2>Сводка операций</h2></header>
    <div className="finance-totals">
      {[[ 'Баланс', totals.balance], ['Доходы', totals.income], ['Расходы', totals.expenses], ['В плане', totals.planned]].map(([label, amount]) =>
        <div key={String(label)}><small>{label}</small><strong>{money(amount as bigint)}</strong></div>)}
    </div>
    <div className="finance-filter">
      <label>С даты<input type="date" value={start} max={end || undefined} onChange={event => setStart(event.target.value)} /></label>
      <label>По дату<input type="date" value={end} min={start || undefined} onChange={event => setEnd(event.target.value)} /></label>
      <button disabled={!items.length} onClick={() => downloadFile(new Blob([financeCsv(items)], { type: 'text/csv;charset=utf-8' }), 'pomogator-finance.csv')}>
        <Download size={17} /> Экспорт CSV
      </button>
    </div>
    {totals.categories.length > 0 && <div className="finance-categories"><h3>Расходы по категориям</h3>
      {totals.categories.map(([category, amount]) => <div key={category}>
        <span>{category}</span><small>{money(amount)}</small>
        <progress value={Number(amount * 10000n / totals.expenses)} max={10000} aria-label={category} />
      </div>)}</div>}
    {items.length ? <div className="summary-list">{items.map(item => <Row key={item.entry.id} data={data} entry={item.entry} open={open}
      detail={[item.day && shortDate(item.day), item.category, money(item.amount), item.planned && 'В плане'].filter(Boolean).join(' · ')} />)}</div> :
      <p className="widget-empty">Операций за выбранный период нет.</p>}
  </section>
}
