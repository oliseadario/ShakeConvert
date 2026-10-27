# ShakeConvert's local "do-it-all" engine. No network connections.
#   omni.py <operation> <input> <output> [options]
# operations: data, vcard, font, mesh, cad, geo, trace, eml, pdfsvg, heic
# Output text follows the app language (SHAKECONVERT_LANG = "it" or "en").
import csv
import io
import json
import os
import sys
import warnings
import zipfile

warnings.filterwarnings("ignore")

IT = os.environ.get("SHAKECONVERT_LANG", "en") == "it"


def T(it, en):
    return it if IT else en


def ext(p):
    e = os.path.splitext(p)[1].lower().lstrip(".")
    return {"yml": "yaml", "ndjson": "jsonl", "htm": "html"}.get(e, e)


# ------------------------------------------------------------------ structured data

def flatten(obj, prefix=""):
    out = {}
    if isinstance(obj, dict):
        for k, v in obj.items():
            out.update(flatten(v, f"{prefix}{k}."))
    elif isinstance(obj, list) and not all(isinstance(x, (str, int, float, bool, type(None))) for x in obj):
        for i, v in enumerate(obj):
            out.update(flatten(v, f"{prefix}{i}."))
    elif isinstance(obj, list):
        out[prefix[:-1]] = "; ".join("" if x is None else str(x) for x in obj)
    else:
        out[prefix[:-1]] = "" if obj is None else obj
    return out


def records(data):
    """Any structure -> list of flat rows (for CSV/TSV)."""
    if isinstance(data, dict):
        lists = [v for v in data.values() if isinstance(v, list) and v and all(isinstance(x, dict) for x in v)]
        if len(data) == 1 and lists:
            data = lists[0]
        elif not lists:
            data = [data]
        else:
            data = lists[0]
    if not isinstance(data, list):
        data = [{"value": data}]
    return [flatten(r) if isinstance(r, (dict, list)) else {"value": r} for r in data]


def unflatten(row):
    out = {}
    for k, v in row.items():
        cur = out
        parts = k.split(".")
        for p in parts[:-1]:
            cur = cur.setdefault(p, {})
        cur[parts[-1]] = v
    return out


def no_none(x):
    if isinstance(x, dict):
        return {str(k): no_none(v) for k, v in x.items() if v is not None}
    if isinstance(x, list):
        return [no_none(v) for v in x if v is not None]
    return x


def read_data(p):
    e = ext(p)
    with open(p, "rb") as f:
        raw = f.read()
    text = raw.decode("utf-8-sig", errors="replace")
    if e == "json":
        return json.loads(text)
    if e == "jsonl":
        return [json.loads(l) for l in text.splitlines() if l.strip()]
    if e == "yaml":
        import yaml
        docs = [d for d in yaml.safe_load_all(text)]
        return docs[0] if len(docs) == 1 else docs
    if e == "toml":
        import tomllib
        return tomllib.loads(text)
    if e == "xml":
        import xmltodict
        return xmltodict.parse(text)
    if e in ("csv", "tsv"):
        sample = text[:4096]
        delim = "\t" if e == "tsv" else max([",", ";", "\t"], key=sample.count)
        return [unflatten(r) for r in csv.DictReader(io.StringIO(text), delimiter=delim)]
    raise ValueError(T("formato dati non supportato: ", "unsupported data format: ") + e)


def write_data(data, p):
    e = ext(p)
    if e == "json":
        s = json.dumps(data, ensure_ascii=False, indent=2, default=str)
    elif e == "jsonl":
        items = data if isinstance(data, list) else [data]
        s = "\n".join(json.dumps(x, ensure_ascii=False, default=str) for x in items) + "\n"
    elif e == "yaml":
        import yaml
        s = yaml.safe_dump(data, allow_unicode=True, sort_keys=False)
    elif e == "toml":
        import tomli_w
        d = data if isinstance(data, dict) else {"items": data}
        s = tomli_w.dumps(no_none(d))
    elif e == "xml":
        import xmltodict
        d = data
        if not (isinstance(d, dict) and len(d) == 1):
            d = {"root": {"item": d} if isinstance(d, list) else d}
        s = xmltodict.unparse(no_none(d), pretty=True)
    elif e in ("csv", "tsv"):
        rows = records(data)
        cols = []
        for r in rows:
            for k in r:
                if k not in cols:
                    cols.append(k)
        buf = io.StringIO()
        w = csv.DictWriter(buf, fieldnames=cols, delimiter="\t" if e == "tsv" else ",", extrasaction="ignore")
        w.writeheader()
        w.writerows(rows)
        s = buf.getvalue()
    else:
        raise ValueError(T("formato dati non supportato: ", "unsupported data format: ") + e)
    with open(p, "w", encoding="utf-8-sig" if e in ("csv", "tsv") else "utf-8", newline="") as f:
        f.write(s)


