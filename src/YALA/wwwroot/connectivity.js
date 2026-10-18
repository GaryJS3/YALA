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

// Logout is a full page POST. Clear cached identity and private snapshots before leaving.
document.addEventListener('submit', event => {
    if (!event.target.matches('form[action="/Account/Logout"]')) return;
    for (const key of Object.keys(localStorage)) {
        if (key.startsWith('yala.')) localStorage.removeItem(key);
    }
    sessionStorage.removeItem('yala.household');
    if ('caches' in window) caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith('yala-')).map(key => caches.delete(key))));
});
