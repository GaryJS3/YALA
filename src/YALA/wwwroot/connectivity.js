window.yalaDialog = {
    show(dialog) { if (!dialog.open) dialog.showModal(); },
    close(dialog) { if (dialog.open) dialog.close(); }
};

window.yalaConnectivity = (() => {
    let listener;
    let timer;
    let stop;
    return {
        watch(reference) {
            stop?.();
            let refreshing = false;
            listener = async () => {
                if (refreshing || document.visibilityState === 'hidden' || navigator.onLine === false) return;
                refreshing = true;
                try { await reference.invokeMethodAsync('Reconnected'); }
                catch { /* The component may have been disposed during a refresh. */ }
                finally { refreshing = false; }
            };
            window.addEventListener('online', listener);
            window.addEventListener('focus', listener);
            document.addEventListener('visibilitychange', listener);
            timer = window.setInterval(listener, 15000);
            const currentListener = listener;
            const currentTimer = timer;
            stop = () => {
                window.removeEventListener('online', currentListener);
                window.removeEventListener('focus', currentListener);
                document.removeEventListener('visibilitychange', currentListener);
                window.clearInterval(currentTimer);
            };
        },
        unwatch() {
            stop?.();
            stop = undefined;
            listener = undefined;
            timer = undefined;
        }
    };
})();

window.yalaOfflineQueue = {
    update(key, id, replacement) {
        const update = () => {
            const saved = localStorage.getItem(key);
            const pending = saved === null ? [] : JSON.parse(saved);
            if (!Array.isArray(pending)) throw new Error('Saved changes could not be read. The original data has been kept.');
            const index = pending.findIndex(change => change?.operationId === id);
            if (replacement === null) {
                if (index < 0) return;
                pending.splice(index, 1);
            } else {
                const change = JSON.parse(replacement);
                if (index < 0) pending.push(change);
                else pending[index] = change;
            }
            localStorage.setItem(key, JSON.stringify(pending));
        };
        return navigator.locks ? navigator.locks.request(key, update) : update();
    }
};

// Logout is a full page POST. Clear cached identity and private snapshots before leaving.
document.addEventListener('submit', event => {
    if (!event.target.matches('form[action="/Account/Logout"]')) return;
    for (const key of Object.keys(localStorage)) {
        if (key.startsWith('yala.')) localStorage.removeItem(key);
    }
    sessionStorage.removeItem('yala.household');
    if ('caches' in window) caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith('yala-')).map(key => caches.delete(key))));
});
