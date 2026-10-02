/**
 * Volt hydration runtime v1 (see CONTRACT.md).
 * Zero dependencies, single ES module, served as-is.
 * - Boots volt-island elements and binds delegated events (data-v-on="event:action").
 * - Dispatches island actions to POST /_volt/action and morphs the returned form fragment.
 * - Client-side router: intercepts internal links, keyed morph by data-v-key, prefetch.
 */
'use strict';

// ---------------------------------------------------------------------------
// island registry
// ---------------------------------------------------------------------------

/** sid -> { el, island, state } */
const islands = new Map();

function safeJson(s) {
  try { return JSON.parse(s); } catch (_) { return {}; }
}

function collectIslands(root) {
  (root || document).querySelectorAll('volt-island').forEach((el) => {
    const sid = el.getAttribute('data-sid');
    if (!sid) return;
    islands.set(sid, {
      el: el,
      island: el.getAttribute('data-v') || '',
      state: safeJson(el.getAttribute('data-props')),
    });
  });
}

function formArgs(form) {
  const out = {};
  if (!form) return out;
  const fd = new FormData(form);
  fd.forEach((v, k) => {
    if (k.indexOf('__volt_') === 0) return;
    out[k] = typeof v === 'string' ? v : (v && v.name) || '';
  });
  return out;
}

async function dispatchAction(el, action) {
  const islandEl = el.closest ? el.closest('volt-island') : null;
  const form = el.closest ? el.closest('form[data-v-form]') : null;
  if (!islandEl) return;
  const sid = islandEl.getAttribute('data-sid');
  const rec = islands.get(sid);
  if (!rec) return;

  // WASM island (M3, experimental): dispatch + render client-side, no round-trip.
  const wasmUrl = islandEl.getAttribute('data-v-wasm');
  if (wasmUrl) {
    try {
      const module = await loadWasmModule(wasmUrl);
      const html = wasmDispatchIsland(module, rec.island, action, rec.state, formArgs(form));
      if (html) applyFragment(form, html);
    } catch (err) {
      console.error('volt: wasm island error', err);
    }
    return;
  }

  try {
    const res = await fetch('/_volt/action', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
      body: JSON.stringify({
        island: rec.island,
        sid: sid,
        action: action,
        state: rec.state,
        args: formArgs(form),
      }),
    });
    if (!res.ok) {
      console.error('volt: action failed', res.status);
      return;
    }
    const payload = await res.json();
    applyFragment(form, payload.html);
  } catch (err) {
    console.error('volt: action error', err);
  }
}


// ---------------------------------------------------------------------------
// WASM islands (M3, experimental) — see CONTRACT.md for the module contract:
//   exports: memory, alloc(n) -> offset, volt_dispatch(...) -> resultPtr,
//            volt_render(statePtr, stateLen) -> htmlPtr
// strings cross the boundary as UTF-8, NUL-terminated, in module memory.
// ---------------------------------------------------------------------------

const wasmModules = new Map();

async function loadWasmModule(url) {
  let entry = wasmModules.get(url);
  if (!entry) {
    entry = WebAssembly.instantiateStreaming(fetch(url), {}).then((mod) => mod.instance);
    wasmModules.set(url, entry);
  }
  return entry;
}

function wasmWriteString(instance, s) {
  const bytes = new TextEncoder().encode(s);
  const exports = instance.exports;
  const offset = exports.alloc(bytes.length + 1);
  const memory = new Uint8Array(exports.memory.buffer, offset, bytes.length + 1);
  memory.set(bytes);
  memory[bytes.length] = 0; // NUL terminator
  return { offset: offset, length: bytes.length };
}

function wasmReadString(instance, offset) {
  if (!offset) return null;
  const bytes = new Uint8Array(instance.exports.memory.buffer);
  let end = offset;
  while (end < bytes.length && bytes[end] !== 0) end++;
  return new TextDecoder().decode(bytes.subarray(offset, end));
}

function wasmDispatchIsland(instance, island, action, state, args) {
  const i = wasmWriteString(instance, island);
  const a = wasmWriteString(instance, action);
  const st = wasmWriteString(instance, JSON.stringify(state));
  const ar = wasmWriteString(instance, JSON.stringify(args || {}));
  const resultPtr = instance.exports.volt_dispatch(
    i.offset, i.length, a.offset, a.length,
    st.offset, st.length, ar.offset, ar.length);
  const newState = wasmReadString(instance, resultPtr);
  if (!newState) throw new Error('volt_dispatch failed');
  const statePtr = wasmWriteString(instance, newState);
  const htmlPtr = instance.exports.volt_render(statePtr.offset, statePtr.length);
  return wasmReadString(instance, htmlPtr);
}

