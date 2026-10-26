# -*- coding: utf-8 -*-
"""按 docs/ 各战役文档的『模板对应与修正建议』批量更新 data/scenarios/*.json。
备份已于 2026-10-19 生成：data/scenarios_backup_20261019。
用法：python tools/update_scenarios_from_docs.py
"""
import json, os, copy

SCEN = "D:/Project/wargame/data/scenarios"

def cmd(side, name, q):
    return {"op": "cmd", "side": side, "name": name, "q": q}

def mv(side, typ, at, occ=0):
    return {"op": "move", "side": side, "typ": typ, "at": at, "occ": occ}

def sp(side, typ, positions):
    """把该类型第一个条目按 positions 拆分为多组（均分 qty，其余字段保留）。"""
    return {"op": "split", "side": side, "typ": typ, "positions": positions}

def split(side, typ, positions):
    """把该类型条目按 positions 分组（均分 qty，其余字段保留）。幂等：若已拆分为多组则仅重新定位。"""
    return {"op": "split", "side": side, "typ": typ, "positions": positions}

def align(light_side="red"):
    """幂等对调：把轻步兵体系 OOB 归位到 light_side，机械化体系（mec/tank/radar/recon）归位到另一侧。"""
    return {"op": "align_bodies", "light_side": light_side}

def sched(*seg):
    return {"op": "schedule", "segs": [{"from": a, "to": b, "weather": w} for a, b, w in seg]}

def vic(side, old, new):
    return {"op": "victory", "side": side, "old": old, "new": new}

def desc(text):
    return {"op": "desc", "text": text}

def flags(**kw):
    return {"op": "flags", "kw": kw}

