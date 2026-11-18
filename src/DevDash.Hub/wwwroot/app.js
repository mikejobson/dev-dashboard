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
