import { Check, ExternalLink, Pencil, Star, Trash2 } from 'lucide-react'
import { AttachmentView } from './attachments'
import { blocksOf, cardStatus, kindIcon, kindName, kindOf, priorities, priorityName, progress, stateName, tagsOf, type Block } from './cards'
import { Glyph } from './editors'
import { safeLink, type Data, type Entry } from './model'
import { deadlineState, fromDay } from './schedule'

const deadlineLabel = { done: 'Выполнено', late: 'Просрочено', today: 'Сегодня', soon: 'Скоро', upcoming: 'Предстоит' }
export const shortDate = (day: string) => fromDay(day).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })

function Body({ block, data, open, toggle }: { block: Block; data: Data; open: (entry: Entry) => void; toggle: (id: string, index: number) => void }) {
  switch (block.type) {
    case 'text':
      if (!block.value.trim()) return null
      if (block.format === 'heading') return <h4>{block.value}</h4>
      if (block.format === 'list') return <ul className="text-list">
        {block.value.split('\n').filter(line => line.trim()).map((line, index) => <li key={index}>{line}</li>)}
      </ul>
      return <p className="entry-text">{block.value}</p>
    case 'link': return null
    case 'checklist': return <div>{block.items.map((item, index) => item.text.trim() && <label className="check-item" key={index}>
      <input type="checkbox" checked={item.done} onChange={() => toggle(block.id, index)} /><span>{item.text}</span>
    </label>)}</div>
    case 'table': return block.rows.some(row => row.some(value => value)) ? <div className="table-scroll">
      <table><tbody>{block.rows.map((row, y) => <tr key={y}>{row.map((value, x) => <td key={x}>{value}</td>)}</tr>)}</tbody></table>
    </div> : null
    case 'file': case 'image': return <AttachmentView block={block} preview />
    case 'contact': return (block.name || block.email || block.phone) && <p>{[block.name, block.email, block.phone].filter(Boolean).join(' · ')}</p>
    case 'relation': {
      const other = data.entries.find(item => item.id === block.value)
      return other ? <button className="related" onClick={() => open(other)}>Связь · {other.title}</button> :
        block.value && <p className="detail">Связанная карточка недоступна</p>
    }
    case 'date': return block.value && <p className="detail">Дата · {shortDate(block.value)}</p>
    case 'time': return block.value && <p className="detail">Время · {block.value}</p>
    case 'reminder': return block.value && <p className="detail">Напоминание · {shortDate(block.value.slice(0, 10))} {block.value.slice(11)}</p>
    case 'amount': return block.value && <p className="detail">Сумма · {block.value.replace('.', ',')} ₽</p>
    case 'status': case 'tag': return null
  }
}

export function CardLinks({ blocks, payment = false }: { blocks: Block[]; payment?: boolean }) {
  const links = blocks.filter((block): block is Extract<Block, { type: 'link' }> => block.type === 'link' && safeLink(block.value))
  if (!links.length) return null
  return <div className="card-links">{links.map(link => {
    const label = link.label.trim()
    return <a className="card-link" key={link.id} href={link.value} target="_blank" rel="noopener noreferrer">
      <ExternalLink size={17} aria-hidden="true" />
      <span>{payment ? <><strong>{label || 'Сайт оплаты'}</strong><small>{link.value}</small></> : label || link.value}</span>
    </a>
  })}</div>
}

export function CardView({ entry, data, today, open, remove, toggle, complete, favorite }: {
  entry: Entry; data: Data; today: string; open: (entry: Entry) => void; remove: () => void;
  toggle: (id: string, index: number) => void; complete: () => void; favorite: () => void
}) {
  const kind = kindOf(data, entry)
  const status = cardStatus(entry)
  const tags = tagsOf(entry)
  const blocks = blocksOf(entry)
  const isDeadline = kind === 'task' || kind === 'project' || kind === 'payment'
  const dates = isDeadline ? blocks.flatMap(block => block.type === 'date' && block.value ? [block] : []) : []
  const children = data.entries.filter(item => item.parentCard === entry.id)
  const parent = data.entries.find(item => item.id === entry.parentCard)
  return <article className="entry glass-surface">
    <div className="entry-heading">
      <span className={`tile tile-small tone-${entry.icon ?? kindIcon[kind]}`}><Glyph icon={entry.icon ?? kindIcon[kind]} /></span>
      <div className="entry-title"><small>{kindName[kind]}</small><h3>{entry.title}</h3></div>
      <div className="tools">
        <button className="icon-button" aria-label={`Избранное «${entry.title}»`} aria-pressed={Boolean(entry.favorite)} onClick={favorite}>
          <Star size={17} fill={entry.favorite ? 'currentColor' : 'none'} /></button>
        <button className="icon-button" aria-label={`Редактировать «${entry.title}»`} onClick={() => open(entry)}><Pencil size={17} /></button>
        <button className="icon-button" aria-label={`Удалить «${entry.title}»`} onClick={remove}><Trash2 size={17} /></button>
      </div>
    </div>
    <div className="tags">
      {(kind === 'task' || kind === 'project' || blocksOf(entry).some(block => block.type === 'status')) &&
        <span className={`tag status-${status}`}>{stateName[status]}</span>}
      {entry.priority && entry.priority !== priorities[0] && <span className="tag">{priorityName[entry.priority]} приоритет</span>}
      {entry.category && <span className="tag">{entry.category}</span>}
      {tags.map((tag, index) => <span className="tag" key={index}>#{tag}</span>)}
    </div>
    {dates.map(({ id, value }) => {
      const state = deadlineState(value, today, status === 'done')
      const label = kind === 'payment' && state === 'done' ? 'Оплачено' : deadlineLabel[state]
      return <p className={`deadline deadline-${state}`} key={id}>
        <time dateTime={value}>{kind === 'payment' ? 'Дата платежа' : 'Срок'} · {shortDate(value)}</time>
        <strong>{label}</strong>
      </p>
    })}
    <CardLinks blocks={blocks} payment={kind === 'payment'} />
    {kind === 'project' && <div className="card-progress"><progress value={progress(entry)} max={100} aria-label="Выполнение проекта" />
      <small>{progress(entry)}%</small></div>}
    <div className="card-body">{blocks.filter(block => block.type !== 'link' && (!isDeadline || block.type !== 'date')).map(block => <Body key={block.id} block={block} data={data} open={open} toggle={toggle} />)}</div>
    {parent && <button className="related" onClick={() => open(parent)}>В составе · {parent.title}</button>}
    {children.length > 0 && <details className="editor-options"><summary>{kind === 'project' ? 'Подпроекты' : 'Подзадачи'} · {children.length}</summary>
      <div className="related-list">{children.map(child => <button className="related" key={child.id} onClick={() => open(child)}>{child.title}</button>)}</div>
    </details>}
    {(kind === 'project' || kind === 'task') && <div className="actions">
      <button onClick={complete}><Check size={17} /> {status === 'done' ? 'Вернуть в работу' : 'Завершить'}</button>
    </div>}
  </article>
}