UPDATES = {
 # ===== 抗战系列 =====
 "lugouqiao_1937": {
   "ops": [
     cmd("red", "冯治安（第37师·卢沟桥当面）", 0.72),
     cmd("blue", "河边正三（驻屯步兵旅团长）", 0.68),
     mv("red", "hq", [13, 9]),
     mv("red", "light_inf", [13, 9], 0),
     sp("red", "light_inf", [[13, 9], [12, 10], [14, 12]]),
     mv("red", "rifle", [12, 9], 0),
     sp("red", "rifle", [[12, 9], [13, 10]]),
     mv("red", "gun", [12, 11]), mv("red", "arty", [11, 9]),
     mv("red", "engineer", [11, 10]), mv("red", "signals", [11, 11]),
     mv("red", "aa", [12, 8]),
     mv("blue", "hq", [18, 9]),
     sp("blue", "light_inf", [[17, 8], [17, 10], [18, 9]]),
     sp("blue", "rifle", [[18, 8], [18, 10]]),
     mv("blue", "gun", [17, 9]), mv("blue", "arty", [19, 9]),
     mv("blue", "engineer", [16, 9]), mv("blue", "signals", [19, 10]),
     mv("blue", "recy", [20, 9]),
   ]},
 "mukden_1931": {
   "ops": [
     cmd("red", "赵镇藩（第7旅参谋长·当夜主事）", 0.62),
     cmd("blue", "本庄繁（关东军司令官）", 0.65),
     mv("red", "hq", [14, 10]),
     sp("red", "light_inf", [[13, 10], [14, 11], [13, 9]]),
     mv("red", "rifle", [14, 11], 0),
     sp("red", "rifle", [[14, 11], [15, 10]]),
     mv("red", "gun", [15, 10]), mv("red", "arty", [13, 11]),
     mv("red", "engineer", [14, 10]), mv("red", "signals", [13, 11]),
     mv("red", "recy", [11, 10]), mv("red", "aa", [13, 9]),
     mv("blue", "hq", [13, 5]),
     sp("blue", "light_inf", [[13, 7], [14, 8], [12, 7]]),
     sp("blue", "rifle", [[14, 9], [13, 8]]),
     mv("blue", "gun", [13, 6]), mv("blue", "arty", [14, 7]),
     mv("blue", "engineer", [12, 6]), mv("blue", "signals", [14, 6]),
     mv("blue", "recy", [15, 6]),
   ]},
 "songhu_1937": {
   "ops": [
     cmd("red", "张治中（第9集团军总司令）", 0.72),
     cmd("blue", "松井石根（上海派遣军司令官）", 0.70),
     mv("red", "hq", [7, 10]),
     mv("red", "light_inf", [9, 9], 0), mv("red", "rifle", [9, 12]),
     mv("red", "gun", [8, 11]), mv("red", "arty", [7, 9]),
     mv("red", "engineer", [8, 10]), mv("red", "signals", [8, 11]),
     mv("red", "aa", [8, 7]),
     mv("blue", "hq", [21, 10]),
     sp("blue", "light_inf", [[19, 9], [19, 12]]),
     sp("blue", "rifle", [[20, 10], [21, 13]]),
     mv("blue", "gun", [20, 11]), mv("blue", "arty", [21, 9]),
     mv("blue", "engineer", [20, 10]), mv("blue", "signals", [22, 11]),
     mv("blue", "recy", [23, 10]),
   ]},
 "nanjing_1937": {
   "ops": [
     cmd("red", "唐生智（卫戍军总司令）", 0.55),
     cmd("blue", "朝香宫鸠彦（上海派遣军司令官）", 0.68),
     mv("red", "hq", [10, 10]),
     mv("red", "light_inf", [9, 9], 0), mv("red", "rifle", [9, 12]),
     mv("red", "gun", [8, 11]), mv("red", "arty", [7, 9]),
     mv("red", "engineer", [9, 10]), mv("red", "signals", [8, 11]),
     mv("red", "aa", [9, 7]),
     mv("blue", "hq", [20, 10]),
     sp("blue", "light_inf", [[18, 9], [18, 12]]),
     sp("blue", "rifle", [[19, 10], [17, 13]]),
     mv("blue", "gun", [19, 11]), mv("blue", "arty", [20, 9]),
     mv("blue", "engineer", [19, 10]), mv("blue", "signals", [21, 11]),
     mv("blue", "recy", [22, 10]),
   ]},
 "taierzhuang_1938": {
   "ops": [
     cmd("red", "孙连仲（第2集团军总司令）", 0.78),
     cmd("blue", "濑谷启（第33旅团长·濑谷支队）", 0.62),
     mv("red", "hq", [9, 9]),
     sp("red", "light_inf", [[12, 9], [11, 10], [10, 7]]),
     mv("red", "rifle", [12, 10], 0),
     sp("red", "rifle", [[12, 10], [11, 11]]),
     mv("red", "gun", [9, 10]), mv("red", "arty", [9, 8]),
     mv("red", "engineer", [10, 9]), mv("red", "signals", [9, 11]),
     mv("red", "recy", [7, 10]), mv("red", "aa", [10, 7]),
     mv("blue", "hq", [13, 3]),
     sp("blue", "light_inf", [[12, 5], [13, 6]]),
     sp("blue", "rifle", [[13, 4], [12, 6]]),
     mv("blue", "gun", [12, 3]), mv("blue", "arty", [13, 3]),
     mv("blue", "engineer", [11, 3]), mv("blue", "signals", [13, 2]),
     mv("blue", "recy", [14, 2]),
   ]},
 "xuzhou_1938": {
   "ops": [
     cmd("red", "李宗仁（第五战区司令长官）", 0.85),
     cmd("blue", "寺内寿一（华北方面军司令官）", 0.75),
     mv("red", "hq", [9, 10]),
     mv("red", "light_inf", [10, 9], 0), mv("red", "rifle", [10, 11]),
     mv("red", "gun", [9, 11]), mv("red", "arty", [8, 10]),
     mv("red", "engineer", [9, 10]), mv("red", "signals", [8, 11]),
     mv("red", "aa", [9, 7]),
     mv("blue", "hq", [14, 3]),
     sp("blue", "light_inf", [[13, 4], [14, 17]]),
     sp("blue", "rifle", [[15, 5], [14, 18]]),
     mv("blue", "gun", [13, 3]), mv("blue", "arty", [12, 3]),
     mv("blue", "engineer", [13, 2]), mv("blue", "signals", [14, 2]),
     mv("blue", "recy", [15, 2]),
   ]},
 "wuhan_1938": {
   "ops": [
     cmd("red", "陈诚（第九战区司令长官）", 0.72),
     cmd("blue", "冈村宁次（第11军司令官）", 0.80),
     mv("red", "hq", [7, 10]),
     mv("red", "light_inf", [10, 8], 0), mv("red", "rifle", [10, 12]),
     mv("red", "gun", [8, 11]), mv("red", "arty", [7, 9]),
     mv("red", "engineer", [9, 10]), mv("red", "signals", [8, 11]),
     mv("red", "aa", [9, 7]),
     mv("blue", "hq", [22, 10]),
     sp("blue", "light_inf", [[20, 9], [21, 12]]),
     sp("blue", "rifle", [[19, 10], [22, 12]]),
     mv("blue", "gun", [21, 10]), mv("blue", "arty", [22, 9]),
     mv("blue", "engineer", [20, 10]), mv("blue", "signals", [22, 11]),
     mv("blue", "recy", [23, 10]),
   ]},
 "changsha_1941": {
   "ops": [
     cmd("red", "薛岳（第九战区司令长官）", 0.85),
     cmd("blue", "阿南惟几（第11军司令官）", 0.65),
     mv("red", "hq", [9, 9]),
     sp("red", "light_inf", [[11, 8], [10, 10], [10, 7]]),
     mv("red", "rifle", [10, 11], 0),
     sp("red", "rifle", [[10, 11], [9, 11]]),
     mv("red", "gun", [9, 10]), mv("red", "arty", [9, 9]),
     mv("red", "engineer", [10, 9]), mv("red", "signals", [9, 11]),
     mv("red", "recy", [7, 10]), mv("red", "aa", [10, 7]),
     mv("blue", "hq", [12, 2]),
     sp("blue", "light_inf", [[11, 5], [13, 5]]),
     sp("blue", "rifle", [[11, 6], [13, 6]]),
     mv("blue", "gun", [11, 4]), mv("blue", "arty", [12, 3]),
     mv("blue", "engineer", [11, 3]), mv("blue", "signals", [12, 4]),
     mv("blue", "recy", [12, 2]),
     sched((1, 4, "overcast"), (5, 7, "clear"), (8, 13, "overcast")),
   ]},
 "zhongtiaoshan_1941": {
   "ops": [
     cmd("red", "曾万钟（第5集团军总司令）", 0.55),
     cmd("blue", "多田骏（华北方面军司令官）", 0.78),
     flags(strict_supply=True),
     mv("red", "hq", [7, 10]),
     sp("red", "light_inf", [[10, 9], [11, 11], [16, 6]]),
     sp("red", "rifle", [[13, 13], [14, 5], [12, 12]]),
     mv("red", "gun", [9, 10]), mv("red", "arty", [8, 10]),
     mv("red", "engineer", [10, 10]), mv("red", "signals", [7, 10]),
     mv("red", "aa", [10, 8]),
     mv("blue", "hq", [14, 3]),
     sp("blue", "light_inf", [[12, 4], [20, 4]]),
     sp("blue", "rifle", [[21, 11], [4, 13]]),
     mv("blue", "gun", [12, 3]), mv("blue", "arty", [13, 2]),
     mv("blue", "engineer", [12, 2]), mv("blue", "signals", [13, 2]),
     mv("blue", "recy", [14, 2]),
   ]},
 "shipai_1943": {
   "ops": [
     cmd("red", "胡琏（第11师师长）", 0.82),
     cmd("blue", "澄田赉四郎（第39师团长）", 0.62),
     mv("red", "hq", [10, 10]),
     sp("red", "light_inf", [[10, 9], [9, 11], [8, 8]]),
     mv("red", "rifle", [9, 11], 0),
     sp("red", "rifle", [[9, 11], [8, 10]]),
     mv("red", "gun", [8, 10]), mv("red", "arty", [8, 9]),
     mv("red", "engineer", [9, 10]), mv("red", "signals", [8, 11]),
     mv("red", "recy", [6, 10]), mv("red", "aa", [9, 8]),
     mv("blue", "hq", [18, 10]),
     sp("blue", "light_inf", [[16, 9], [17, 11], [16, 12]]),
     sp("blue", "rifle", [[17, 10], [15, 10]]),
     mv("blue", "gun", [17, 9]), mv("blue", "arty", [18, 9]),
     mv("blue", "engineer", [16, 10]), mv("blue", "signals", [18, 11]),
     mv("blue", "recy", [19, 10]),
   ]},
 # ===== 解放战争系列 =====
 "menglianggu_1947": {
   "ops": [
     cmd("red", "陈毅·粟裕（华野司令员兼政委·负责战役指挥）", 0.88),
     cmd("blue", "张灵甫（整编第74师师长）", 0.72),
   ]},
 "huaihai_1948": {
   "ops": [
     cmd("red", "粟裕（华野代司令员兼代政委）", 0.90),
     cmd("blue", "杜聿明（徐州剿总副总司令·前进指挥部主任）", 0.68),
     sched((1, 4, "overcast"), (9, 12, "snow"), (13, 15, "clear")),
   ]},
 "liaoshen_1948": {
   "ops": [
     cmd("red", "林彪·罗荣桓（东北野战军司令员·政委）", 0.88),
     cmd("blue", "卫立煌（东北剿总总司令）", 0.60),
     sched((8, 9, "overcast"), (11, 13, "overcast"), (14, 16, "clear")),
   ]},
 "pingjin_1949": {
   "ops": [
     cmd("red", "林彪·刘亚楼（东北野战军司令员·参谋长）", 0.88),
     cmd("blue", "陈长捷（天津警备司令）", 0.62),
     sched((1, 5, "clear"), (6, 8, "snow"), (10, 11, "clear"), (12, 12, "overcast")),
   ]},
 "yudong_1948": {
   "ops": [
     cmd("red", "粟裕（华野代司令员兼代政委·全权指挥）", 0.92),
     cmd("blue", "李仲辛（整编第66师师长·开封城防）", 0.50),
     mv("blue", "hq", [12, 10]),
     mv("blue", "rifle", [11, 9], 0), mv("blue", "rifle", [12, 11], 1),
     mv("blue", "rifle_auto", [14, 10]), mv("blue", "tank", [17, 9]),
     mv("blue", "gun", [13, 10]), mv("blue", "arty", [14, 11]),
     mv("blue", "aa", [12, 9]), mv("blue", "engineer", [12, 10]),
     mv("blue", "recy", [11, 10]),
     vic("red", "16,10", "12,10"),
   ]},
 "dujiang_1949": {"ops": []},  # 已含史实数据，跳过
 # ===== 抗美援朝系列 =====
 "korea_c1_1950": {
   "ops": [
     cmd("red", "彭德怀（志愿军司令员兼政委）", 0.88),
     cmd("blue", "沃克（美第8集团军司令官）", 0.68),
     flags(night_from=19, night_to=6),
     sched((1, 2, "clear"), (3, 6, "overcast"), (8, 9, "rain"), (10, 14, "clear")),
   ]},
 "korea_c2_1950": {
   "ops": [
     cmd("red", "彭德怀（志愿军司令员兼政委）", 0.88),
     cmd("blue", "沃克（美第8集团军司令官）", 0.65),
   ]},
 "korea_c3_1950": {
   "ops": [
     cmd("red", "彭德怀（志愿军司令员兼政委）", 0.85),
     cmd("blue", "李奇微（美第8集团军司令官）", 0.75),
     flags(night_from=19, night_to=6),
   ]},
 "korea_c4_1951": {
   "ops": [
     cmd("red", "彭德怀（志愿军司令员兼政委）", 0.82),
     cmd("blue", "李奇微（美第8集团军司令官·霹雳/屠夫/撕裂者行动）", 0.78),
   ]},
 "korea_c5_1951": {
   "ops": [
     cmd("red", "彭德怀（志愿军司令员兼政委）", 0.85),
     cmd("blue", "范弗里特（美第8集团军司令官）", 0.78),
   ]},
 "hanjiang_1951": {
   "ops": [
     align(),
     cmd("red", "曾泽生（第50军军长）", 0.80),
     cmd("blue", "李奇微（美第8集团军司令官）", 0.75),
     flags(night=True, night_from=18, night_to=6),
     sched((1, 6, "snow"), (7, 9, "overcast"), (10, 14, "clear")),
     mv("red", "hq", [12, 14]),
     sp("red", "light_inf", [[10, 14], [14, 15]]),
     mv("red", "rifle", [12, 13]), mv("red", "arty", [10, 13]),
     mv("red", "engineer", [12, 12]), mv("red", "signals", [10, 12]),
     mv("red", "recy", [9, 13]),
     mv("blue", "mec_inf", [20, 10]), mv("blue", "tank", [21, 11]),
     mv("blue", "gun", [21, 12]), mv("blue", "arty", [20, 13]),
     mv("blue", "aa", [20, 9]), mv("blue", "radar", [21, 10]),
   ]},
 "tieyuan_1951": {
   "ops": [
     align(),
     cmd("red", "傅崇碧（第63军军长）", 0.82),
     cmd("blue", "范弗里特（美第8集团军司令官）", 0.75),
     mv("red", "hq", [8, 10]),
     sp("red", "light_inf", [[9, 10], [10, 12]]),
     mv("red", "rifle", [10, 13]), mv("red", "arty", [8, 9]),
     mv("red", "engineer", [9, 11]), mv("red", "signals", [8, 11]),
     mv("red", "recy", [7, 10]),
     mv("blue", "mec_inf", [19, 10]), mv("blue", "tank", [21, 9]),
     mv("blue", "gun", [21, 10]), mv("blue", "arty", [20, 8]),
     mv("blue", "aa", [20, 10]), mv("blue", "radar", [21, 11]),
   ]},
 "shangganling_1952": {
   "ops": [
     align(),
     cmd("red", "秦基伟（第15军军长）", 0.85),
     cmd("blue", "范弗里特（美第8集团军司令官）", 0.72),
     mv("red", "hq", [10, 9]),
     sp("red", "light_inf", [[11, 9], [12, 10]]),
     mv("red", "rifle", [10, 10]), mv("red", "arty", [9, 9]),
     mv("red", "engineer", [11, 10]), mv("red", "signals", [9, 10]),
     mv("red", "recy", [8, 9]),
     mv("blue", "mec_inf", [18, 9]), mv("blue", "tank", [19, 8]),
     mv("blue", "gun", [19, 10]), mv("blue", "arty", [18, 10]),
     mv("blue", "aa", [18, 8]), mv("blue", "radar", [19, 11]),
   ]},
 # ===== 二战欧洲 =====
 "rhine_corridor": {
   "ops": [
     cmd("red", "蒙哥马利（第21集团军群司令）", 0.90),
     cmd("blue", "布拉斯科维茨（H集团军群司令）", 0.55),
     desc("1945年3月，盟军第21集团军群自莱茵河西岸渡河突破（战利品行动+大学校队行动），东岸河防集群迟滞阻击，鲁尔合围收官。默认使用真实 GIS 六角格数据包；若无数据包则程序化地形降级。"),
   ]},
}