function applyFragment(form, html) {
  const active = document.activeElement;
  const focusId = active && active.id && active.getAttribute ? active.id : null;
  const tpl = document.createElement('template');
  tpl.innerHTML = String(html || '').trim();
  const next = tpl.content.firstElementChild;
  if (!next) return;
  if (form && form.parentNode) form.parentNode.replaceChild(next, form);
  else document.body.appendChild(next);
  collectIslands(next);
  if (focusId) {
    const again = document.getElementById(focusId);
    if (again && typeof again.focus === 'function') again.focus();
  }
}

// ---------------------------------------------------------------------------
// delegated events (capture phase)
// ---------------------------------------------------------------------------

function parseOn(el, eventType) {
  const attr = el.getAttribute('data-v-on');
  if (!attr) return null;
  const parts = attr.split(/\s+/);
  for (let i = 0; i < parts.length; i++) {
    const p = parts[i];
    const idx = p.indexOf(':');
    if (idx < 0) {
      if (eventType === 'click') return p;
      continue;
    }
    if (p.slice(0, idx) === eventType) return p.slice(idx + 1);
  }
  return null;
}

function bindOn(eventType, opts) {
  document.addEventListener(
    eventType,
    (e) => {
      const t = e.target instanceof Element ? e.target.closest('[data-v-on]') : null;
      if (!t) return;
      const action = parseOn(t, eventType);
      if (!action) return;
      e.preventDefault();
      if (opts && opts.debounce) {
        clearTimeout(bindOn._t);
        bindOn._t = setTimeout(() => dispatchAction(t, action), opts.debounce);
      } else {
        dispatchAction(t, action);
      }
    },
    true
  );
}

bindOn('click');
bindOn('change');

document.addEventListener(
  'input',
  (e) => {
    const t = e.target instanceof Element ? e.target.closest('[data-v-on]') : null;
    if (!t) return;
    const action = parseOn(t, 'input');
    if (!action) return;
    e.preventDefault();
    clearTimeout(bindOn._t);
    bindOn._t = setTimeout(() => dispatchAction(t, action), 150);
  },
  true
);

document.addEventListener(
  'submit',
  (e) => {
    const form = e.target;
    if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-v-form')) return;
    const source =
      (e.submitter instanceof Element && e.submitter.closest('[data-v-on]')) ||
      (form instanceof Element ? form.closest('[data-v-on]') : null);
    if (!source) return; // plain submit: let the no-JS fallback POST handle it
    const action = parseOn(source, 'submit') || parseOn(source, 'click');
    if (!action) return;
    e.preventDefault();
    dispatchAction(source, action);
  },
  true
);

// ---------------------------------------------------------------------------
// client-side router
// ---------------------------------------------------------------------------

/** url -> { at, html } */
const navCache = new Map();
const NAV_CACHE_MAX = 20;
const NAV_CACHE_TTL = 30000;
let navController = null;

function urlOf(a) {
  return a.pathname + a.search;
}

function interceptable(a) {
  if (!a || !a.getAttribute) return false;
  const target = a.getAttribute('target');
  if (target && target !== '_self') return false;
  const rel = a.getAttribute('rel') || '';
  if (a.hasAttribute('download')) return false;
  if (rel.indexOf('external') !== -1) return false;
  if (a.hasAttribute('data-v-external')) return false;
  const href = a.getAttribute('href') || '';
  return href.charAt(0) === '/' && href.charAt(1) !== '/';
}

function cachedDoc(url) {
  const hit = navCache.get(url);
  if (!hit) return null;
  if (Date.now() - hit.at > NAV_CACHE_TTL) {
    navCache.delete(url);
    return null;
  }
  return hit.html;
}

function cacheDoc(url, html) {
  navCache.set(url, { at: Date.now(), html: html });
  if (navCache.size > NAV_CACHE_MAX) {
    navCache.delete(navCache.keys().next().value);
  }
}

