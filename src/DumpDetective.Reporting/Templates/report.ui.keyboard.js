export function setupKeyboardShortcuts(announce) {
  document.addEventListener('keydown', function (ev) {
    const active = document.activeElement;
    const tag = active && active.tagName ? active.tagName.toLowerCase() : '';
    const isEditing = tag === 'input' || tag === 'textarea' || tag === 'select' || (active && active.isContentEditable);

    try {
      if (active && active.closest && active.closest('.table-with-pagination')) {
        const container = active.closest('.table-with-pagination');
        if (container) {
          const prev = container.querySelector('.table-prev');
          const next = container.querySelector('.table-next');
          if (ev.key === 'ArrowLeft' && prev && !prev.disabled) { prev.click(); ev.preventDefault(); }
          if (ev.key === 'ArrowRight' && next && !next.disabled) { next.click(); ev.preventDefault(); }
        }
      }
    } catch (e) { }

    if (isEditing) return;

    // Shift+N: jump to next critical signal card/section.
    if (ev.shiftKey && !ev.ctrlKey && !ev.altKey && String(ev.key || '').toLowerCase() === 'n') {
      const criticalNodes = Array.from(document.querySelectorAll(
        '.analyzer-section[data-lead-severity="critical"]:not([hidden]), .health-domain-tile--critical:not([hidden])'
      ));
      if (criticalNodes.length) {
        const currentY = window.scrollY;
        let target = criticalNodes[0];
        for (let i = 0; i < criticalNodes.length; i++) {
          const rect = criticalNodes[i].getBoundingClientRect();
          const top = rect.top + window.scrollY;
          if (top > currentY + 12) {
            target = criticalNodes[i];
            break;
          }
        }
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
        try {
          if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
          target.focus({ preventScroll: true });
        } catch (e) { }
        if (announce) announce('Jumped to next critical signal');
      }
      ev.preventDefault();
      return;
    }

    // Shift+A: jump to action queue.
    if (ev.shiftKey && !ev.ctrlKey && !ev.altKey && String(ev.key || '').toLowerCase() === 'a') {
      const queue = document.getElementById('sec-action-queue');
      if (queue) {
        queue.scrollIntoView({ behavior: 'smooth', block: 'start' });
        try {
          if (!queue.hasAttribute('tabindex')) queue.setAttribute('tabindex', '-1');
          queue.focus({ preventScroll: true });
        } catch (e) { }
        if (announce) announce('Jumped to action queue');
      }
      ev.preventDefault();
      return;
    }
  });
}
