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

// 下拉菜单摆位。
//
// 菜单不能待在表格单元格里：表格包在 .wb-table-scroll 里（overflow:auto），
// 绝对定位的菜单会被容器裁掉 —— 最后一行点开就只剩一条边。
// 所以菜单渲染在表格外层，用 fixed 定位到触发按钮旁边；下方放不下就往上翻。
window.wbPositionMenu = function (menuId, anchorId) {
    var menu = document.getElementById(menuId);
    var anchor = document.getElementById(anchorId);

    if (!menu || !anchor) {
        return;
    }

    menu.style.position = 'fixed';
    menu.style.top = '0px';
    menu.style.left = '0px';

    var rect = anchor.getBoundingClientRect();
    var height = menu.offsetHeight;
    var width = menu.offsetWidth;
    var top = rect.bottom + 4;

    if (top + height > window.innerHeight - 8) {
        top = Math.max(8, rect.top - height - 4);
    }

    var left = rect.left;
    if (left + width > window.innerWidth - 8) {
        left = Math.max(8, window.innerWidth - width - 8);
    }

    menu.style.top = top + 'px';
    menu.style.left = left + 'px';
};
