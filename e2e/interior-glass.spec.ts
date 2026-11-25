import { expect, test, type Locator, type Page } from '@playwright/test'
import { join } from 'node:path'
import { exportBackup } from '../src/backup'
import { createCard } from '../src/cards'
import { saveEntry, savePlace } from '../src/model'
import { demo } from './demo-data'
import { navigateSection } from './navigation-support'

const address = 'https://example.org/payment/2026-11'
const title = 'Домашний интернет'
const button = (page: Page, name: string) => page.getByRole('button', { name, exact: true })

for (const viewport of [{ width: 390, height: 844 }, { width: 1366, height: 960 }]) {
  test(`единые стеклянные поверхности и ссылки оплаты · ${viewport.width}px`, async ({ browser, baseURL }, testInfo) => {
    test.setTimeout(65_000)
    const touch = viewport.width < 500
    const context = await browser.newContext({ viewport, hasTouch: touch, isMobile: touch, reducedMotion: 'reduce', locale: 'ru-RU' })
    const activate = (target: Locator) => touch ? target.tap() : target.click()
    try {
      const page = await context.newPage()
      const heading = () => page.getByRole('heading', { level: 1 })
      const dialog = page.getByRole('dialog')
      await page.clock.setFixedTime(new Date('2026-11-23T12:00:00Z'))
      await page.goto(baseURL!)
      let data = demo()
      data = savePlace(data, { id: 'receipts', parent: 'finance', title: 'Счета и квитанции', icon: 'folder' })
      data = savePlace(data, { id: 'internet', parent: 'receipts', title: 'Интернет', icon: 'folder' })
      const original = data.entries.find(entry => entry.title === title)!
      data = saveEntry(data, { ...original, parent: 'internet', blocks: undefined, links: [address] }, true)
      data = saveEntry(data, { ...createCard('note', 'internet'), title: 'Порядок оплаты',
        text: '', blocks: [{ id: 'text', type: 'text', format: 'paragraph', value: 'Договор и реквизиты\n' + 'длиннаястрока'.repeat(48) }] })
      data = { ...data, shortcuts: [{ id: 'sample', title: 'Почта', url: 'https://example.org/mail', icon: 'mail' }] }
      await activate(button(page, 'Настройки'))
      await page.locator('.app-settings input[type=file]').setInputFiles({
        name: 'glass-demo.json', mimeType: 'application/json', buffer: Buffer.from(await exportBackup(data)),
      })
      await activate(button(page, 'Подтвердить'))
      await expect(heading()).toHaveText('Обзор')

      async function checkGlass(selector: string, capture?: string) {
        const surface = page.locator(selector).first()
        await expect(surface).toBeVisible()
        await expect(surface).toHaveClass(/glass-surface/)
        const screen = await surface.evaluate(element => ({
          background: getComputedStyle(element).backgroundColor,
          backdrop: getComputedStyle(document.querySelector('.shell')!, '::before').backgroundImage,
          width: document.documentElement.clientWidth,
          scrollWidth: document.documentElement.scrollWidth,
          fits: !(element instanceof HTMLDialogElement) || element.scrollWidth <= element.clientWidth + 1,
        }))
        expect(screen.background).toMatch(/^rgba\(/)
        expect(screen.backdrop).toContain('organizer-glass')
        expect(screen.scrollWidth).toBeLessThanOrEqual(screen.width)
        expect(screen.fits).toBe(true)
        if (capture) {
          const directory = process.env.POMOGATOR_SCREENSHOTS || testInfo.outputDir
          await page.screenshot({ path: join(directory, `glass-${viewport.width}-${capture}.png`), animations: 'disabled' })
        }
      }

      await checkGlass('.center-tile', '01-overview-dark')
      for (const section of ['Мои проекты', 'Мои дела', 'Заметки', 'Документы', 'Мои сервисы']) {
        await navigateSection(page, section, activate)
        await expect(heading()).toHaveText(section)
        await checkGlass('.workspace :is(.entry, .empty)')
      }
      await navigateSection(page, 'Календарь', activate)
      await checkGlass('.calendar', '02-calendar-dark')
      await activate(button(page, 'Предыдущий месяц'))
      await expect(page.locator('.calendar button[aria-pressed=true]')).toBeVisible()

      await navigateSection(page, 'Финансы', activate)
      await checkGlass('.finance-panel', '03-finance-dark')
      await expect(page.locator('.finance-panel').getByRole('link', { name: /Сайт оплаты/ })).toHaveAttribute('href', address)
      await activate(page.getByRole('button', { name: /^Счета и квитанции/ }))
      await activate(page.getByRole('button', { name: /^Интернет 0 пап/ }))
      await expect(heading()).toHaveText('Интернет')
      await checkGlass('.entries .entry', '04-nested-dark')
      const payCard = page.getByRole('article').filter({ has: page.getByRole('heading', { name: title }) })
      await expect(payCard.getByRole('link', { name: /Сайт оплаты/ })).toHaveAttribute('href', address)
      await activate(button(page, `Редактировать «${title}»`))
      await expect(dialog.getByLabel('Адрес сайта оплаты')).toHaveValue(address)
      await checkGlass('dialog[open]', '05-editor-dark')
      await activate(dialog.getByRole('button', { name: 'Назад', exact: true }))
      await expect(dialog).toHaveCount(0)

      await activate(button(page, 'Настройки'))
      await activate(page.getByRole('checkbox', { name: 'Светлая тема' }))
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
      await checkGlass('.app-settings', '06-settings-light')
      await page.reload()
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
      await checkGlass('.center-tile', '07-overview-light')
      await activate(button(page, 'Мой центр'))
      await checkGlass('.shortcut-card', '08-center-light')
      await activate(button(page, 'Добавить ярлык'))
      await checkGlass('dialog[open]', '09-shortcut-light')
      await dialog.getByLabel('Название').fill('Черновик ярлыка')
      await dialog.getByRole('button', { name: 'Закрыть', exact: true }).click()
      const discard = page.getByRole('dialog', { name: 'Несохранённые изменения' })
      await expect(discard).toBeVisible()
      await expect(discard).toHaveClass(/glass-surface/)
      await activate(discard.getByRole('button', { name: 'Продолжить редактирование' }))
      await expect(dialog.getByLabel('Название')).toHaveValue('Черновик ярлыка')
      await activate(dialog.getByRole('button', { name: 'Отмена', exact: true }))
      await activate(discard.getByRole('button', { name: 'Закрыть без сохранения' }))
      await expect(dialog).toHaveCount(0)
      await page.getByRole('button', { name: 'Найти', exact: true }).click()
      await page.getByRole('searchbox').fill(title)
      await checkGlass('.results .result')
    } finally { await context.close() }
  })
}

test('старый платёж без ссылки можно дополнить без изменения остальных полей', async ({ page }) => {
  await page.goto('/')
  await button(page, 'Настройки').click()
  const data = demo()
  await page.locator('.app-settings input[type=file]').setInputFiles({
    name: 'old.json', mimeType: 'application/json', buffer: Buffer.from(await exportBackup(data)),
  })
  await button(page, 'Подтвердить').click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Обзор')
  await navigateSection(page, 'Финансы')
  const card = page.getByRole('article').filter({ hasText: title })
  await expect(card.locator('.card-links')).toHaveCount(0)
  await button(page, `Редактировать «${title}»`).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Адрес сайта оплаты').fill(address)
  await dialog.getByLabel('Подпись ссылки').fill('Личный кабинет')
  await button(page, 'Готово').click()
  await expect(dialog).toHaveCount(0)
  const link = card.getByRole('link', { name: /Личный кабинет/ })
  await expect(link).toHaveAttribute('href', address)
  await expect(link).toContainText(address)
  await expect(card).toContainText('Сумма · 850 ₽')
})
