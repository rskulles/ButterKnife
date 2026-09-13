#!/usr/bin/env node
// Screenshot a page with headless Chrome after a real-time wait, over the DevTools protocol.
// Chrome's own --screenshot flag captures as soon as the page has loaded, which for a Blazor Server page is before
// the circuit has connected and the model list has arrived. This script navigates, waits `waitMs`, then captures.
//
//   node tools/screenshot.mjs <url> <out.png> [waitMs=8000] [width=1440] [height=900] [dark|light]
//
// Needs Node 22+ (built-in WebSocket and fetch) and Google Chrome (override the path with CHROME=...).
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [url, out, waitArg = '8000', widthArg = '1440', heightArg = '900', scheme = 'dark'] = process.argv.slice(2);
if (!url || !out) {
    console.error('usage: node tools/screenshot.mjs <url> <out.png> [waitMs] [width] [height] [dark|light]');
    process.exit(2);
}
const waitMs = Number(waitArg);
const width = Number(widthArg);
const height = Number(heightArg);
const chrome = process.env.CHROME ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const port = 9000 + Math.floor(Math.random() * 1000);
const profile = mkdtempSync(join(tmpdir(), 'bk-shot-'));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const proc = spawn(chrome, [
    '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
    `--window-size=${width},${height}`, `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank',
], { stdio: 'ignore' });

try {
    let wsUrl;
    for (let i = 0; i < 100 && !wsUrl; i++) {
        try {
            const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
            wsUrl = targets.find((t) => t.type === 'page')?.webSocketDebuggerUrl;
        } catch { /* not up yet */ }
        if (!wsUrl) {
            await sleep(100);
        }
    }
    if (!wsUrl) {
        throw new Error('Chrome did not expose a page target');
    }

    const sock = new WebSocket(wsUrl);
    await new Promise((resolve, reject) => { sock.onopen = resolve; sock.onerror = reject; });
    let nextId = 0;
    const pending = new Map();
    sock.onmessage = (e) => {
        const m = JSON.parse(e.data);
        if (m.id && pending.has(m.id)) {
            pending.get(m.id)(m);
            pending.delete(m.id);
        }
    };
    const send = (method, params = {}) => new Promise((resolve, reject) => {
        const id = ++nextId;
        pending.set(id, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result)));
        sock.send(JSON.stringify({ id, method, params }));
    });

    await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
    await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: scheme }] });
    await send('Page.navigate', { url });
    await sleep(waitMs);
    const { data } = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(out, Buffer.from(data, 'base64'));
    console.log(`${out}: ${width}x${height} after ${waitMs} ms`);
    sock.close();
} finally {
    const exited = new Promise((resolve) => proc.once('exit', resolve));
    proc.kill();
    await Promise.race([exited, sleep(5000)]); // let Chrome finish writing its profile before removing it
    rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
