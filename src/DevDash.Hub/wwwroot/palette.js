// The command palette's browser half: the Cmd/Ctrl-K shortcut, moving through the results with the
// arrow keys, keeping focus inside the dialog while it is open, and remembering what was used recently.
// The list itself is rendered on the server (Components/Layout/CommandPalette.razor). Nothing here
// runs a command: Enter clicks the highlighted row, which is a link or a button the server rendered.

const RECENT_KEY = 'devdash.palette.recent';
const isMac = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

let host = null;        // the .NET component
let active = 0;         // index of the highlighted row
let returnTo = null;    // what had focus before the palette opened

const dialog = () => document.querySelector('.cmdk');
const input = () => document.getElementById('cmdk-input');
const rows = () => Array.from(document.querySelectorAll('.cmdk [data-cmdk-item]'));

function paint(scroll) {
    const list = rows();
    active = Math.max(0, Math.min(active, list.length - 1));
    list.forEach((row, i) => row.setAttribute('aria-selected', i === active ? 'true' : 'false'));
    const field = input();
    if (field) {
        if (list[active]) {
            field.setAttribute('aria-activedescendant', list[active].id);
        } else {
            field.removeAttribute('aria-activedescendant');
        }
    }
    if (scroll && list[active]) {
        list[active].scrollIntoView({ block: 'nearest' });
    }
}

function labelKey() {
    if (!isMac) {
        document.querySelectorAll('.cmdk-key').forEach((key) => {
            if (key.textContent !== 'Ctrl K') {
                key.textContent = 'Ctrl K';
            }
        });
    }
}

function close() {
    if (host) {
        host.invokeMethodAsync('CloseFromKey');
    }
}

function onKeyDown(event) {
    if ((event.metaKey || event.ctrlKey) && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        if (dialog()) {
            close();
        } else if (host) {
            returnTo = document.activeElement;
            host.invokeMethodAsync('OpenFromKey');
        }
        return;
    }

    if (!dialog()) {
        return;
    }

    const list = rows();
    switch (event.key) {
        case 'ArrowDown':
            event.preventDefault();
            active = list.length === 0 ? 0 : (active + 1) % list.length;
            paint(true);
            break;
        case 'ArrowUp':
            event.preventDefault();
            active = list.length === 0 ? 0 : (active - 1 + list.length) % list.length;
            paint(true);
            break;
        case 'Home':
        case 'PageUp':
            if (event.key === 'PageUp' || event.metaKey || event.ctrlKey) {
                event.preventDefault();
                active = 0;
                paint(true);
            }
            break;
        case 'End':
        case 'PageDown':
            if (event.key === 'PageDown' || event.metaKey || event.ctrlKey) {
                event.preventDefault();
                active = list.length - 1;
                paint(true);
            }
            break;
        case 'Enter':
            event.preventDefault();
            if (list[active]) {
                list[active].click();
            }
            break;
        case 'Escape':
            event.preventDefault();
            event.stopPropagation();
            close();
            break;
        case 'Tab':
            // The focus trap: the search field is the only stop, the rows are reached with the arrow keys.
            event.preventDefault();
            input()?.focus();
            break;
    }
}

// Focus that lands outside the open dialog (a click on the page behind, a screen reader's jump) comes back.
function onFocusIn(event) {
    const box = dialog();
    if (box && !box.contains(event.target)) {
        input()?.focus();
    }
}

function onPointerDown(event) {
    if (event.target instanceof Element && event.target.hasAttribute('data-cmdk-backdrop')) {
        event.preventDefault();
        close();
    }
}

function onPointerMove(event) {
    const row = event.target instanceof Element ? event.target.closest('.cmdk [data-cmdk-item]') : null;
    if (row) {
        const at = rows().indexOf(row);
        if (at >= 0 && at !== active) {
            active = at;
            paint(false);
        }
    }
}

function readRecent() {
    try {
        const list = JSON.parse(localStorage.getItem(RECENT_KEY) || '[]');
        return Array.isArray(list) ? list.filter((id) => typeof id === 'string').slice(0, 8) : [];
    } catch {
        return [];
    }
}

// Called once per page load. Returns what was used recently, most recent first.
export function init(component) {
    host = component;
    document.addEventListener('keydown', onKeyDown, true);
    document.addEventListener('focusin', onFocusIn);
    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('mousemove', onPointerMove);
    labelKey();
    return readRecent();
}

// The dialog has just been rendered.
export function opened() {
    labelKey();
    if (!returnTo || returnTo === document.body) {
        returnTo = document.getElementById('global-search');
    }
    const field = input();
    if (field) {
        field.focus();
        field.select();
    }
    active = 0;
    paint(true);
}

// The results changed: the first one is highlighted again.
export function sync() {
    active = 0;
    paint(true);
}

// The dialog has gone: focus returns to where it was.
export function closed() {
    const target = returnTo;
    returnTo = null;
    if (target && document.contains(target) && typeof target.focus === 'function') {
        target.focus({ preventScroll: true });
    }
}

export function remember(ids) {
    try {
        localStorage.setItem(RECENT_KEY, JSON.stringify(ids));
    } catch {
        // Private browsing, or storage is full: recents last for this visit only.
    }
}

export function dispose() {
    document.removeEventListener('keydown', onKeyDown, true);
    document.removeEventListener('focusin', onFocusIn);
    document.removeEventListener('mousedown', onPointerDown);
    document.removeEventListener('mousemove', onPointerMove);
    host = null;
}
