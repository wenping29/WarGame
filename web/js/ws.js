// WebSocket 会话：连接、消息分发、断线重连。
// 服务端消息类型：welcome|state|events|saved|frames|error|kicked|pong
import { toast } from './util.js';

export class RoomSocket {
  constructor(code, role, opts = {}) {
    this.code = code;
    this.role = role;
    this.name = opts.name;
    this.token = opts.token || localStorage.getItem('hf.tok.' + role) || undefined;
    this.force = opts.force ? '1' : undefined;
    this.handlers = {};
    this.sock = null;
    this.alive = false;
    this.closedByUser = false;
    this.kicked = false;
    this._retry = 0;
  }

  on(type, fn) { (this.handlers[type] ||= []).push(fn); return this; }
  _emit(type, msg) { (this.handlers[type] || []).forEach((f) => { try { f(msg); } catch (e) { console.error(e); } }); }

  connect() {
    const q = ['room=' + encodeURIComponent(this.code), 'role=' + encodeURIComponent(this.role)];
    if (this.token) q.push('token=' + encodeURIComponent(this.token));
    if (this.name) q.push('name=' + encodeURIComponent(this.name));
    if (this.force) q.push('force=1');
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const url = proto + '://' + location.host + '/ws?' + q.join('&');
    const sock = new WebSocket(url);
    this.sock = sock;
    sock.onopen = () => { this.alive = true; this._retry = 0; this._emit('open', {}); };
    sock.onclose = () => {
      this.alive = false;
      if (this.kicked) return; // 被同角色新终端接管：不再重连，否则会互相顶号死循环
      this._emit('close', {});
      if (!this.closedByUser) this._reconnect();
    };
    sock.onerror = () => this._emit('error', { message: '连接错误' });
    sock.onmessage = (ev) => {
      let m;
      try { m = JSON.parse(ev.data); } catch (_) { return; }
      if (m.type === 'welcome' && m.token) {
        this.token = m.token;
        try { localStorage.setItem('hf.tok.' + this.role, m.token); } catch (_) {}
      }
      if (m.type === 'kicked') {
        this.kicked = true;
        this.closedByUser = true;
        try { sock.close(); } catch (_) {}
        toast(m.reason || '本角色已在其他终端打开', 'warn', 6000);
        this._emit('kicked', m);
        return;
      }
      this._emit(m.type || 'message', m);
    };
    return this;
  }

  _reconnect() {
    this._retry++;
    if (this._retry > 8) {
      this.closedByUser = true;
      this._emit('close', { reason: '重连次数过多，已停止（角色位可能被占用或服务离线）' });
      return;
    }
    const delay = Math.min(1000 * this._retry, 5000);
    this._emit('reconnecting', { delay, attempt: this._retry });
    setTimeout(() => { if (!this.closedByUser) this.connect(); }, delay);
  }

  send(obj) {
    if (this.sock && this.sock.readyState === 1) { this.sock.send(JSON.stringify(obj)); return true; }
    return false;
  }
  close() { this.closedByUser = true; try { this.sock && this.sock.close(); } catch (_) {} }
}
