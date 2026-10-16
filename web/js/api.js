// REST：全部同源相对路径，绝不硬编码端口。
async function jget(path) {
  const r = await fetch(path, { headers: { 'accept': 'application/json' } });
  return r.json();
}
async function jpost(path, body) {
  const r = await fetch(path, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body || {}),
  });
  // 服务端 400/404 也返回 JSON，交由调用方判断 error/errors
  return r.json();
}

export const api = {
  scenarios: () => jget('/api/scenarios'),
  rooms: () => jget('/api/rooms'),
  room: (code) => jget('/api/room/' + code),
  frames: (code) => jget('/api/frames/' + code),
  validate: (scenario, overrides) => jpost('/api/validate', { scenario, overrides }),
  createInstance: (body) => jpost('/api/instances', body),
  loadSave: (instance) => jpost('/api/saves/load', { instance }),
};