def op_data(src, dst):
    write_data(read_data(src), dst)


# ------------------------------------------------------------------ vCard

def op_vcard(src, dst):
    if ext(src) == "vcf":
        text = open(src, encoding="utf-8", errors="replace").read().replace("\r\n ", "").replace("\n ", "")
        rows, cur = [], None
        for line in text.splitlines():
            u = line.upper()
            if u.startswith("BEGIN:VCARD"):
                cur = {}
            elif u.startswith("END:VCARD"):
                if cur is not None:
                    rows.append(cur)
                cur = None
            elif cur is not None and ":" in line:
                key, val = line.split(":", 1)
                name = key.split(";")[0].upper()
                label = {"FN": T("Nome", "Name"), "N": T("Nome strutturato", "Structured name"), "TEL": T("Telefono", "Phone"),
                         "EMAIL": "Email", "ORG": T("Azienda", "Company"), "TITLE": T("Ruolo", "Title"), "ADR": T("Indirizzo", "Address"),
                         "BDAY": T("Compleanno", "Birthday"), "NOTE": T("Note", "Notes"), "URL": T("Sito", "Website")}.get(name)
                if not label:
                    continue
                if name == "ADR":
                    val = ", ".join(x for x in val.split(";") if x)
                if label in cur:
                    i = 2
                    while f"{label} {i}" in cur:
                        i += 1
                    label = f"{label} {i}"
                cur[label] = val
        write_data(rows, dst if ext(dst) in ("csv", "tsv", "json") else dst)
    else:
        rows = read_data(src)
        out = []
        for r in rows:
            r = flatten(r)
            get = lambda *names: next((str(r[k]) for k in r for n in names if k.lower().startswith(n)), "")
            name = get("nome", "name", "fn", "full")
            out.append("BEGIN:VCARD\r\nVERSION:3.0\r\n")
            out.append(f"FN:{name}\r\nN:{name};;;;\r\n")
            for k, v in r.items():
                kl = k.lower()
                if not v:
                    continue
                if kl.startswith(("tel", "phone", "cell")):
                    out.append(f"TEL:{v}\r\n")
                elif kl.startswith(("email", "mail")):
                    out.append(f"EMAIL:{v}\r\n")
                elif kl.startswith(("azienda", "org", "company")):
                    out.append(f"ORG:{v}\r\n")
                elif kl.startswith(("note",)):
                    out.append(f"NOTE:{v}\r\n")
            out.append("END:VCARD\r\n")
        open(dst, "w", encoding="utf-8", newline="").write("".join(out))


# ------------------------------------------------------------------ font

def op_font(src, dst):
    from fontTools.ttLib import TTFont
    font = TTFont(src)
    e = ext(dst)
    if e == "ttf" and "CFF " in font:
        # cubic outlines (OTF/CFF) -> quadratic (TTF)
        from fontTools.pens.cu2quPen import Cu2QuPen
        from fontTools.pens.ttGlyphPen import TTGlyphPen
        from fontTools.ttLib import newTable
        glyphs = font.getGlyphSet()
        glyf = newTable("glyf")
        glyf.glyphOrder = font.getGlyphOrder()
        glyf.glyphs = {}
        for name in glyf.glyphOrder:
            pen = TTGlyphPen(glyphs)
            glyphs[name].draw(Cu2QuPen(pen, 1.0, reverse_direction=True))
            glyf.glyphs[name] = pen.glyph()
        font["glyf"] = glyf
        font["loca"] = newTable("loca")
        font["maxp"].tableVersion = 0x00010000
        for attr in ("maxZones", "maxTwilightPoints", "maxStorage", "maxFunctionDefs", "maxInstructionDefs",
                     "maxStackElements", "maxSizeOfInstructions", "maxComponentElements"):
            setattr(font["maxp"], attr, 0)
        font["maxp"].maxZones = 1
        font["maxp"].maxComponentDepth = 0
        font["head"].indexToLocFormat = 0
        font["post"].formatType = 2.0
        font["post"].extraNames = []
        font["post"].mapping = {}
        font["post"].glyphOrder = None
        del font["CFF "]
        if "VORG" in font:
            del font["VORG"]
        font.sfntVersion = "\x00\x01\x00\x00"
    font.flavor = {"woff": "woff", "woff2": "woff2"}.get(e)
    font.save(dst)


# ------------------------------------------------------------------ 3D models

