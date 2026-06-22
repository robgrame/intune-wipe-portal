// Light/dark theme toggle. The initial theme is applied inline in <head>
// (see App.razor) to avoid a flash of the wrong color scheme. This module only
// handles the user-initiated toggle and persists the choice in localStorage.
(function () {
    function current() {
        return document.documentElement.getAttribute('data-bs-theme') || 'light';
    }

    function apply(theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
        try { localStorage.setItem('portal-theme', theme); } catch (e) { /* ignore */ }
    }

    window.portalTheme = {
        toggle: function () {
            apply(current() === 'dark' ? 'light' : 'dark');
        },
        set: apply,
        get: current
    };
})();
