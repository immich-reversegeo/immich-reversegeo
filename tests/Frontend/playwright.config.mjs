import { defineConfig } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
process.env.FRONTEND_TEST_RUN_DIR ??= join(root, '_out', 'frontend-tests', 'runs', randomUUID());

export default defineConfig({
    testDir: '.',
    testMatch: '**/*.spec.mjs',
    workers: 1,
    fullyParallel: false,
    timeout: 45000,
    expect: { timeout: 10000 },
    outputDir: join(root, '_out', 'frontend-tests', 'results'),
    reporter: [['list'], ['html', { outputFolder: join(root, '_out', 'frontend-tests', 'report'), open: 'never' }]],
    use: {
        baseURL: 'http://127.0.0.1:5265',
        browserName: 'chromium',
        timezoneId: 'UTC',
        locale: 'en-US',
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure'
    },
    projects: [
        { name: 'desktop', use: { viewport: { width: 1280, height: 900 } } },
        { name: 'phone', use: { viewport: { width: 390, height: 844 } } }
    ],
    webServer: {
        command: 'node tests/Frontend/server.mjs',
        cwd: root,
        url: 'http://127.0.0.1:5265/data/location-management/scans',
        reuseExistingServer: false,
        timeout: 180000,
        gracefulShutdown: { signal: 'SIGTERM', timeout: 5000 },
        env: { FRONTEND_TEST_RUN_DIR: process.env.FRONTEND_TEST_RUN_DIR }
    }
});
