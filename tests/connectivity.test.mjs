import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

test('reconnection listener invokes the active component and is removed on disposal', async () => {
    const { context, listeners } = loadScript();
    const calls = [];
    context.window.yalaConnectivity.watch({ invokeMethodAsync: async name => calls.push(name) });
    await listeners.get('online')();
    assert.deepEqual(calls, ['Reconnected']);
    context.window.yalaConnectivity.unwatch();
    assert.equal(listeners.has('online'), false);
    assert.equal(listeners.has('focus'), false);
    assert.equal(listeners.has('visibilitychange'), false);
    assert.equal(listeners.has('interval'), false);
});

test('shared-list refresh skips hidden, offline and overlapping requests', async () => {
    const { context, listeners } = loadScript();
    let calls = 0;
    let finish;
    context.window.yalaConnectivity.watch({ invokeMethodAsync: () => { calls++; return new Promise(resolve => finish = resolve); } });
    context.document.visibilityState = 'hidden';
    await listeners.get('interval')();
    assert.equal(calls, 0);
    context.document.visibilityState = 'visible';
    context.navigator.onLine = false;
    await listeners.get('interval')();
    assert.equal(calls, 0);
    context.navigator.onLine = true;
    const pending = listeners.get('focus')();
    await listeners.get('interval')();
    assert.equal(calls, 1);
    finish();
    await pending;
    const next = listeners.get('visibilitychange')();
    assert.equal(calls, 2);
    finish();
    await next;
    context.window.yalaConnectivity.unwatch();
});

test('logout removes private browser data and YALA caches while preserving unrelated data', async () => {
    const { context, listeners, deletedCaches } = loadScript();
    listeners.get('submit')({ target: { matches: () => true } });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(context.localStorage['yala.session'], undefined);
    assert.equal(context.localStorage['yala.list:alice:home'], undefined);
    assert.equal(context.localStorage['yala.pending-mutations.v2'], undefined);
    assert.equal(context.localStorage.unrelated, 'keep');
    assert.equal(context.sessionStorage['yala.household'], undefined);
    assert.deepEqual(deletedCaches, ['yala-shell-v1']);
});

test('queue changes use one browser lock and preserve order when an existing change is updated', async () => {
    const { context } = loadScript();
    const queue = context.window.yalaOfflineQueue;
    const key = 'yala.pending-mutations.v2';
    const lockNames = [];
    context.navigator.locks = { request: async (name, update) => { lockNames.push(name); return update(); } };
    await Promise.all([
        queue.update(key, 'first', '{"operationId":"first"}'),
        queue.update(key, 'second', '{"operationId":"second"}')
    ]);
    await queue.update(key, 'first', '{"operationId":"first","error":"Retry"}');
    assert.deepEqual(JSON.parse(context.localStorage.getItem(key)), [
        { operationId: 'first', error: 'Retry' }, { operationId: 'second' }
    ]);
    await queue.update(key, 'first', null);
    assert.deepEqual(JSON.parse(context.localStorage.getItem(key)), [{ operationId: 'second' }]);
    assert.deepEqual(lockNames, [key, key, key, key]);
});

test('a damaged offline queue is never overwritten by a new edit', () => {
    const { context } = loadScript();
    const key = 'yala.pending-mutations.v2';
    for (const saved of ['{broken', 'null', '{}']) {
        context.localStorage.setItem(key, saved);
        assert.throws(() => context.window.yalaOfflineQueue.update(key, 'new', '{"operationId":"new"}'));
        assert.equal(context.localStorage.getItem(key), saved);
    }
});

function loadScript() {
    const listeners = new Map();
    const deletedCaches = [];
    const localStorage = {
        'yala.session': '{}',
        'yala.list:alice:home': '{}',
        'yala.pending-mutations.v2': '[]',
        unrelated: 'keep',
        getItem(key) { return this[key] ?? null; },
        setItem(key, value) { this[key] = value; },
        removeItem(key) { delete this[key]; }
    };
    const sessionStorage = { 'yala.household': 'home', removeItem(key) { delete this[key]; } };
    const caches = { keys: async () => ['yala-shell-v1', 'unrelated'], delete: async key => deletedCaches.push(key) };
    const window = {
        addEventListener: (name, callback) => listeners.set(name, callback),
        removeEventListener: (name, callback) => { if (listeners.get(name) === callback) listeners.delete(name); },
        setInterval: (callback, delay) => { assert.equal(delay, 15000); listeners.set('interval', callback); return 1; },
        clearInterval: () => listeners.delete('interval'),
        caches
    };
    const context = { window, localStorage, sessionStorage, caches, navigator: {}, document: {
        addEventListener: (name, callback) => listeners.set(name, callback),
        removeEventListener: (name, callback) => { if (listeners.get(name) === callback) listeners.delete(name); }
    } };
    vm.runInNewContext(readFileSync(new URL('../src/YALA/wwwroot/connectivity.js', import.meta.url), 'utf8'), context);
    return { context, listeners, deletedCaches };
}
