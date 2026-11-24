import { defineConfig } from '@playwright/test'
import { resolve } from 'node:path'

const executable = process.env.POMOGATOR_TEST_EXE
const evidence = process.env.POMOGATOR_EVIDENCE_ROOT
const port = executable ? '47821' : process.env.POMOGATOR_TEST_PORT || '4182'
const url = `http://127.0.0.1:${port}`

export default defineConfig({
  testDir: './e2e',
  use: {
    baseURL: url,
    colorScheme: 'dark',
    locale: 'ru-RU',
    launchOptions: { executablePath: process.env.POMOGATOR_CHROMIUM || undefined },
  },
  webServer: {
    command: executable ? `"${resolve(executable)}" --no-browser` : evidence
      ? `python -m http.server ${port} --bind 127.0.0.1 --directory "${resolve(evidence)}"`
      : `npm run preview -- --host 127.0.0.1 --port ${port} --strictPort`,
    url,
  },
})
