import { useEffect, useRef, useState } from 'react'
import { ArrowDown, ArrowUp, Plus, Star, Trash2, Undo2 } from 'lucide-react'
import { AttachmentView } from './attachments'
import { validateBlocks } from './backup'
import { blockNames, blocksOf, createCard, firstValue, kindName, kinds, kindOf, makeBlock, priorities, priorityName,
  reorder, repeatName, repeatValues, stateName, states, type Block, type BlockType, type Kind } from './cards'
import { Dialog, formatTime } from './editors'
import { icons, pathTo, type Data, type Entry } from './model'

const formats = { paragraph: 'Абзац', heading: 'Заголовок', list: 'Список' } as const

function BlockEditor({ block, cards, change }: { block: Block; cards: Entry[]; change: (block: Block) => void }) {
  switch (block.type) {
    case 'text': return <>
      <label>Формат<select value={block.format} onChange={event => change({ ...block, format: event.target.value as typeof block.format })}>
        {Object.entries(formats).map(([value, name]) => <option key={value} value={value}>{name}</option>)}
      </select></label>
      <label>Текст<textarea rows={4} value={block.value} onChange={event => change({ ...block, value: event.target.value })} /></label>
    </>
    case 'link': return <div className="form-columns">
      <label>Адрес ссылки<input type="url" value={block.value} onChange={event => change({ ...block, value: event.target.value.trim() })} /></label>
      <label>Подпись ссылки<input value={block.label} onChange={event => change({ ...block, label: event.target.value })} /></label>
    </div>
    case 'checklist': return <>
      {block.items.map((item, index) => <div className="check-edit" key={index}>
        <input type="checkbox" aria-label={`Отметить пункт ${index + 1}`} checked={item.done}
          onChange={() => change({ ...block, items: block.items.map((row, i) => i === index ? { ...row, done: !row.done } : row) })} />
        <input aria-label={`Пункт ${index + 1}`} value={item.text}
          onChange={event => change({ ...block, items: block.items.map((row, i) => i === index ? { ...row, text: event.target.value } : row) })} />
        <button type="button" className="icon-button" aria-label={`Удалить пункт ${index + 1}`} onClick={() =>
          change({ ...block, items: block.items.filter((_, i) => i !== index) })}><Trash2 size={16} /></button>
      </div>)}
      <button type="button" onClick={() => change({ ...block, items: [...block.items, { text: '', done: false }] })}><Plus size={16} /> Пункт</button>
    </>
    case 'table': {
      const width = block.rows[0].length
      return <>
        <div className="table-scroll"><table className="editable-table"><tbody>
          {block.rows.map((row, y) => <tr key={y}>{row.map((cell, x) => <td key={x}>
            <input aria-label={`Строка ${y + 1}, столбец ${x + 1}`} value={cell} onChange={event => change({ ...block,
              rows: block.rows.map((line, i) => i === y ? line.map((value, j) => j === x ? event.target.value : value) : line) })} />
          </td>)}<td><button type="button" className="icon-button" disabled={block.rows.length < 2}
            aria-label={`Удалить строку ${y + 1}`} onClick={() => change({ ...block, rows: block.rows.filter((_, i) => i !== y) })}><Trash2 size={16} /></button></td></tr>)}
        </tbody></table></div>
        <div className="tools">
          <button type="button" onClick={() => change({ ...block, rows: [...block.rows, Array<string>(width).fill('')] })}>Добавить строку</button>
          <button type="button" onClick={() => change({ ...block, rows: block.rows.map(row => [...row, '']) })}>Добавить столбец</button>
          <button type="button" disabled={width < 2} onClick={() => change({ ...block, rows: block.rows.map(row => row.slice(0, -1)) })}>Удалить последний столбец</button>
        </div>
      </>
    }
    case 'file': case 'image': return <>
      <label>Выбрать {block.type === 'image' ? 'изображение' : 'файл'}<input type="file" accept={block.type === 'image' ? 'image/*' : undefined}
        onChange={event => {
          const file = event.target.files?.[0]
          if (file) change({ ...block, name: file.name, file: file.slice(0, file.size, file.type) })
          event.target.value = ''
        }} /></label>
      <label>Описание<input value={block.description} onChange={event => change({ ...block, description: event.target.value })} /></label>
      <AttachmentView block={block} preview />
    </>
    case 'contact': return <>
      <label>Имя<input value={block.name} onChange={event => change({ ...block, name: event.target.value })} /></label>
      <div className="form-columns">
        <label>Электронная почта<input type="email" value={block.email} onChange={event => change({ ...block, email: event.target.value })} /></label>
        <label>Телефон<input type="tel" value={block.phone} onChange={event => change({ ...block, phone: event.target.value })} /></label>
      </div>
    </>
    case 'status': return <label>Статус<select value={block.value}
      onChange={event => change({ ...block, value: event.target.value as typeof block.value })}>
      {states.map(value => <option key={value} value={value}>{stateName[value]}</option>)}
    </select></label>
    case 'relation': return <label>Связанная карточка<select value={block.value}
      onChange={event => change({ ...block, value: event.target.value })}>
      <option value="">Выберите карточку</option>
      {block.value && !cards.some(item => item.id === block.value) && <option value={block.value}>Карточка недоступна</option>}
      {cards.map(item => <option key={item.id} value={item.id}>{item.title}</option>)}
    </select></label>
    default: {
      const type = block.type === 'reminder' ? 'datetime-local' : block.type === 'date' || block.type === 'time' ? block.type : 'text'
      return <label>{blockNames[block.type]}{block.type === 'amount' ? ' (₽)' : ''}
        <input type={type} inputMode={block.type === 'amount' ? 'decimal' : undefined} value={block.value}
          onChange={event => change({ ...block, value: block.type === 'amount' ? event.target.value.replace(',', '.') : event.target.value })} /></label>
    }
  }
}

