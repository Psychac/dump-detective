const MOTION_KEY = 'dumpdetective:motion';

export function loadMotionPreference() {
  const prefersReduced = (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) || false;
  try {
    const stored = localStorage.getItem(MOTION_KEY);
    if (stored === 'on') return true;
    if (stored === 'off') return false;
  } catch (e) { }
  return !prefersReduced;
}

// v2-only header stagger animation with a user-facing motion toggle.
export function setupMotionStagger(isV2, initialCanMotion) {
  if (!isV2) return;
  let canMotion = initialCanMotion;
  const staggerTargets = Array.from(document.querySelectorAll('#sec-header, #sec-health, #sec-exec, #sec-action-queue'));

  function applyStagger() {
    if (canMotion) {
      for (let i = 0; i < staggerTargets.length; i++) {
        const node = staggerTargets[i];
        node.classList.add('summary-stagger');
        node.style.setProperty('--stagger-delay', String(i * 120) + 'ms');
      }
    } else {
      for (let i = 0; i < staggerTargets.length; i++) {
        staggerTargets[i].classList.remove('summary-stagger');
      }
    }
  }

  applyStagger();

  // Insert a small motion toggle control (non-intrusive) to allow user override
  try {
    const existing = document.getElementById('motion-toggle');
    if (!existing) {
      const hdr = document.getElementById('sec-header') || document.body;
      const btn = document.createElement('button');
      btn.id = 'motion-toggle';
      btn.type = 'button';
      btn.className = 'action-btn motion-toggle';
      btn.setAttribute('aria-pressed', canMotion ? 'true' : 'false');
      btn.setAttribute('aria-label', 'Toggle motion animations');
      btn.textContent = canMotion ? 'Motion: On' : 'Motion: Off';
      btn.addEventListener('click', function () {
        canMotion = !canMotion;
        try { localStorage.setItem(MOTION_KEY, canMotion ? 'on' : 'off'); } catch (e) { }
        try { window.__DUMPDETECTIVE_CAN_MOTION__ = canMotion; } catch (e) { }
        btn.setAttribute('aria-pressed', canMotion ? 'true' : 'false');
        btn.textContent = canMotion ? 'Motion: On' : 'Motion: Off';
        applyStagger();
        if (!canMotion) {
          document.querySelectorAll('.anchor-flash').forEach(n => n.classList.remove('anchor-flash'));
        }
      });
      try { hdr.insertBefore(btn, hdr.firstChild); } catch (e) { document.body.appendChild(btn); }
    }
  } catch (e) { /* non-fatal */ }
}
