import { expect, test, type Page } from '@playwright/test'
import { exportBackup } from '../src/backup'
import { createCard, makeBlock } from '../src/cards'
import { saveEntry, savePlace, seed } from '../src/model'

const button = (page: Page, name: string) => page.getByRole('button', { name, exact: true })
const heading = (page: Page, name: string) => page.getByRole('heading', { name, exact: true, level: 1 })

async function expectNoOverflow(page: Page) {
  const dimensions = await page.evaluate(() => {
    const dialog = document.querySelector('dialog[open]')
    return {
      width: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth,
      dialogFits: !dialog || dialog.scrollWidth <= dialog.clientWidth + 1,
    }
  })
  expect(dimensions.scrollWidth).toBeLessThanOrEqual(dimensions.width)
  expect(dimensions.dialogFits).toBe(true)
}

for (const viewport of [{ width: 320, height: 680 }, { width: 800, height: 1000 }]) {
  test(`стеклянная навигация, прокрутка и формы при ширине ${viewport.width}px`, async ({ browser, baseURL }) => {
    const context = await browser.newContext({ viewport, hasTouch: true, isMobile: true, reducedMotion: 'reduce' })
    try {
      const page = await context.newPage()
      await page.goto(baseURL!)
      await expect(heading(page, 'Обзор')).toBeVisible()
      await expect(button(page, 'Наверх')).toHaveCount(0)
      await expectNoOverflow(page)
      await button(page, 'Предстоящие платежи').tap()
      const payments = heading(page, 'Предстоящие платежи')
      await expect(payments).toBeVisible()
      if (viewport.width === 320) {
        const clipped = await payments.evaluate(title => {
          const lineHeight = parseFloat(getComputedStyle(title).lineHeight)
          // Allow for font glyphs taller than their line box, but no clipped line.
          return title.scrollHeight - title.clientHeight >= lineHeight / 2
        })
        expect(clipped).toBe(false)
      }
      await button(page, 'Назад').tap()

      let data = savePlace(seed(), { id: 'nested', parent: 'notes', title: 'Вложенная папка', icon: 'folder' })
      for (let index = 1; index <= 24; index++)
        data = saveEntry(data, { ...createCard('note', 'nested'), title: 'Запись ' + index, blocks: [
          { ...makeBlock('text'), type: 'text', value: 'Строка без переноса: ' + 'длинныйтекст'.repeat(20) },
        ] })
      await button(page, 'Настройки').tap()
      await page.locator('.app-settings input[type=file]').setInputFiles({
        name: 'notes.json', mimeType: 'application/json', buffer: Buffer.from(await exportBackup(data)),
      })
      await button(page, 'Подтвердить').tap()
      await expect(heading(page, 'Обзор')).toBeVisible()
      await button(page, 'Заметки').tap()
      await page.getByRole('button', { name: /^Вложенная папка 0 пап/ }).tap()
      const title = heading(page, 'Вложенная папка')
      const bar = page.locator('.section-bar')
      await expect(bar).toHaveCSS('position', 'sticky')
      await expect(bar).toHaveCSS('backdrop-filter', /blur/)
      await expect(bar.getByRole('button', { name: 'Назад', exact: true })).toHaveCSS('border-radius', '999px')
      await expectNoOverflow(page)

      const edit = button(page, 'Редактировать «Запись 24»')
      await edit.scrollIntoViewIfNeeded()
      const before = await page.evaluate(() => scrollY)
      expect(before).toBeGreaterThan(viewport.height)
      expect((await title.boundingBox())!.y).toBeLessThan(60)
      await expect(button(page, 'Наверх')).toBeInViewport()
      await edit.tap()
      await expect(page.getByRole('dialog').getByLabel('Название')).toHaveValue('Запись 24')
      await expectNoOverflow(page)
      await page.getByRole('dialog').getByRole('button', { name: 'Назад' }).tap()
      await expect(page.getByRole('dialog')).toHaveCount(0)
      expect(Math.abs(await page.evaluate(() => scrollY) - before)).toBeLessThan(80)
      await button(page, 'Наверх').tap()
      await expect.poll(() => page.evaluate(() => scrollY)).toBe(0)
      await expect(title).toBeFocused()
      await expect(button(page, 'Наверх')).toHaveCount(0)

      await button(page, 'Календарь').tap()
      await expect(heading(page, 'Календарь')).toBeVisible()
      const today = page.locator('.calendar-grid [aria-current=date]')
      await expect(today).toHaveAttribute('aria-pressed', 'true')
      await expectNoOverflow(page)
      await button(page, 'Настройки').tap()
      await page.getByRole('checkbox', { name: 'Светлая тема' }).tap()
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
      await page.reload()
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
      await button(page, 'Мой центр').tap()
      await button(page, 'Добавить ярлык').tap()
      await expect(page.getByRole('dialog')).toHaveCSS('color-scheme', 'light')
      await expectNoOverflow(page)
    } finally { await context.close() }
  })
}

test('Glass UI: мышь, клавиатура и состояния элементов', async ({ page }) => {
  await page.goto('/')
  const overview = button(page, 'Обзор')
  await expect(overview).toHaveAttribute('aria-current', 'page')
  const search = page.getByRole('searchbox')
  const searchFrame = page.locator('.search')
  const baseShadow = await searchFrame.evaluate(element => getComputedStyle(element).boxShadow)
  await page.keyboard.press('Control+k')
  await expect(search).toBeFocused()
  await expect(searchFrame).not.toHaveCSS('box-shadow', baseShadow)
  await page.keyboard.press('Shift+Tab')
  const settings = button(page, 'Настройки')
  await expect(settings).toBeFocused()
  await expect(settings).toHaveCSS('outline-style', 'solid')
  await page.keyboard.press('Enter')
  await expect(heading(page, 'Настройки')).toBeVisible()
  await expect(settings).toHaveAttribute('aria-current', 'page')
  await expect(overview).not.toHaveAttribute('aria-current', 'page')

  await overview.click()
  const center = button(page, 'Мой центр')
  await page.mouse.move(0, 0)
  const background = await center.evaluate(element => getComputedStyle(element).backgroundColor)
  await center.hover()
  await expect(center).not.toHaveCSS('background-color', background)
  await page.mouse.down()
  await expect(center).toHaveCSS('animation-name', 'press')
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await expect(center).toHaveCSS('animation-name', 'none')
  await page.mouse.move(0, 0)
  await page.mouse.up()
})
