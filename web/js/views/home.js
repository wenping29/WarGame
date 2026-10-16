// 首页：只给三个入口——创建副本 / 一键开局 / 存档副本；其余一览都在大厅，不在这里重复。
import { el, clear } from '../util.js';
import { getMeta, router } from '../router.js';
import { quickStart, saveList } from './lobby.js';

export async function renderHome(root) {
  clear(root);
  const saves = (await getMeta()).saves || [];
  const list = el('div', { class: 'home-list' });

  const tile = (title, hint, onclick) => el('button', { class: 'home-tile', onclick }, [
    el('b', {}, title), el('span', { class: 'hint' }, hint),
  ]);
  const toggleSaves = () => { if (list.firstChild) clear(list); else list.replaceChildren(saveList(saves)); };

  root.appendChild(el('div', { class: 'page home-page' }, [
    el('div', { class: 'home' }, [
      tile('创建副本', '按剧本自定义部队与战场环境', () => router.go('#/new')),
      tile('一键开局', '默认剧本 · 零配置 · 直接进导演部', () => quickStart()),
      tile('存档副本', saves.length ? saves.length + ' 个副本可载入' : '暂无存档', toggleSaves),
    ]),
    list,
  ]));
}
