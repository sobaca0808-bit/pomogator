import { describe, expect, it } from 'vitest'
import { inboxFor, saveCapture, withInbox } from './capture'
import { exportBackup, importBackup } from './backup'
import { blocksOf } from './cards'
import { seed } from './model'

describe('quick capture', () => {
  it('adds an idempotent inbox to an old backup without changing existing folders or cards', async () => {
    const old = seed()
    const legacy = importBackup(JSON.stringify(old))
    const imported = withInbox(legacy)
    expect(imported.places.slice(0, -1)).toEqual(old.places)
    expect(withInbox(imported)).toBe(imported)
    const collision = { ...old, places: [...old.places, { ...inboxFor(old), title: 'Моя папка' }] }
    expect(withInbox(collision).places.slice(0, -1)).toEqual(collision.places)
    expect(withInbox(importBackup(await exportBackup(imported))).places).toEqual(imported.places)
  })

  it('round trips a task and its photo through a backup', async () => {
    const data = saveCapture(seed(), { kind: 'task', text: 'Позвонить\nПосле обеда', parent: '',
      file: new File([new Uint8Array([1, 3, 5])], 'photo.png', { type: 'image/png' }) })
    const copy = importBackup(await exportBackup(data))
    const [entry] = copy.entries
    expect(entry).toMatchObject({ title: 'Позвонить', parent: inboxFor(copy).id, kind: 'task', text: 'Позвонить\nПосле обеда' })
    const attachment = blocksOf(entry).find(block => block.type === 'image')
    if (attachment?.type !== 'image') throw new Error('Photo missing after import.')
    expect(attachment.name).toBe('photo.png')
    expect(new Uint8Array(await attachment.file!.arrayBuffer())).toEqual(new Uint8Array([1, 3, 5]))
  })

  it('allows file-only notes, respects a selected folder and rejects missing contents or destinations', () => {
    const data = seed()
    const draft = { kind: 'note' as const, text: '', parent: 'docs', file: new File(['контент'], 'a.txt', { type: 'text/plain' }) }
    expect(saveCapture(data, draft).entries[0]).toMatchObject({ title: 'a.txt', parent: 'docs', kind: 'note' })
    expect(() => saveCapture(data, { ...draft, file: undefined })).toThrow('Введите текст')
    expect(() => saveCapture(data, { ...draft, parent: 'missing' })).toThrow('уже удалены')
    expect(data.entries).toHaveLength(0)
  })
})
