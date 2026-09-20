// Set up event handlers
const reconnectBanner = document.getElementById("components-reconnect-modal");
reconnectBanner.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
const reconnectBannerDelayMilliseconds = 5000;
let reconnectBannerDelay;

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        scheduleReconnectBanner();
    } else if (event.detail.state === "hide") {
        hideReconnectBanner();
    } else if (event.detail.state === "failed") {
        showReconnectBanner();
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

function scheduleReconnectBanner() {
    if (!reconnectBanner.hidden || reconnectBannerDelay) {
        return;
    }

    reconnectBannerDelay = setTimeout(() => {
        reconnectBannerDelay = undefined;
        showReconnectBanner();
    }, reconnectBannerDelayMilliseconds);
}

function showReconnectBanner() {
    clearTimeout(reconnectBannerDelay);
    reconnectBannerDelay = undefined;
    reconnectBanner.hidden = false;
}

function hideReconnectBanner() {
    clearTimeout(reconnectBannerDelay);
    reconnectBannerDelay = undefined;
    reconnectBanner.hidden = true;
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                hideReconnectBanner();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        showReconnectBanner();
        reconnectBanner.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
