#!/usr/bin/env python3
"""把 HexFront 的前后端分离产物打成一个 zip（ZIP64）。

为什么不直接用 Compress-Archive / zip 命令：
  · PowerShell 5.1 的 Compress-Archive 打包 2G 级数据极慢，且有单文件 2GB 上限；
  · zip 命令在 Git-Bash 环境里通常没装。
Python 的 zipfile 支持 ZIP64、可精��控制排除项、可分档设置压缩级别。

用法：
  python3 pack_release.py --out <输出zip> --root <zip内顶层目录名> \
      --web <前端dist目录> --publish <后端publish目录> \
      --db <app.db> --data <server/data目录> --deploy <部署物料目录> \
      [--gisapi <生成服务publish目录>]
"""
import argparse
import os
import sys
import time
import zipfile


# 目录名级排除：命中任一层即跳过整个子树
EXCLUDE_DIR_NAMES = {
    ".git", "node_modules", "bin", "obj", "__pycache__",
    "saves", "terrain_test",
    "gis_axial_backup", "scenarios_backup_20261019", "scenario_backups",
    ".vs", ".idea", ".vscode",
}

# 文件名级排除
EXCLUDE_FILE_NAMES = {
    "tianditu.json",          # 天地图真实 token，部署时单独放
    ".DS_Store", "Thumbs.db",
    "*.db-shm", "*.db-wal",    # SQLite 临时文件（打包前已 checkpoint）
}

# 后缀级排除（编译中间产物）
EXCLUDE_SUFFIXES = (".pdb", ".xml")

# 这些后缀的数据已是压缩栅格，deflate 收益 <10% 却慢 5 倍以上 → 存不压缩
STORE_SUFFIXES = (".tif", ".ovr", ".png", ".jpg", ".jpeg", ".gz", ".zip", ".woff", ".woff2")


def is_excluded_dir(name: str) -> bool:
    return name in EXCLUDE_DIR_NAMES


def is_excluded_file(name: str) -> bool:
    if name in EXCLUDE_FILE_NAMES:
        return True
    if name.endswith(EXCLUDE_SUFFIXES):
        return True
    return False


def human(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024:
            return f"{n:.1f}{unit}"
        n /= 1024
    return f"{n:.1f}TB"


def add_dir(zf: zipfile.ZipFile, src: str, prefix: str, counter: dict) -> None:
    """递归加入目录，prefix 是 zip 内的目标路径前缀（不含顶层目录名）。"""
    for dirpath, dirnames, filenames in os.walk(src):
        dirnames[:] = sorted(d for d in dirnames if not is_excluded_dir(d))
        rel_dir = os.path.relpath(dirpath, src).replace("\\", "/")
        if rel_dir == ".":
            rel_dir = ""
        for fname in sorted(filenames):
            if is_excluded_file(fname):
                counter["excluded"] += 1
                continue
            full = os.path.join(dirpath, fname)
            if not os.path.isfile(full):
                continue
            arc = f"{prefix}/{rel_dir}/{fname}" if rel_dir else f"{prefix}/{fname}"
            level = 1 if fname.endswith(STORE_SUFFIXES) else 6
            zf.write(full, arc, compress_type=zipfile.ZIP_DEFLATED, compresslevel=level)
            counter["files"] += 1
            counter["bytes"] += os.path.getsize(full)


def add_file(zf: zipfile.ZipFile, src: str, prefix: str, counter: dict) -> None:
    if not os.path.isfile(src):
        print(f"  ! 跳过不存在的文件: {src}", file=sys.stderr)
        return
    level = 1 if src.endswith(STORE_SUFFIXES) else 6
    zf.write(src, prefix, compress_type=zipfile.ZIP_DEFLATED, compresslevel=level)
    counter["files"] += 1
    counter["bytes"] += os.path.getsize(src)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--root", required=True, help="zip 内的顶层目录名")
    ap.add_argument("--web")
    ap.add_argument("--publish")
    ap.add_argument("--db")
    ap.add_argument("--data")
    ap.add_argument("--deploy")
    ap.add_argument("--gisapi")
    args = ap.parse_args()

    t0 = time.time()
    counter = {"files": 0, "bytes": 0, "excluded": 0}

    print(f"==> 打包 {args.out}")
    # allowZip64=True：单个 tif 623MB + 总量 2.1G，必须开 ZIP64
    with zipfile.ZipFile(args.out, "w", zipfile.ZIP_DEFLATED, allowZip64=True) as zf:
        if args.web:
            print("  · 前端静态站 web/")
            add_dir(zf, args.web, f"{args.root}/web", counter)
        if args.publish:
            print("  · 后端 publish/ + app.db")
            add_dir(zf, args.publish, f"{args.root}/server/HexFront.Api/publish", counter)
        if args.gisapi:
            print("  · 生成服务 HexFront.GisApi publish/")
            add_dir(zf, args.gisapi, f"{args.root}/server/HexFront.GisApi/publish", counter)
        if args.db:
            add_file(zf, args.db, f"{args.root}/server/HexFront.Api/app.db", counter)
        if args.data:
            print("  · 数据 server/data/（2G 级，压缩需几分钟）")
            add_dir(zf, args.data, f"{args.root}/server/data", counter)
        if args.deploy:
            print("  · 部署物料 deploy/")
            add_dir(zf, args.deploy, f"{args.root}/deploy", counter)

    size = os.path.getsize(args.out)
    print(f"==> 完成：{counter['files']} 个文件，原始 {human(counter['bytes'])} "
          f"→ zip {human(size)}（排除 {counter['excluded']} 项），耗时 {time.time() - t0:.0f}s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