def op_mesh(src, dst):
    import trimesh
    scene = trimesh.load(src, force="scene")
    e = ext(dst)
    if e in ("stl", "ply", "obj", "off"):
        mesh = scene.to_geometry() if hasattr(scene, "to_geometry") else scene.dump(concatenate=True)
        mesh.export(dst)
    else:
        scene.export(dst)


# ------------------------------------------------------------------ CAD (DXF)

def op_cad(src, dst):
    import ezdxf
    from ezdxf.addons.drawing import Frontend, RenderContext, layout
    doc = ezdxf.readfile(src)
    msp = doc.modelspace()
    e = ext(dst)
    page = layout.Page(0, 0, layout.Units.mm, margins=layout.Margins.all(10))
    if e == "svg":
        from ezdxf.addons.drawing import svg
        backend = svg.SVGBackend()
        Frontend(RenderContext(doc), backend).draw_layout(msp)
        open(dst, "w", encoding="utf-8").write(backend.get_string(page))
    else:
        from ezdxf.addons.drawing import pymupdf
        backend = pymupdf.PyMuPdfBackend()
        Frontend(RenderContext(doc), backend).draw_layout(msp)
        if e == "pdf":
            open(dst, "wb").write(backend.get_pdf_bytes(page))
        else:
            open(dst, "wb").write(backend.get_pixmap_bytes(page, fmt="png", dpi=200))


# ------------------------------------------------------------------ geodata

def geo_read(src):
    e = ext(src)
    feats = []
    if e in ("geojson", "json"):
        d = json.load(open(src, encoding="utf-8-sig"))
        feats = d["features"] if d.get("type") == "FeatureCollection" else [d] if d.get("type") == "Feature" else [{"type": "Feature", "geometry": d, "properties": {}}]
    elif e in ("kml", "kmz"):
        import xml.etree.ElementTree as ET
        data = zipfile.ZipFile(src).read(next(n for n in zipfile.ZipFile(src).namelist() if n.lower().endswith(".kml"))) if e == "kmz" else open(src, "rb").read()
        root = ET.fromstring(data)
        def coords(el):
            return [[float(c) for c in t.split(",")[:2]] for t in (el.text or "").split()]
        for pm in root.iter():
            if not pm.tag.endswith("Placemark"):
                continue
            props = {}
            for ch in pm:
                if ch.tag.endswith("name") or ch.tag.endswith("description"):
                    props[ch.tag.split("}")[-1]] = (ch.text or "").strip()
            for g in pm.iter():
                t = g.tag.split("}")[-1]
                c = next((x for x in g if x.tag.endswith("coordinates")), None)
                if c is None:
                    continue
                if t == "Point":
                    feats.append({"type": "Feature", "properties": props, "geometry": {"type": "Point", "coordinates": coords(c)[0]}})
                elif t == "LineString":
                    feats.append({"type": "Feature", "properties": props, "geometry": {"type": "LineString", "coordinates": coords(c)}})
                elif t == "LinearRing":
                    feats.append({"type": "Feature", "properties": props, "geometry": {"type": "Polygon", "coordinates": [coords(c)]}})
    elif e == "gpx":
        import xml.etree.ElementTree as ET
        root = ET.parse(src).getroot()
        for el in root.iter():
            t = el.tag.split("}")[-1]
            name = next((c.text for c in el if c.tag.endswith("name")), None)
            if t == "wpt":
                feats.append({"type": "Feature", "properties": {"name": name or ""}, "geometry": {"type": "Point", "coordinates": [float(el.get("lon")), float(el.get("lat"))]}})
            elif t in ("trkseg", "rte"):
                pts = [[float(p.get("lon")), float(p.get("lat"))] for p in el if p.tag.split("}")[-1] in ("trkpt", "rtept")]
                if pts:
                    feats.append({"type": "Feature", "properties": {"name": name or ""}, "geometry": {"type": "LineString", "coordinates": pts}})
    elif e in ("shp", "zip"):
        import shapefile
        if e == "zip":
            import tempfile
            tmp = tempfile.mkdtemp()
            zipfile.ZipFile(src).extractall(tmp)
            shps = [os.path.join(r, f) for r, _, fs in os.walk(tmp) for f in fs if f.lower().endswith(".shp")]
        else:
            shps = [src]
        for shp in shps:
            r = shapefile.Reader(shp)
            fields = [f[0] for f in r.fields[1:]]
            for sr in r.iterShapeRecords():
                feats.append({"type": "Feature", "properties": dict(zip(fields, sr.record)), "geometry": sr.shape.__geo_interface__})
    elif e == "csv":
        for row in read_data(src):
            row = flatten(row)
            keys = {k.lower(): k for k in row}
            lon = next((keys[k] for k in ("lon", "lng", "longitude", "long", "x") if k in keys), None)
            lat = next((keys[k] for k in ("lat", "latitude", "y") if k in keys), None)
            if lon and lat:
                try:
                    feats.append({"type": "Feature", "properties": {k: v for k, v in row.items() if k not in (lon, lat)},
                                  "geometry": {"type": "Point", "coordinates": [float(str(row[lon]).replace(",", ".")), float(str(row[lat]).replace(",", "."))]}})
                except ValueError:
                    pass
    else:
        raise ValueError(T("formato geografico non supportato: ", "unsupported geodata format: ") + e)
    return feats


