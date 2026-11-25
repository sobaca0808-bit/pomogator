import { expect, test } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import { join } from 'node:path'
import { navigateSection } from './navigation-support'
import { createCard } from '../src/cards'
import { seed } from '../src/model'

for (const width of [390, 1366]) {
  test(`быстрый захват, перенос и резервирование · ${width}px`, async ({ browser, baseURL }, info) => {
    test.setTimeout(70_000)
    const touch = width <= 390
    const context = await browser.newContext({ viewport: { width, height: 844 }, isMobile: touch, hasTouch: touch,
      reducedMotion: 'reduce', locale: 'ru-RU' })
    const page = await context.newPage()
    const button = (name: string | RegExp) => page.getByRole('button', { name, exact: typeof name === 'string' })
    const dialog = page.getByRole('dialog')
    const capture = () => touch ? button('Быстрый захват').tap() : button('Быстрый захват').click()
    const screen = (name: string) => page.screenshot({ path: join(process.env.POMOGATOR_SCREENSHOTS || info.outputDir, `capture-${width}-${name}.png`) })
    async function restore(input: Parameters<ReturnType<typeof page.locator>['setInputFiles']>[0]) {
      await button('Настройки').click()
      await page.locator('.app-settings input[type=file]').setInputFiles(input)
      await button('Подтвердить').click()
      await expect(page.getByRole('heading', { name: 'Обзор', exact: true })).toBeVisible()
    }
    try {
      await page.goto(baseURL!)
      const old = seed()
      old.entries.push({ ...createCard('project', 'projects'), title: 'Поездка', blocks: undefined,
        created: '2026-11-20T12:00:00.000Z', updated: '2026-11-20T12:00:00.000Z' })
      await restore({ name: 'v1.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(old)) })
      await expect(button(/Входящие\s*0/)).toBeVisible()
      await capture()
      await expect(dialog).toHaveClass(/glass-surface/)
      await dialog.getByLabel('Текст', { exact: true }).fill('Взять билеты\nУтром')
      await dialog.getByLabel('Тип записи').selectOption('task')
      await button('Закрыть').click()
      await page.reload()
      await capture()
      await expect(dialog.getByLabel('Текст', { exact: true })).toHaveValue('Взять билеты\nУтром')
      await expect(dialog.getByLabel('Тип записи')).toHaveValue('task')
      await dialog.locator('input[type=file]').setInputFiles({ name: 'plan.txt', mimeType: 'text/plain', buffer: Buffer.from('План поездки') })
      await screen('dark')
      await button('Сохранить запись').click()
      await expect(dialog).toHaveCount(0)
      await page.reload()
      await button(/Входящие\s*1/).click()
      await expect(page.getByRole('heading', { name: 'Взять билеты', exact: true })).toBeVisible()
      await expect(button('Скачать plan.txt')).toBeVisible()
      await screen('inbox')
      await button('Редактировать «Взять билеты»').click()
      await dialog.getByRole('combobox', { name: 'Проект', exact: true }).selectOption({ label: 'Поездка' })
      await button('Готово').click()
      await expect(dialog).toHaveCount(0)
      await expect(button(/Входящие\s*0/)).toBeVisible()
      await navigateSection(page, 'Мои проекты')
      await expect(page.getByRole('heading', { name: 'Взять билеты', exact: true })).toBeVisible()
      await expect(button('В составе · Поездка')).toBeVisible()

      await button('Настройки').click()
      await page.getByRole('checkbox', { name: 'Светлая тема' }).check()
      const backup = page.waitForEvent('download')
      await button('Экспорт JSON').click()
      const download = await backup
      const file = await download.path()
      expect(JSON.parse(await readFile(file, 'utf8')).entries).toHaveLength(2)
      await restore(file)
      await navigateSection(page, 'Мои проекты')
      await button('Просмотреть текст').click()
      await expect(page.getByText('План поездки', { exact: true })).toBeVisible()
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
      await capture()
      await dialog.getByLabel('Текст', { exact: true }).fill('Быстрая заметка')
      await dialog.getByLabel('Куда сохранить').selectOption('notes')
      await screen('light')
      await button('Сохранить запись').click()
      await expect(dialog).toHaveCount(0)
      await expect(button(/Входящие\s*0/)).toBeVisible()
      await navigateSection(page, 'Заметки')
      await expect(page.getByRole('heading', { name: 'Быстрая заметка', exact: true })).toBeVisible()
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width)
    } finally { await context.close() }
  })
}

test('ошибка IndexedDB оставляет быстрый черновик и разрешает повторное сохранение', async ({ page }) => {
  await page.addInitScript(() => {
    const put = IDBObjectStore.prototype.put
    let rejectOnce = true
    IDBObjectStore.prototype.put = function (...args) {
      if (rejectOnce && args[1] === 'data' && args[0]?.entries?.some((entry: { title: string }) => entry.title === 'Сохранить ещё раз')) {
        rejectOnce = false
        throw new DOMException('Тестовая ошибка', 'QuotaExceededError')
      }
      return put.apply(this, args)
    }
  })
  await page.goto('/')
  await page.getByRole('button', { name: 'Быстрый захват' }).click()
  const dialog = page.getByRole('dialog')
  const text = dialog.getByLabel('Текст', { exact: true })
  const save = dialog.getByRole('button', { name: 'Сохранить запись' })
  await text.fill('Сохранить ещё раз')
  await save.click()
  await expect(dialog.getByRole('alert')).toContainText('Тестовая ошибка')
  await expect(text).toHaveValue('Сохранить ещё раз')
  await save.click()
  await expect(dialog).toHaveCount(0)
  await page.getByRole('button', { name: /Входящие\s*1/ }).click()
  await expect(page.getByRole('heading', { name: 'Сохранить ещё раз' })).toHaveCount(1)
})