def find_entries(oob, typ):
    return [u for u in oob if u.get("type") == typ]

def apply_ops(d, ops):
    log = []
    for op in ops:
        s = op["side"] if "side" in op else None
        if op["op"] == "cmd":
            hq = find_entries(d["sides"][s]["oob"], "hq")[0]
            hq["commander"] = {"name": op["name"], "quality": op["q"]}
            log.append(f"{s}.hq指挥官→{op['name']}@{op['q']}")
        elif op["op"] == "move":
            es = find_entries(d["sides"][s]["oob"], op["typ"])
            es[op["occ"]]["at"] = list(op["at"])
            log.append(f"{s}.{op['typ']}[{op['occ']}]→{op['at']}")
        elif op["op"] == "split":
            oob = d["sides"][s]["oob"]
            es = [i for i, u in enumerate(oob) if u.get("type") == op["typ"]]
            if not es:
                raise KeyError(f"split: {s} 无 {op['typ']} 条目")
            if len(es) == 1:  # 未拆分：拆成多组
                idx = es[0]
                u = oob[idx]
                n = len(op["positions"])
                base = u.get("qty", 1) // n
                rem = u.get("qty", 1) - base * (n - 1)
                newq = [base] * (n - 1) + [rem]
                fresh = []
                for k, at in enumerate(op["positions"]):
                    e = copy.deepcopy(u); e["qty"] = newq[k]; e["at"] = list(at)
                    fresh.append(e)
                oob[idx:idx + 1] = fresh
            else:  # 已拆分：仅重新定位（幂等）
                for k, at in enumerate(op["positions"]):
                    if k < len(es):
                        oob[es[k]]["at"] = list(at)
            log.append(f"{s}.{op['typ']}分组→{op['positions']}")
        elif op["op"] == "align_bodies":
            MEC = {"mec_inf", "tank", "radar", "recon_air"}
            ls, bs = op["light_side"], ("blue" if op["light_side"] == "red" else "red")
            def is_mec(oob):
                return any(u.get("type") in MEC for u in oob)
            light_oob, mec_oob = d["sides"][ls]["oob"], d["sides"][bs]["oob"]
            if is_mec(light_oob) and not is_mec(mec_oob):
                d["sides"][ls]["oob"], d["sides"][bs]["oob"] = mec_oob, light_oob
                log.append(f"OOB体系归位：{ls}=轻步兵体（原机械化体对调回 {bs}）")
            elif is_mec(light_oob) and is_mec(mec_oob):
                raise ValueError("align_bodies: 两侧都含机械化条目，无法判别")
            else:
                log.append("OOB体系归位：已是目标状态，跳过")
        elif op["op"] == "flags":
            d["flags"].update(op["kw"])
            log.append(f"flags.update({op['kw']})")
        elif op["op"] == "schedule":
            d["weather"]["schedule"] = op["segs"]
            log.append("weather.schedule 已按文档建议写入")
        elif op["op"] == "victory":
            for v in d["victory"]:
                if v["side"] == op["side"] and v["kind"] == "capture" and v["target"] == op["old"]:
                    v["target"] = op["new"]
            log.append(f"victory {op['side']} capture {op['old']}→{op['new']}")
        elif op["op"] == "desc":
            d["description"] = op["text"]
            log.append("description 已更新")
    return log

changed = {}
for fid, spec in UPDATES.items():
    p = os.path.join(SCEN, fid + ".json")
    d = json.load(open(p, encoding="utf-8"))
    log = apply_ops(d, spec["ops"])
    if log:
        json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        changed[fid] = log

total = sum(len(v) for v in changed.values())
print(f"更新剧本 {len(changed)} 个，共 {total} 项变更\n")
for fid, log in changed.items():
    print(f"### {fid}（{len(log)} 项）")
    for l in log:
        print("  -", l)