def esc(s):
    return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def geo_write(feats, dst):
    e = ext(dst)
    if e in ("geojson", "json"):
        json.dump({"type": "FeatureCollection", "features": feats}, open(dst, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    elif e in ("kml", "kmz"):
        out = ['<?xml version="1.0" encoding="UTF-8"?>\n<kml xmlns="http://www.opengis.net/kml/2.2"><Document>']
        for f in feats:
            g, p = f.get("geometry") or {}, f.get("properties") or {}
            name = p.get("name") or p.get("Name") or p.get("Nome") or ""
            t, c = g.get("type"), g.get("coordinates")
            cs = lambda pts: " ".join(f"{x[0]},{x[1]}" for x in pts)
            geom = {"Point": lambda: f"<Point><coordinates>{c[0]},{c[1]}</coordinates></Point>",
                    "LineString": lambda: f"<LineString><coordinates>{cs(c)}</coordinates></LineString>",
                    "Polygon": lambda: f"<Polygon><outerBoundaryIs><LinearRing><coordinates>{cs(c[0])}</coordinates></LinearRing></outerBoundaryIs></Polygon>"}.get(t)
            if geom:
                out.append(f"<Placemark><name>{esc(name)}</name>{geom()}</Placemark>")
        out.append("</Document></kml>")
        kml = "".join(out)
        if e == "kmz":
            with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
                z.writestr("doc.kml", kml)
        else:
            open(dst, "w", encoding="utf-8").write(kml)
    elif e == "gpx":
        out = ['<?xml version="1.0" encoding="UTF-8"?>\n<gpx version="1.1" creator="ShakeConvert" xmlns="http://www.topografix.com/GPX/1/1">']
        for f in feats:
            g, p = f.get("geometry") or {}, f.get("properties") or {}
            name = esc(p.get("name") or "")
            if g.get("type") == "Point":
                x, y = g["coordinates"][:2]
                out.append(f'<wpt lat="{y}" lon="{x}"><name>{name}</name></wpt>')
            elif g.get("type") in ("LineString", "Polygon"):
                pts = g["coordinates"] if g["type"] == "LineString" else g["coordinates"][0]
                out.append(f"<trk><name>{name}</name><trkseg>" + "".join(f'<trkpt lat="{q[1]}" lon="{q[0]}"/>' for q in pts) + "</trkseg></trk>")
        out.append("</gpx>")
        open(dst, "w", encoding="utf-8").write("".join(out))
    elif e == "csv":
        rows = []
        for f in feats:
            g = f.get("geometry") or {}
            row = dict(f.get("properties") or {})
            if g.get("type") == "Point":
                row["lon"], row["lat"] = g["coordinates"][:2]
            else:
                row[T("geometria", "geometry")] = json.dumps(g)
            rows.append(row)
        write_data(rows, dst)
    elif e == "zip":
        # a shapefile holds a single geometry type: one each for points, lines and polygons
        import shapefile, tempfile
        tmp = tempfile.mkdtemp()
        stem = os.path.splitext(os.path.basename(dst))[0]
        groups = {"Point": (T("punti", "points"), shapefile.POINT), "LineString": (T("linee", "lines"), shapefile.POLYLINE), "Polygon": (T("poligoni", "polygons"), shapefile.POLYGON)}
        prj = 'GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]]'
        kinds = [k for k in groups if any((f.get("geometry") or {}).get("type") == k for f in feats)]
        for kind in kinds:
            suffix, st = groups[kind]
            sub = [f for f in feats if (f.get("geometry") or {}).get("type") == kind]
            base = os.path.join(tmp, stem if len(kinds) == 1 else f"{stem}_{suffix}")
            w = shapefile.Writer(base, shapeType=st)
            keys = []
            for f in sub:
                for k in (f.get("properties") or {}):
                    if k not in keys:
                        keys.append(k)
            for k in keys:
                w.field(str(k)[:10], "C", 254)
            if not keys:
                w.field("id", "N")
            for i, f in enumerate(sub):
                c = f["geometry"]["coordinates"]
                if st == shapefile.POINT:
                    w.point(*c[:2])
                elif st == shapefile.POLYLINE:
                    w.line([c])
                else:
                    w.poly(c)
                p = f.get("properties") or {}
                w.record(*([str(p.get(k, ""))[:254] for k in keys] if keys else [i]))
            w.close()
            open(base + ".prj", "w").write(prj)
        with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
            for fn in os.listdir(tmp):
                z.write(os.path.join(tmp, fn), fn)
    else:
        raise ValueError(T("formato geografico non supportato: ", "unsupported geodata format: ") + e)


def op_geo(src, dst):
    feats = geo_read(src)
    if not feats:
        raise ValueError(T("nessun elemento geografico trovato", "no geographic features found"))
    geo_write(feats, dst)


# ------------------------------------------------------------------ vectorization, e-mail, PDF → SVG

def op_trace(src, dst):
    import vtracer
    vtracer.convert_image_to_svg_py(src, dst, colormode="color", mode="spline", filter_speckle=4, color_precision=6)


def op_eml(src, dst):
    from email import policy
    from email.parser import BytesParser
    msg = BytesParser(policy=policy.default).parse(open(src, "rb"))
    head = [(k, msg.get(k, "")) for k in ("From", "To", "Cc", "Date", "Subject")]
    body = msg.get_body(preferencelist=("html", "plain"))
    content = body.get_content() if body else ""
    is_html = body is not None and body.get_content_type() == "text/html"
    attachments = [a.get_filename() for a in msg.iter_attachments() if a.get_filename()]
    if ext(dst) == "txt":
        import re
        text = re.sub(r"<[^>]+>", "", content) if is_html else content
        lines = [f"{k}: {v}" for k, v in head if v]
        if attachments:
            lines.append(T("Allegati: ", "Attachments: ") + ", ".join(attachments))
        open(dst, "w", encoding="utf-8").write("\n".join(lines) + "\n\n" + text)
    else:
        h = "".join(f"<tr><th>{k}</th><td>{esc(v)}</td></tr>" for k, v in head if v)
        if attachments:
            h += f"<tr><th>{T('Allegati', 'Attachments')}</th><td>{esc(', '.join(attachments))}</td></tr>"
        inner = content if is_html else "<pre style='white-space:pre-wrap'>" + esc(content) + "</pre>"
        open(dst, "w", encoding="utf-8").write(
            "<!doctype html><meta charset='utf-8'><title>" + esc(msg.get("Subject", "")) + "</title>"
            "<style>body{font-family:'Segoe UI',sans-serif;max-width:900px;margin:32px auto;padding:0 20px}"
            "table{border-collapse:collapse;margin-bottom:24px}th{text-align:left;padding:4px 12px 4px 0;color:#555}</style>"
            "<table>" + h + "</table>" + inner)


def op_pdfsvg(src, dst):
    import pymupdf
    doc = pymupdf.open(src)
    if len(doc) == 1:
        open(dst, "w", encoding="utf-8").write(doc[0].get_svg_image(text_as_path=False))
        return
    os.makedirs(dst, exist_ok=True)
    name = os.path.splitext(os.path.basename(src))[0]
    for i, page in enumerate(doc):
        open(os.path.join(dst, f"{name} - {T('pagina', 'page')} {i + 1:02d}.svg"), "w", encoding="utf-8").write(page.get_svg_image(text_as_path=False))


def op_heic(src, dst, quality="85"):
    # HEIC with the HEVC (x265) encoder bundled in pillow-heif
    import pillow_heif
    from PIL import Image
    pillow_heif.register_heif_opener()
    im = Image.open(src)
    if im.mode not in ("RGB", "RGBA"):
        im = im.convert("RGBA" if "A" in im.getbands() else "RGB")
    im.save(dst, format="HEIF", quality=max(1, min(100, int(quality))))


OPS = {"data": op_data, "vcard": op_vcard, "font": op_font, "mesh": op_mesh, "cad": op_cad,
       "geo": op_geo, "trace": op_trace, "eml": op_eml, "pdfsvg": op_pdfsvg, "heic": op_heic}


def main():
    try:
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
    if len(sys.argv) < 4 or sys.argv[1] not in OPS:
        print("usage: omni.py " + "|".join(OPS) + " input output", file=sys.stderr)
        return 2
    try:
        OPS[sys.argv[1]](sys.argv[2], sys.argv[3], *sys.argv[4:])
    except Exception as ex:
        print(f"{type(ex).__name__}: {ex}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
