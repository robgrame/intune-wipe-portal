// Light/dark theme toggle. The initial theme is applied inline in <head>
// (see App.razor) to avoid a flash of the wrong color scheme. This module
// handles the user-initiated toggle, persists the choice in localStorage, and
// re-applies it after Blazor enhanced navigation (which re-renders the server's
// static data-bs-theme="light" on <html> and would otherwise revert the theme).
(function () {
    function current() {
        return document.documentElement.getAttribute('data-bs-theme') || 'light';
    }

    function apply(theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
        try { localStorage.setItem('portal-theme', theme); } catch (e) { /* ignore */ }
    }

    function persisted() {
        try {
            var t = localStorage.getItem('portal-theme');
            if (!t) {
                t = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
            }
            return t;
        } catch (e) {
            return 'light';
        }
    }

    function reapply() {
        document.documentElement.setAttribute('data-bs-theme', persisted());
    }

    window.portalTheme = {
        toggle: function () {
            apply(current() === 'dark' ? 'light' : 'dark');
        },
        set: apply,
        get: current,
        reapply: reapply
    };

    // Blazor enhanced navigation patches the DOM with server-rendered markup
    // (data-bs-theme="light") without re-running the <head> bootstrap script.
    // Re-apply the persisted theme on every enhanced page load so dark mode
    // survives navigation. blazor.web.js loads after this module, so register
    // the listener once the global Blazor object is available.
    function registerEnhancedLoad() {
        if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
            window.Blazor.addEventListener('enhancedload', reapply);
        } else {
            setTimeout(registerEnhancedLoad, 50);
        }
    }
    registerEnhancedLoad();
})();

