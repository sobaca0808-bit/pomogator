import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  use: {
    baseURL: 'http://127.0.0.1:4182',
    colorScheme: 'dark',
    locale: 'ru-RU',
    launchOptions: { executablePath: process.env.POMOGATOR_CHROMIUM || undefined },
  },
  webServer: {
    command: 'npm run preview -- --host 127.0.0.1 --port 4182 --strictPort',
    url: 'http://127.0.0.1:4182',
  },
})
