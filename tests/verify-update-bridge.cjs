const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
let now = 0, next = 1, receive;
const timers = new Map(), sent = [];
const window = { chrome: { webview: {
  postMessage: m => sent.push(m), addEventListener: (_, fn) => { receive = fn; },
} }, addEventListener() {} };
const context = { window, console, Map, Promise, Error,
  setTimeout(fn, delay) { const id = next++; timers.set(id, { due: now + delay, fn }); return id; },
  clearTimeout(id) { timers.delete(id); },
};
vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname,
  '../src/Neo.App/Assets/HtmlShell/js/native-host.js'), 'utf8'), context);
async function main() {
  let done = false;
  const progress = [];
  const install = window.neoNative.request('settings.install', { token: 'synthetic-token' }, text => progress.push(text)).then(() => { done = true; });
  receive({ data: { type: 'update.progress', token: 'different-install', text: 'unrelated' } });
  assert.equal(progress.length, 0, 'another update cannot overwrite this dialog');
  receive({ data: { type: 'update.progress', token: 'synthetic-token', text: '下载 · 42%' } });
  assert.deepEqual(progress, ['下载 · 42%'], 'real progress reaches its original install dialog');
  const regular = window.neoNative.request('ports.apply', {}).then(() => 'resolved', () => 'rejected');
  now = 120001;
  for (const [id, t] of timers) if (t.due <= now) { timers.delete(id); t.fn(); }
  await Promise.resolve();
  assert.equal(done, false, 'a real download lasting two minutes must stay pending');
  assert.equal(await regular, 'rejected', 'ordinary actions still time out');
  receive({ data: { id: sent[0].id, ok: true, data: { message: 'installed' } } });
  await install;
  assert.equal(done, true, 'the late real install result reaches the original request');
  assert.equal(timers.size, 0, 'response removes long-running timer');
  console.log('PASS 6 update bridge progress and timeout assertions');
}
main().catch(e => { console.error(e); process.exitCode = 1; });
