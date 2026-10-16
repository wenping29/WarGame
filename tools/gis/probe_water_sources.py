"""从 hydrosheds.org 下载页提取真实文件 URL。用完可删。"""
import re
import urllib.request

UA = {"User-Agent": "wargame-probe/1.0"}
PAGES = [
    "https://www.hydrosheds.org/hydrosheds-core-downloads",
    "https://www.hydrosheds.org/",
]

if __name__ == "__main__":
    urls = set()
    for page in PAGES:
        try:
            r = urllib.request.urlopen(urllib.request.Request(page, headers=UA), timeout=60)
            html = r.read().decode("utf-8", "replace")
        except Exception as exc:  # noqa: BLE001
            print("PAGE %s ERR %s" % (page, repr(exc)[:120]))
            continue
        found = re.findall(r'https?://[^"\'<>\s]+?\.(?:zip|shp|gpkg|tif|dbf|pbf)', html, re.I)
        print("PAGE %s -> %d links" % (page, len(found)))
        urls.update(found)
        # 也抓相对链接，方便人工判断目录结构
        for m in re.findall(r'href="([^"]+)"', html)[:200]:
            if any(k in m.lower() for k in ("hydro", "download", "file")):
                print("   href:", m[:120])
    for u in sorted(urls)[:40]:
        print("URL:", u[:150])
