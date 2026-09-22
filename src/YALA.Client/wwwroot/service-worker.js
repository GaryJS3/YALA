const cacheName = "yala-shell-v1";
const shell = ["/", "/manifest.webmanifest", "/app.css"];

self.addEventListener("install", event => {
    event.waitUntil(caches.open(cacheName).then(cache => cache.addAll(shell)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", event => {
    event.waitUntil(self.clients.claim());
});

self.addEventListener("fetch", event => {
    const request = event.request;
    const url = new URL(request.url);
    if (request.method !== "GET" || url.origin !== self.location.origin || url.pathname.startsWith("/api/") || url.pathname.startsWith("/Account/") || url.pathname.startsWith("/images/")) return;

    event.respondWith(fetch(request).then(response => {
        if (response.ok && (url.pathname === "/" || url.pathname.startsWith("/_framework/") || url.pathname.endsWith(".css") || url.pathname.endsWith(".js") || url.pathname.endsWith(".png") || url.pathname.endsWith(".webmanifest"))) {
            const copy = response.clone();
            caches.open(cacheName).then(cache => cache.put(request, copy));
        }
        return response;
    }).catch(() => caches.match(request).then(cached => cached || caches.match("/"))));
});
