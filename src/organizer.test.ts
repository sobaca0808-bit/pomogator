import 'fake-indexeddb/auto'
import { describe, expect, it } from 'vitest'
import { contents, deleteEntry, deletePlace, exportData, importData, orderPlace, pathTo,
  saveEntry, savePlace, search, seed, type Entry, type Place } from './model'
import { openStorage, transact } from './storage'

const folder = (id: string, parent: string): Place => ({ id, parent, title: id, icon: 'folder' })
const entry: Entry = {
  id: 'entry', parent: 'projects', title: 'Смета', text: 'Расходы на ремонт',
  links: ['https://example.com/plan'], checklist: [{ text: 'Проверить условия', done: false }],
  date: '2026-11-21', amount: '-10.25', created: '', updated: '',
}

describe('Органайзер', () => {
  it('создаёт, обновляет, переставляет и удаляет разделы', () => {
    const root: Place = { id: 'work', parent: null, title: 'Работа', icon: 'star' }
    let data = savePlace(seed(), root)
    data = savePlace(data, { ...root, title: 'Команда', icon: 'projects' }, true)
    data = orderPlace(data, root.id, -1)
    expect(data.places.at(-2)).toEqual({ ...root, title: 'Команда', icon: 'projects' })
    expect(deletePlace(data, root.id).places).toEqual(seed().places)
  })

  it('поддерживает глубокие папки и подтверждаемое каскадное удаление', () => {
    let data = seed()
    let parent = 'projects'
    for (let index = 0; index < 500; index++) {
      const id = `folder-${index}`
      data = savePlace(data, folder(id, parent))
      parent = id
    }
    data = saveEntry(data, { ...entry, parent })
    expect(pathTo(data, parent)).toHaveLength(501)
    expect(contents(data, 'projects').entries).toHaveLength(1)
    expect(() => deletePlace(data, 'projects')).toThrow()
    const after = deletePlace(data, 'projects', true)
    expect(after.places).toHaveLength(5)
    expect(after.entries).toEqual([])
  })

  it('создаёт, редактирует, перемещает и удаляет запись, сохраняя дату создания', () => {
    const created = saveEntry(seed(), entry)
    const first = created.entries[0]
    expect(first.created).toBe(first.updated)
    expect(new Date(first.created).toISOString()).toBe(first.created)
    const changed = saveEntry(created, { ...first, text: 'Правка', parent: 'finance' }, true)
    expect(changed.entries[0]).toMatchObject({ parent: 'finance', text: 'Правка', created: first.created })
    expect(() => saveEntry(changed, { ...first, parent: 'absent' }, true)).toThrow()
    expect(deleteEntry(changed, entry.id).entries).toEqual([])
  })

  it('ищет по названиям разделов, папок, записей и всем пользовательским полям', () => {
    const data = saveEntry(savePlace(seed(), folder('Черновики', 'projects')), entry)
    expect(search(data, ' МОИ ПРОЕКТЫ ').places[0].id).toBe('projects')
    expect(search(data, 'ЧЕРНОВИКИ').places[0].id).toBe('Черновики')
    for (const query of ['СМЕТА', 'РЕМОНТ', 'PLAN', 'УСЛОВИЯ', '2026-11', '-10.25']) {
      expect(search(data, query).entries[0].id).toBe(entry.id)
    }
    expect(search(data, ' ')).toEqual({ places: [], entries: [] })
  })

  it('восстанавливает все поля, порядок и связи из экспорта', () => {
    const data = saveEntry(savePlace(seed(), folder('home', 'finance')), { ...entry, parent: 'home' })
    expect(importData(exportData(data))).toEqual(data)
    expect(importData(exportData({ version: 1, places: [], entries: [] })).places).toEqual([])
  })

  it('отклоняет повреждённые данные, циклы, дубликаты и опасные ссылки', () => {
    const data = saveEntry(seed(), entry)
    expect(() => importData('{')).toThrow()
    for (const candidate of [
      { ...data, version: 2 },
      { ...data, places: [...data.places, data.places[0]] },
      { ...data, places: [...data.places, folder('orphan', 'missing')] },
      { ...data, places: [...data.places, folder('a', 'b'), folder('b', 'a')] },
      { ...data, entries: [{ ...data.entries[0], links: ['javascript:alert(1)'] }] },
      { ...data, entries: [{ ...data.entries[0], date: '2026-02-30' }] },
      { ...data, entries: [{ ...data.entries[0], checklist: [{ text: 'Пункт', done: 'yes' }] }] },
    ]) expect(() => importData(JSON.stringify(candidate))).toThrow()
  })
})

describe('IndexedDB', () => {
  it('создаёт стартовый набор один раз и восстанавливает изменения после переоткрытия', async () => {
    const db = await openStorage('persist')
    expect(await transact(db)).toEqual(seed())
    const saved = await transact(db, data => saveEntry(data, entry))
    db.close()
    const reopened = await openStorage('persist')
    expect(await transact(reopened)).toEqual(saved)
    reopened.close()
  })

  it('атомарно заменяет базу при импорте и не заполняет пустую базу повторно', async () => {
    const db = await openStorage('replace')
    const empty = importData('{"version":1,"places":[],"entries":[]}')
    await transact(db, () => empty)
    db.close()
    const reopened = await openStorage('replace')
    expect(await transact(reopened)).toEqual(empty)
    reopened.close()
  })

  it('откатывает ошибочные операции и сохраняет параллельные изменения', async () => {
    const db = await openStorage('atomic')
    const second = await openStorage('atomic')
    await expect(transact(db, data => saveEntry(data, { ...entry, parent: 'missing' }))).rejects.toThrow()
    expect(await transact(second)).toEqual(seed())
    await Promise.all([
      transact(db, data => saveEntry(data, entry)),
      transact(second, data => savePlace(data, folder('parallel', 'projects'))),
    ])
    const final = await transact(db)
    expect(final.entries).toHaveLength(1)
    expect(final.places.at(-1)?.id).toBe('parallel')
    db.close()
    second.close()
  })
})
