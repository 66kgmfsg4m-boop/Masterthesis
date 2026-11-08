/**
 * Dark/Light Theme für die App (aus katalogbaum-ui übernommen).
 * - Speichert die Wahl in localStorage (Key: data-bs-theme-preference).
 * - Setzt data-bs-theme auf document.documentElement ("light" | "dark"); Bootstrap 5.3 nutzt das für Farben.
 * - Erlaubte Werte: "light", "dark", "auto" (auto = System via prefers-color-scheme).
 * - themeToggle() / themeGetResolved() werden von Blazor (ThemeToggle.razor) aufgerufen.
 */
window.theme = {
    key: 'data-bs-theme-preference',

    get: function () {
        return localStorage.getItem(this.key) || 'auto';
    },

    set: function (value) {
        if (!['light', 'dark', 'auto'].includes(value)) value = 'auto';
        localStorage.setItem(this.key, value);
        this.apply();
    },

    /** Nächsten Modus durchschalten: auto -> light -> dark -> auto */
    toggle: function () {
        var current = this.get();
        var next = current === 'auto' ? 'light' : current === 'light' ? 'dark' : 'auto';
        this.set(next);
        return next;
    },

    apply: function () {
        var resolved = this.getResolved();
        document.documentElement.setAttribute('data-bs-theme', resolved);
        try {
            document.querySelectorAll('iframe[data-theme-aware]').forEach(function (f) {
                try { f.contentWindow.postMessage({ type: 'theme', value: resolved }, '*'); } catch (e) { }
            });
        } catch (e) { }
    },

    getResolved: function () {
        var preference = this.get();
        return preference === 'auto'
            ? (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
            : preference;
    }
};

if (document.documentElement && !document.documentElement.getAttribute('data-bs-theme')) {
    window.theme.apply();
}

window.themeToggle = function () {
    window.theme.toggle();
    return window.theme.getResolved();
};
window.themeGetResolved = function () {
    return window.theme.getResolved();
};
