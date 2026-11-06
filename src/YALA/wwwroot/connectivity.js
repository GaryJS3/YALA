window.yalaConnectivity = (() => {
    let listener;
    return {
        watch(reference) {
            if (listener) window.removeEventListener('online', listener);
            listener = () => reference.invokeMethodAsync('Reconnected').catch(() => {});
            window.addEventListener('online', listener);
        },
        unwatch() {
            if (listener) window.removeEventListener('online', listener);
            listener = undefined;
        }
    };
})();

window.yalaOfflineQueue = {
    update(key, id, replacement) {
        const update = () => {
            let pending;
            try { pending = JSON.parse(localStorage.getItem(key)); }
            catch (error) { if (!(error instanceof SyntaxError)) throw error; }
            if (!Array.isArray(pending)) pending = [];
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
