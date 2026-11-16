window.wbTheme = (function () {
    var key = 'wb-theme';

    function apply(theme) {
        if (theme === 'light') {
            document.documentElement.setAttribute('data-theme', 'light');
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
    }

    function get() {
        try {
            return localStorage.getItem(key) || 'dark';
        } catch (e) {
            return 'dark';
        }
    }

    function set(theme) {
        try {
            localStorage.setItem(key, theme);
        } catch (e) {
            // 隐私模式下写不了 localStorage —— 主题这次生效就行，不持久化
        }
        apply(theme);
    }

    function toggle() {
        var next = get() === 'light' ? 'dark' : 'light';
        set(next);
        return next;
    }

    return { key: key, apply: apply, get: get, set: set, toggle: toggle };
})();
