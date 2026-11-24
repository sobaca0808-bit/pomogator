import { devices, expect, test, type Page } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { exportBackup } from '../src/backup'
import { createCard, makeBlock } from '../src/cards'
import { saveEntry, savePlace, seed } from '../src/model'

const button = (page: Page, name: string) => page.getByRole('button', { name, exact: true })
const heading = (page: Page, name: string) => page.getByRole('heading', { name, exact: true, level: 1 })
const settings = (page: Page) => button(page, 'Настройки')
const back = (page: Page) => page.getByRole('main').getByRole('button', { name: 'Назад', exact: true })

for (const touch of [false, true]) {
  test(`назад, прокрутка и защита редактора · ${touch ? 'сенсорный экран' : 'мышь'}`, async ({ browser, baseURL }) => {
    const context = await browser.newContext(touch ? devices['Pixel 7'] : { viewport: { width: 1000, height: 580 } })
    try {
      const page = await context.newPage()
      const activate = (element: ReturnType<Page['getByRole']>) => touch ? element.tap() : element.click()
      await page.goto(baseURL!)
      let data = savePlace(seed(), { id: 'nested', parent: 'notes', title: 'Вложенная папка', icon: 'folder' })
      for (let index = 1; index <= 28; index++)
        data = saveEntry(data, { ...createCard('note', 'nested'), title: 'Запись ' + index })
      await activate(settings(page))
      await page.locator('.app-settings input[type=file]').setInputFiles({
        name: 'notes.json', mimeType: 'application/json', buffer: Buffer.from(await exportBackup(data)),
      })
      await activate(button(page, 'Подтвердить'))
      await expect(heading(page, 'Обзор')).toBeVisible()
      await activate(button(page, 'Заметки'))
      await activate(page.getByRole('button', { name: /^Вложенная папка 0 пап/ }))
      await expect(heading(page, 'Вложенная папка')).toBeVisible()
      await page.goBack()
      await expect(heading(page, 'Заметки')).toBeVisible()
      await page.goForward()
      await expect(heading(page, 'Вложенная папка')).toBeVisible()

      const edit = button(page, 'Редактировать «Запись 28»')
      await edit.scrollIntoViewIfNeeded()
      const position = await page.evaluate(() => scrollY)
      expect(position).toBeGreaterThan(500)
      await activate(edit)
      const dialog = page.getByRole('dialog')
      await expect(dialog.getByRole('button', { name: 'Назад' })).toBeVisible()
      await dialog.getByLabel('Название').fill('')
      page.once('dialog', prompt => prompt.dismiss())
      await page.goBack()
      await expect(dialog.getByRole('alert')).toHaveText('Введите название карточки.')
      await expect(dialog.getByLabel('Название')).toHaveValue('')
      await dialog.getByLabel('Название').fill('Запись 28 обновлена')
      await activate(dialog.getByRole('button', { name: 'Назад' }))
      await expect(dialog).toHaveCount(0)
      await expect(button(page, 'Редактировать «Запись 28 обновлена»')).toBeVisible()
      expect(Math.abs(await page.evaluate(() => scrollY) - position)).toBeLessThan(110)
      const sticky = (await back(page).boundingBox())!
      expect(sticky.y).toBeGreaterThanOrEqual(0)
      expect(sticky.y).toBeLessThan(100)
      await activate(back(page))
      await expect(heading(page, 'Заметки')).toBeVisible()
    } finally { await context.close() }
  })
}

test('ссылка, сроки, обе темы и фактический номер сборки', async ({ page, context }) => {
  await page.clock.setFixedTime(new Date('2026-11-24T12:00:00Z'))
  await page.goto('/')
  let data = seed()
  for (const [title, day, done] of [
    ['Вчера', '2026-11-23', false], ['Сегодня', '2026-11-24', false], ['Завтра', '2026-11-25', false],
    ['Позже', '2026-12-04', false], ['Завершено', '2026-11-20', true],
  ] as const) {
    data = saveEntry(data, { ...createCard('task', 'tasks', day), title, blocks: [
      { ...makeBlock('date'), type: 'date', value: day }, { ...makeBlock('status'), type: 'status', value: done ? 'done' : 'planned' },
      { ...makeBlock('link'), type: 'link', value: 'https://example.org/test-link', label: 'Внешняя ссылка' },
    ] })
  }
  data = saveEntry(data, { ...createCard('payment', 'finance'), title: 'Оплаченный счёт', blocks: [
    { ...makeBlock('date'), type: 'date', value: '2026-11-15' },
    { ...makeBlock('status'), type: 'status', value: 'done' },
  ] })
  await settings(page).click()
  await page.locator('.app-settings input[type=file]').setInputFiles({
    name: 'deadlines.json', mimeType: 'application/json', buffer: Buffer.from(await exportBackup(data)),
  })
  await button(page, 'Подтвердить').click()
  await expect(heading(page, 'Обзор')).toBeVisible()
  await button(page, 'Мои дела').click()
  const card = (title: string) => page.getByRole('article').filter({ has: page.getByRole('heading', { name: title, exact: true }) })
  for (const [title, label] of [['Вчера', 'Просрочено'], ['Сегодня', 'Сегодня'], ['Завтра', 'Скоро'], ['Позже', 'Предстоит'], ['Завершено', 'Выполнено']])
    await expect(card(title).locator('.deadline strong')).toHaveText(label)
  const link = card('Сегодня').getByRole('link', { name: 'Внешняя ссылка' })
  await context.route('https://example.org/test-link', route => route.fulfill({ body: 'Проверка ссылки' }))
  const opened = page.waitForEvent('popup')
  await link.click()
  const target = await opened
  await expect(target).toHaveURL('https://example.org/test-link')
  await target.close()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await button(page, 'Финансы').click()
  await expect(card('Оплаченный счёт').locator('.deadline strong')).toHaveText('Оплачено')

  await settings(page).click()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await page.getByRole('checkbox', { name: 'Светлая тема' }).check()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
  await page.reload()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
  await button(page, 'Финансы').click()
  await button(page, 'Редактировать «Оплаченный счёт»').click()
  await expect(page.getByRole('dialog')).toHaveCSS('background-color', 'rgb(245, 248, 241)')
  await page.getByRole('dialog').getByRole('button', { name: 'Назад' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  const sha = execFileSync('git', ['rev-parse', '--short=7', 'HEAD'], { encoding: 'utf8' }).trim()
  await expect(page.getByRole('contentinfo')).toHaveText(`ПОМОГАТОР · v0.2.1 · build ${sha}`)
})