export function CardEditor({ entry, editing, data, persist, close, child }: {
  entry: Entry; editing: boolean; data: Data; close: () => void; child: (entry: Entry) => void;
  persist: (value: Entry, editing: boolean, revision: number, generation?: string) => Promise<Entry>
}) {
  const [draft, setDraft] = useState<Entry>(() => ({ ...entry, kind: kindOf(data, entry), blocks: blocksOf(entry) }))
  const generation = useRef(data.generation)
  const [saved, setSaved] = useState(draft)
  const [record, setRecord] = useState(editing ? entry : undefined)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [blockType, setBlockType] = useState<BlockType>('text')
  const [removed, setRemoved] = useState<{ block: Block; index: number }>()
  const [submitting, setSubmitting] = useState(false)
  const pending = useRef<Promise<boolean> | null>(null)
  const savedRef = useRef(saved)
  const recordRef = useRef(record)
  const latest = useRef(draft)
  const persists = useRef(persist)
  persists.current = persist
  const dirty = saved !== draft
  const changed = (patch: Partial<Entry>) => {
    const next = { ...latest.current, ...patch }
    latest.current = next
    setDraft(next)
    setError('')
  }

  async function save(value: Entry) {
    if (pending.current && !await pending.current) return false
    if (value === savedRef.current) return true
    const invalid = !value.title.trim() ? 'Введите название карточки.' : validateBlocks(blocksOf(value))
    if (invalid) { setError(invalid); return false }
    setSaving(true)
    setError('')
    const operation = (async () => {
      try {
        const next = await persists.current({ ...value, title: value.title.trim(), category: value.category?.trim() },
          Boolean(recordRef.current), recordRef.current?.revision ?? 0, generation.current)
        savedRef.current = value
        recordRef.current = next
        setSaved(value)
        setRecord(next)
        return true
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'Не удалось сохранить карточку.')
        return false
      } finally {
        pending.current = null
        setSaving(false)
      }
    })()
    pending.current = operation
    return operation
  }

  useEffect(() => {
    if (!dirty || !draft.title.trim() || error || saving || submitting) return
    const timer = setTimeout(() => void save(draft), 600)
    return () => clearTimeout(timer)
  }, [draft, dirty, error, saving, submitting])

  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  async function finish(next: () => void, allowDiscard = false) {
    if (submitting) return
    setSubmitting(true)
    if (await save(latest.current) || (allowDiscard && window.confirm('Закрыть карточку и потерять несохранённые изменения?'))) next()
    else setSubmitting(false)
  }

  const blocks = blocksOf(draft)
  const cards = data.entries.filter(item => item.id !== entry.id)
  const kind = draft.kind!
  const supportsParent = kind === 'project' || kind === 'task'
  const supportsFinance = kind === 'payment' || kind === 'purchase'
  const parents = cards.filter(item => kindOf(data, item) === kind)
  const children = cards.filter(item => item.parentCard === draft.id)
  return <Dialog className="card-dialog" title={editing ? 'Карточка' : 'Новая карточка'} close={() => void finish(close, true)}>
    <form inert={submitting} onSubmit={event => { event.preventDefault(); void finish(close) }}>
      <div className="card-editor-top">
        <label>Название<input autoFocus required value={draft.title} onChange={event => changed({ title: event.target.value })} /></label>
        <button type="button" aria-label="В избранном" aria-pressed={draft.favorite === true} onClick={() => changed({ favorite: !draft.favorite })}>
          <Star size={19} fill={draft.favorite ? 'currentColor' : 'none'} /></button>
      </div>
      <div className="form-columns">
        <label>Тип карточки<select value={kind} onChange={event => {
          const type = event.target.value as Kind
          changed({ kind: type, parentCard: undefined, blocks: !record && draft.blocks === saved.blocks
            ? createCard(type, draft.parent, firstValue(draft, 'date')).blocks : draft.blocks })
        }}>{kinds.map(value => <option key={value} value={value}>{kindName[value]}</option>)}</select></label>
        <label>Папка<select value={draft.parent} onChange={event => changed({ parent: event.target.value })}>
          {data.places.map(place => <option key={place.id} value={place.id}>{pathTo(data, place.id).map(item => item.title).join(' / ')}</option>)}
        </select></label>
      </div>
      <details className="editor-options">
        <summary>Свойства карточки</summary>
        <div className="form-columns">
          <label>Категория<input value={draft.category ?? ''} onChange={event => changed({ category: event.target.value })} /></label>
          <label>Иконка<select value={draft.icon ?? ''} onChange={event => changed({ icon: (event.target.value || undefined) as Entry['icon'] })}>
            <option value="">По типу карточки</option>
            {icons.map((value, index) => <option key={value} value={value}>{['Проект', 'Кошелёк', 'Список дел', 'Блокнот', 'Документ', 'Сервисы', 'Папка', 'Звезда'][index]}</option>)}
          </select></label>
          <label>Приоритет<select value={draft.priority ?? 'normal'} onChange={event => changed({ priority: event.target.value as Entry['priority'] })}>
            {priorities.map(value => <option key={value} value={value}>{priorityName[value]}</option>)}
          </select></label>
          <label>Повтор<select value={draft.repeat ?? 'none'} onChange={event => changed({ repeat: event.target.value as Entry['repeat'] })}>
            {repeatValues.map(value => <option key={value} value={value}>{repeatName[value]}</option>)}
          </select></label>
          {supportsFinance && <label>Операция<select value={draft.direction ?? (Number(firstValue(draft, 'amount')) > 0 ? 'income' : 'expense')} onChange={event =>
            changed({ direction: event.target.value as Entry['direction'] })}><option value="expense">Расход</option><option value="income">Доход</option></select></label>}
          {supportsParent && <label>{kind === 'task' ? 'Родительская задача' : 'Родительский проект'}
            <select value={draft.parentCard ?? ''} onChange={event => changed({ parentCard: event.target.value || undefined })}>
              <option value="">Нет</option>
              {draft.parentCard && !parents.some(item => item.id === draft.parentCard) && <option value={draft.parentCard}>Карточка недоступна</option>}
              {parents.map(item => <option key={item.id} value={item.id}>{item.title}</option>)}
            </select></label>}
        </div>
        {supportsParent && record && <button type="button" onClick={() => void finish(() => child({ ...latest.current, kind }))}>
          <Plus size={17} /> {kind === 'task' ? 'Добавить подзадачу' : 'Добавить подпроект'}
        </button>}
        {children.length > 0 && <ul>{children.map(item => <li key={item.id}>{item.title}</li>)}</ul>}
      </details>
      <div className="block-list">
        {blocks.map((block, index) => <section className="block-editor" key={block.id} aria-label={blockNames[block.type]}>
          <div className="block-heading"><strong>{blockNames[block.type]}</strong><div className="tools">
            <button className="icon-button" type="button" disabled={index === 0} aria-label={`Поднять блок «${blockNames[block.type]}»`} onClick={() => changed({ blocks: reorder(blocks, index, -1) })}><ArrowUp size={17} /></button>
            <button className="icon-button" type="button" disabled={index === blocks.length - 1} aria-label={`Опустить блок «${blockNames[block.type]}»`} onClick={() => changed({ blocks: reorder(blocks, index, 1) })}><ArrowDown size={17} /></button>
            <button className="icon-button" type="button" aria-label={`Удалить блок «${blockNames[block.type]}»`} onClick={() => {
              setRemoved({ block, index })
              changed({ blocks: blocks.filter(item => item.id !== block.id) })
            }}><Trash2 size={17} /></button>
          </div></div>
          <BlockEditor block={block} cards={cards} change={value => changed({ blocks: blocks.map(item => item.id === value.id ? value : item) })} />
        </section>)}
      </div>
      <div className="add-block">
        <select aria-label="Тип блока" value={blockType} onChange={event => setBlockType(event.target.value as BlockType)}>
          {Object.entries(blockNames).map(([value, name]) => <option key={value} value={value}>{name}</option>)}
        </select>
        <button type="button" onClick={() => changed({ blocks: [...blocks, makeBlock(blockType)] })}><Plus size={17} /> Добавить блок</button>
        {removed && <button type="button" onClick={() => {
          const restored = [...blocks]
          restored.splice(removed.index, 0, removed.block)
          changed({ blocks: restored })
          setRemoved(undefined)
        }}><Undo2 size={17} /> Вернуть блок</button>}
      </div>
      {record && <details className="editor-options"><summary>История изменений</summary>
        <p className="metadata">Создано {formatTime(record.created)} · Изменено {formatTime(record.updated)}</p>
        {record.history?.length ? <ul className="history">{[...record.history].reverse().map((item, index) =>
          <li key={index}>{formatTime(item.at)} — {item.note}</li>)}</ul> : null}
      </details>}
      {error && <p className="error" role="alert">{error}</p>}
      <div className="editor-footer">
        <span className="muted" role="status">{saving ? 'Сохранение…' : error ? 'Есть несохранённые изменения' :
          dirty ? 'Ожидает сохранения' : record ? 'Сохранено в этом браузере' : 'Введите название для автосохранения'}</span>
        <button type="submit" className="primary" disabled={submitting || !draft.title.trim()}>Готово</button>
      </div>
    </form>
  </Dialog>
}
