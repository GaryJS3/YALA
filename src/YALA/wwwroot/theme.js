(() => {
    const preferenceKey = "yala.theme";
    const root = document.documentElement;
    let dark = false;

    function applyTheme() {
        root.dataset.theme = dark ? "dark" : "light";
        const toggle = document.querySelector(".theme-switch");
        if (toggle) {
            toggle.setAttribute("aria-checked", String(dark));
            toggle.hidden = false;
        }
    }

    try {
        dark = localStorage.getItem(preferenceKey) === "dark";
    } catch {
        // The switch still works for this page when browser storage is unavailable.
    }
    applyTheme();

    document.addEventListener("DOMContentLoaded", () => {
        applyTheme();
        window.Blazor?.addEventListener("enhancedload", applyTheme);
    });

    document.addEventListener("click", event => {
        if (!event.target.closest(".theme-switch")) return;

        dark = !dark;
        applyTheme();
        try {
            localStorage.setItem(preferenceKey, root.dataset.theme);
        } catch {
            // Keep the selection active for the current page.
        }
    });
})();
