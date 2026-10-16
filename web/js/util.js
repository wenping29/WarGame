// 极简 DOM 与提示工具，无框架。
// 属性值为 null/undefined/false 时跳过：布尔属性（disabled 等）只要存在即生效，写 false 会把按钮禁掉。
export function el(tag, attrs = {}, children) {
  const e = document.createElement(tag);
  for (const k in attrs) {
    const v = attrs[k];
    if (v == null || v === false) continue;
    if (k === 'class') e.className = v;
    else if (k === 'html') e.innerHTML = v;
    else if (k.startsWith('on') && typeof v === 'function') e.addEventListener(k.slice(2), v);
    else if (k === 'value') e.value = v;
    else if (k === 'checked') e.checked = !!v;
    else e.setAttribute(k, v);
  }
  if (children != null) append(e, children);
  return e;
}
export function append(parent, children) {
  const list = Array.isArray(children) ? children : [children];
  for (const c of list) {
    if (c == null || c === false) continue;
    if (Array.isArray(c)) { append(parent, c); continue; }
    parent.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
  }
  return parent;
}
export function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }

let toastTimer = 0;
export function toast(msg, kind = 'ok', ms = 2600) {
  const box = document.getElementById('toast');
  if (!box) return;
  const item = el('div', { class: 'toast-item ' + (kind === 'ok' ? '' : kind) }, String(msg));
  box.appendChild(item);
  setTimeout(() => item.remove(), ms);
  // 防抖：过多提示时移除最旧
  while (box.children.length > 5) box.removeChild(box.firstChild);
}

export function fmt(n, d = 0) {
  if (n == null || isNaN(n)) return '-';
  return Number(n).toFixed(d);
}
export function pad(n) { return n < 10 ? '0' + n : '' + n; }
export function hhmm(hourOfDay) {
  const h = ((hourOfDay % 24) + 24) % 24;
  return pad(h) + ':' + pad(Math.round((h % 1) * 60));
}
export function q2(a) { return JSON.stringify(a); }
export const uid = (() => { let c = 0; return (p = 'k') => p + (++c); })();
