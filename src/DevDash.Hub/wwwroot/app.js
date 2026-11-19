// Copy-to-clipboard for any element with a data-copy attribute. Delegated, so it keeps working
// as Blazor re-renders the page. This file only ever copies text; nothing here runs a command.
document.addEventListener('click', async (event) => {
    const button = event.target.closest('[data-copy]');
    if (!button) {
        return;
    }

    const text = button.getAttribute('data-copy');
    try {
        await navigator.clipboard.writeText(text);
    } catch {
        const area = document.createElement('textarea');
        area.value = text;
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        document.execCommand('copy');
        area.remove();
    }

    const label = button.textContent;
    button.classList.add('copied');
    button.textContent = 'Copied';
    setTimeout(() => {
        button.classList.remove('copied');
        button.textContent = label;
    }, 1400);
});

// Search box: Cmd/Ctrl-K focuses it; the arrow keys walk the results, Enter opens the first one and
// Escape leaves. The searching itself happens on the server.
(() => {
    const box = () => document.getElementById('global-search');
    const hits = () => Array.from(document.querySelectorAll('.gsearch-results a'));
    const isMac = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

    const labelKey = () => {
        const key = document.querySelector('.gsearch-key');
        if (key && !isMac && key.textContent !== 'Ctrl K') {
            key.textContent = 'Ctrl K';
        }
    };
    labelKey();
    document.addEventListener('focusin', labelKey);

    document.addEventListener('keydown', (event) => {
        const input = box();
        if (!input) {
            return;
        }

        if ((event.metaKey || event.ctrlKey) && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'k') {
            event.preventDefault();
            input.focus();
            input.select();
            return;
        }

        const inSearch = event.target instanceof Element && event.target.closest('.gsearch');
        if (!inSearch) {
            return;
        }

        const list = hits();
        const at = list.indexOf(document.activeElement);
        if (event.key === 'ArrowDown' && list.length > 0) {
            event.preventDefault();
            list[Math.min(at + 1, list.length - 1)].focus();
        } else if (event.key === 'ArrowUp' && list.length > 0) {
            event.preventDefault();
            (at <= 0 ? input : list[at - 1]).focus();
        } else if (event.key === 'Enter' && event.target === input && list.length > 0) {
            event.preventDefault();
            list[0].click();
        } else if (event.key === 'Escape') {
            document.activeElement.blur();
        }
    });
})();
