// Minimal static renderer for the .dc.html mockups so they open in a plain browser.
// Supports what the mockups use: {{ path }} holes in text and attributes, <sc-for>, <sc-if>,
// and a `class Component extends DCLogic { renderVals() {…} }` logic block. Not interactive.
(function () {
  window.DCLogic = class DCLogic {
    constructor() { this.props = {}; this.state = {}; }
    setState(s) { Object.assign(this.state, s); }
    forceUpdate() {}
  };

  const HOLE = /\{\{\s*([^}]+?)\s*\}\}/g;

  function lookup(expr, scope) {
    if (expr === 'true') return true;
    if (expr === 'false') return false;
    if (/^-?\d+(\.\d+)?$/.test(expr)) return Number(expr);
    return expr.split('.').reduce((v, k) => (v == null ? undefined : v[k]), scope);
  }

  function whole(value, scope) {
    const m = /^\s*\{\{\s*([^}]+?)\s*\}\}\s*$/.exec(value);
    return m ? lookup(m[1], scope) : undefined;
  }

  const interpolate = (text, scope) => text.replace(HOLE, (_, e) => {
    const v = lookup(e, scope);
    return v == null ? '' : String(v);
  });

  function render(node, scope, out) {
    if (node.nodeType === Node.TEXT_NODE) {
      out.push(document.createTextNode(interpolate(node.nodeValue, scope)));
      return;
    }
    if (node.nodeType !== Node.ELEMENT_NODE) return;
    const tag = node.localName;
    if (tag === 'helmet') return;
    if (tag === 'sc-for') {
      const list = whole(node.getAttribute('list'), scope) || [];
      const as = node.getAttribute('as') || 'item';
      list.forEach((item, i) => {
        const inner = Object.assign(Object.create(scope), { [as]: item, $index: i });
        node.childNodes.forEach((c) => render(c, inner, out));
      });
      return;
    }
    if (tag === 'sc-if') {
      if (whole(node.getAttribute('value'), scope)) node.childNodes.forEach((c) => render(c, scope, out));
      return;
    }
    const el = node.cloneNode(false);
    for (const a of Array.from(node.attributes)) {
      if (a.name.startsWith('hint-')) { el.removeAttribute(a.name); continue; }
      if (a.value.includes('{{')) el.setAttribute(a.name, interpolate(a.value, scope));
    }
    const kids = [];
    node.childNodes.forEach((c) => render(c, scope, kids));
    kids.forEach((k) => el.appendChild(k));
    out.push(el);
  }

  document.addEventListener('DOMContentLoaded', () => {
    const script = document.querySelector('script[data-dc-script]');
    const root = document.querySelector('x-dc');
    if (!script || !root) return;
    const Component = new Function('DCLogic', script.textContent + '\nreturn Component;')(window.DCLogic);
    const logic = new Component();
    const vals = logic.renderVals() || {};

    root.querySelectorAll('helmet > *').forEach((h) => document.head.appendChild(h.cloneNode(true)));
    const out = [];
    root.childNodes.forEach((c) => render(c, vals, out));
    root.replaceWith(...out);
    document.documentElement.dataset.rendered = 'true';
  });
})();
