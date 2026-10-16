"""探测河流/湖泊候选源可达性与体量。用完可删。"""
import json
import urllib.request
import xml.etree.ElementTree as ET

UA = {"User-Agent": "wargame-probe/1.0"}


def head(url, limit=300):
    try:
        req = urllib.request.Request(url, headers=UA)
        r = urllib.request.urlopen(req, timeout=30)
        return r.status, r.read(limit)
    except Exception as exc:  # noqa: BLE001
        return 0, repr(exc)[:130].encode()


def s3_list(bucket, prefix="", delimiter="/", maxkeys=20):
    url = ("https://%s.s3.amazonaws.com/?list-type=2&max-keys=%d&prefix=%s"
           "&delimiter=%s" % (bucket, maxkeys, urllib.request.quote(prefix), delimiter))
    st, body = head(url, 200000)
    if st != 200:
        print("  %-40s -> %s %s" % (prefix or "<root>", st, body[:100]))
        return
    root = ET.fromstring(body)
    ns = "{http://s3.amazonaws.com/doc/2006-03-01/}"
    pre = [p.text for p in root.iter(ns + "CommonPrefixes")]
    obj = [(c.text, int(next((k.text for k in o.iter(ns + "Size")
                             if k.tag == ns + "Size"), 0))) for o in root.iter(ns + "Contents") for c in o.iter(ns + "Key")]
    print("  prefixes:", pre[:24])
    for k, s in obj[:24]:
        print("     %-58s %12.1f MB" % (k[:58], s / 1048576.0))


TARGETS = [
    ("zenodo-record", "https://zenodo.org/api/records/7059630"),
    ("zenodo-files", "https://zenodo.org/api/records/7059585"),
    ("files-zone", "https://files.zone/"),
    ("osmfr-extracts", "https://download.openstreetmap.fr/extracts/"),
    ("geofabrik", "https://download.geofabrik.de/"),
    ("hydrosheds", "https://data.hydrosheds.org/"),
]

def zenodo_files(rid):
    try:
        r = urllib.request.urlopen(urllib.request.Request(
            "https://zenodo.org/api/records/%s" % rid, headers=UA), timeout=60)
        d = json.load(r)
    except Exception as exc:  # noqa: BLE001
        print("  zenodo %s -> ERR %s" % (rid, repr(exc)[:110]))
        return
    print("  zenodo %s title: %s" % (rid, d.get("metadata", {}).get("title", "?")[:90]))
    for f in d.get("files", [])[:25]:
        print("     %-58s %10.1f MB" % (f["key"][:58], f["size"] / 1048576.0))


if __name__ == "__main__":
    for name, url in TARGETS:
        st, body = head(url, 160)
        print("%-14s %s %s" % (name, st, body[:120]))
    zenodo_files("7059630")
    zenodo_files("7059585")
    print("osm-pds root:"); s3_list("osm-pds")
    print("osm-pds planet:"); s3_list("osm-pds", "planet/")
