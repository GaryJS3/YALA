const cacheName = "yala-shell-v2";

self.addEventListener("install", event => {
    event.waitUntil((async () => {
        const response = await fetch("/", { cache: "reload" });
        if (response.ok && !response.redirected && response.headers.get("Content-Type")?.includes("text/html")) {
            const cache = await caches.open(cacheName);
            await cache.put("/", response);
        }
        await self.skipWaiting();
    })());
});

self.addEventListener("activate", event => {
    event.waitUntil((async () => {
        for (const key of await caches.keys()) {
            if (key.startsWith("yala-shell-") && key !== cacheName) await caches.delete(key);
        }
        await self.clients.claim();
    })());
});

self.addEventListener("fetch", event => {
    const request = event.request;
    const url = new URL(request.url);
    if (request.method !== "GET" || url.origin !== self.location.origin) return;
    const home = request.mode === "navigate" && url.pathname === "/";
    const asset = request.mode !== "navigate" && /^(script|style|image|font|manifest|empty)$/.test(request.destination)
        && (url.pathname.startsWith("/_framework/") || /^\/[^/]+\.(css|js|png|webmanifest)$/.test(url.pathname));
    if (!home && !asset) return;

    const key = home ? "/" : request;
    const network = fetch(request);
    event.waitUntil(network.then(async response => {
        if (!response.ok || response.redirected) return;
        if (home && !response.headers.get("Content-Type")?.includes("text/html")) return;
        const copy = response.clone();
        const cache = await caches.open(cacheName);
        await cache.put(key, copy);
    }).catch(() => {}));
    event.respondWith(network.catch(async () => {
        const cache = await caches.open(cacheName);
        return (await cache.match(key)) ?? Response.error();
    }));
});
