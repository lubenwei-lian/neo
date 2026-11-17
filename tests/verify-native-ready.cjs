const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname,
  '../src/Neo.App/Assets/HtmlShell/js/native-view.js'), 'utf8').replace(/\r\n/g, '\n');
const match = source.match(/  function nativeSignalViewReady\(\)\{[\s\S]*?\n  \}/);
assert.ok(match, 'production startup handshake is available');
const bundle = fs.readFileSync(path.join(__dirname,
  '../src/Neo.App/Assets/HtmlShell/js/app2.js'), 'utf8').replace(/\r\n/g, '\n');
const bundledMatch = bundle.match(/  function nativeSignalViewReady\(\)\{[\s\S]*?\n  \}/);
assert.equal(bundledMatch && bundledMatch[0], match[0], 'the actual loaded bundle contains the tested startup handshake');
assert.ok(bundle.includes('      nativeSignalViewReady();'), 'the loaded snapshot renderer invokes the startup handshake');
function fixture() {
  const timers = new Map(), frames = [], sent = [];
  let next = 1;
  const context = { nativeLast: {},
    host(action) { sent.push(action); return Promise.resolve(); },
    setTimeout(fn, delay) { const id = next++; timers.set(id, { fn, delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    requestAnimationFrame(fn) { frames.push(fn); },
  };
  vm.createContext(context);
  vm.runInContext(bundledMatch[0], context);
  return { context, timers, frames, sent,
    signal() { context.nativeSignalViewReady(); },
    paint() { const queued = frames.splice(0); queued.forEach(fn => fn()); },
    fallback() { for (const [id, timer] of [...timers]) { timers.delete(id); timer.fn(); } },
  };
}
const hidden = fixture(); hidden.signal();
assert.equal(hidden.sent.length, 0, 'snapshot application finishes before startup notification');
assert.ok([...hidden.timers.values()].every(t => t.delay <= 250), 'hidden-window fallback is bounded');
hidden.fallback();
assert.deepEqual(hidden.sent, ['view.ready'], 'a hidden window can show without receiving any animation frames');
hidden.paint(); hidden.paint();
assert.equal(hidden.sent.length, 1, 'resuming frames after the fallback never shows twice');
const visible = fixture(); visible.signal(); visible.paint();
assert.equal(visible.sent.length, 0, 'visible startup preserves its first layout frame');
visible.paint();
assert.deepEqual(visible.sent, ['view.ready'], 'visible startup retains the second-frame notification');
assert.equal(visible.timers.size, 0, 'normal frames cancel the fallback');
const updates = fixture();
for (let i = 0; i < 100; i++) updates.signal();
assert.equal(updates.timers.size, 1, 'repeated snapshots cannot create repeated startup timers');
updates.fallback(); updates.signal(); updates.paint(); updates.paint();
assert.deepEqual(updates.sent, ['view.ready'], 'repeated snapshots and later frames announce readiness exactly once');
console.log('PASS 9 native startup assertions plus bundle integration checks');