function escapeAttrValue(s) {
  return String(s).replace(/"/g, '\\"');
}

function morphNode(oc, nc) {
  let i;
  const oldAttrs = Array.from(oc.attributes);
  for (i = 0; i < oldAttrs.length; i++) {
    if (!nc.hasAttribute(oldAttrs[i].name)) oc.removeAttribute(oldAttrs[i].name);
  }
  const newAttrs = Array.from(nc.attributes);
  for (i = 0; i < newAttrs.length; i++) {
    if (oc.getAttribute(newAttrs[i].name) !== newAttrs[i].value) {
      oc.setAttribute(newAttrs[i].name, newAttrs[i].value);
    }
  }
  oc.innerHTML = nc.innerHTML;
}

function morphBody(newDoc) {
  const oldBody = document.body;
  const newChildren = Array.from(newDoc.body.children);
  let cursor = null;
  for (let i = 0; i < newChildren.length; i++) {
    const nc = newChildren[i];
    const key = nc.getAttribute('data-v-key');
    let placed = nc;
    if (key) {
      let oc = oldBody.querySelector(':scope > [data-v-key="' + escapeAttrValue(key) + '"]');
      if (oc) {
        morphNode(oc, nc);
        placed = oc;
      }
    }
    const ref = cursor ? cursor.nextSibling : oldBody.firstChild;
    if (ref !== placed) oldBody.insertBefore(placed, ref);
    cursor = placed;
  }
  if (cursor) {
    while (cursor.nextSibling) oldBody.removeChild(cursor.nextSibling);
  } else if (newChildren.length === 0) {
    while (oldBody.firstChild) oldBody.removeChild(oldBody.firstChild);
  }
}

async function navigate(url, push) {
  if (navController) navController.abort();
  navController = new AbortController();
  const signal = navController.signal;
  let html = cachedDoc(url);
  try {
    if (html === null) {
      const res = await fetch(url, { headers: { 'X-Volt-Navigate': '1' }, signal: signal });
      if (!res.ok) {
        window.location.href = url;
        return;
      }
      html = await res.text();
      cacheDoc(url, html);
    }
    const doc = new DOMParser().parseFromString(html, 'text/html');
    morphBody(doc);
    document.title = doc.title || document.title;
    collectIslands(document.body);
    observeLinks();
    if (push) {
      history.pushState({ volt: true, scroll: window.scrollY }, '', url);
      window.scrollTo(0, 0);
    }
  } catch (err) {
    if (err && err.name !== 'AbortError') window.location.href = url;
  }
}

document.addEventListener(
  'click',
  (e) => {
    if (e.defaultPrevented) return;
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    const a = e.target instanceof Element ? e.target.closest('a[href]') : null;
    if (!a || !interceptable(a)) return;
    e.preventDefault();
    navigate(urlOf(a), true);
  },
  false
);

window.addEventListener('popstate', (e) => {
  navigate(window.location.pathname + window.location.search, false).then(() => {
    const scroll = e.state && typeof e.state.scroll === 'number' ? e.state.scroll : 0;
    window.scrollTo(0, scroll);
  });
});

// ---------------------------------------------------------------------------
// prefetch
// ---------------------------------------------------------------------------

const prefetched = new Set();

function prefetch(url) {
  if (!url || url.charAt(0) !== '/' || navCache.has(url) || prefetched.has(url)) return;
  prefetched.add(url);
  fetch(url, { headers: { 'X-Volt-Prefetch': '1' } })
    .then((res) => (res.ok ? res.text() : Promise.reject(res.status)))
    .then((html) => cacheDoc(url, html))
    .catch(() => {
      prefetched.delete(url);
    });
}

const linkObserver = 'IntersectionObserver' in window
  ? new IntersectionObserver(
      (entries) => {
        for (let i = 0; i < entries.length; i++) {
          if (entries[i].isIntersecting) {
            const a = entries[i].target;
            linkObserver.unobserve(a);
            if (interceptable(a)) prefetch(urlOf(a));
          }
        }
      },
      { rootMargin: '100px' }
    )
  : null;

function observeLinks() {
  if (!linkObserver) return;
  const links = document.querySelectorAll('a[href^="/"]');
  for (let i = 0; i < links.length; i++) {
    const a = links[i];
    if (interceptable(a)) linkObserver.observe(a);
  }
}

document.addEventListener(
  'pointerenter',
  (e) => {
    if (e.target instanceof Element === false) return;
    const a = e.target.closest('a[href]');
    if (a && interceptable(a)) prefetch(urlOf(a));
  },
  true
);

document.addEventListener(
  'focusin',
  (e) => {
    if (e.target instanceof Element === false) return;
    const a = e.target.closest('a[href]');
    if (a && interceptable(a)) prefetch(urlOf(a));
  },
  true
);

// ---------------------------------------------------------------------------
// boot
// ---------------------------------------------------------------------------

function boot() {
  collectIslands(document.body);
  observeLinks();
  history.replaceState({ volt: true, scroll: window.scrollY }, '');
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
