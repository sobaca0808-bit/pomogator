import { expect, test } from 'vitest'
import { exportBackup, importBackup } from './backup'
import { seed } from './model'
import { moveShortcut, removeShortcut, saveShortcut, shortcutUrl, type Shortcut } from './shortcuts'

const telegram: Shortcut = { id: 'first', title: ' Telegram ', url: 'https://t.me/sample', icon: 'telegram' }
const custom: Shortcut = { id: 'second', title: 'Мой сайт', url: 'https://example.org/', icon: 'custom' }

test('ярлыки создаются, изменяются, сортируются и удаляются без изменения исходной базы', () => {
  const initial = seed()
  const saved = saveShortcut(saveShortcut(initial, telegram), custom)
  expect(initial.shortcuts).toBeUndefined()
  const first = saved.shortcuts![0]
  expect(first.title).toBe('Telegram')
  const renamed = saveShortcut(saved, { ...first, title: 'Канал', url: 'https://t.me/another' }, first)
  const reordered = moveShortcut(renamed, custom.id, -1)
  expect(reordered.shortcuts?.map(item => item.title)).toEqual(['Мой сайт', 'Канал'])
  expect(() => removeShortcut(renamed, first)).toThrow(/изменился/)
  expect(removeShortcut(reordered, custom).shortcuts).toEqual([renamed.shortcuts![0]])
  expect(initial.places).toEqual(reordered.places)
})

test('резервные копии сохраняют порядок, прежние форматы открываются, небезопасные ярлыки отклоняются', async () => {
  const original = seed()
  expect(importBackup(JSON.stringify(original)).shortcuts).toBeUndefined()
  expect(importBackup(await exportBackup(original)).shortcuts).toBeUndefined()
  const reordered = moveShortcut(saveShortcut(saveShortcut(original, telegram), custom), custom.id, -1)
  const json = await exportBackup(reordered)
  expect(importBackup(json).shortcuts).toEqual(reordered.shortcuts)
  const backup = JSON.parse(json)
  for (const value of ['javascript:alert(1)', 'intent://example.org/', 'file:///test.exe', 'http://example.org/',
    'https://account:password@example.org/', 'https://example.org/a b']) {
    expect(shortcutUrl(value)).toBeUndefined()
    expect(() => importBackup(JSON.stringify({ ...backup, shortcuts: [{ ...custom, url: value }] }))).toThrow(/недопустимый ярлык/)
  }
  expect(() => importBackup(JSON.stringify({ ...backup, shortcuts: [custom, custom] }))).toThrow(/одинаковыми/)
  expect(() => importBackup(JSON.stringify({ ...backup, shortcuts: [{ ...custom, icon: 'constructor' }] }))).toThrow(/недопустимый ярлык/)
})
