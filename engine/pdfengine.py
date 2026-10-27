# ShakeConvert's local PDF reconstruction engine (no network, no API).
#   pdfengine.py docx   <input.pdf> <output.docx>   -> editable Word (pdf2docx)
#   pdfengine.py tables <input.pdf> <output.json>   -> tables of every page, for Excel
# Progress is written to stderr as "(page/total) Page".
import json
import logging
import sys
import warnings

warnings.filterwarnings("ignore")


def main() -> int:
    if len(sys.argv) < 4:
        print("usage: pdfengine.py docx|tables input output", file=sys.stderr)
        return 2
    mode, src, out = sys.argv[1], sys.argv[2], sys.argv[3]
    try:
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(message)s")

    from pdf2docx import Converter

    cv = Converter(src)
    try:
        if mode == "docx":
            cv.convert(out, multi_processing=False)
        elif mode == "tables":
            total = len(cv.fitz_doc)
            pages = []
            for i in range(total):
                try:
                    tables = cv.extract_tables(start=i, end=i + 1)
                except Exception:
                    tables = []
                pages.append([[["" if c is None else str(c) for c in row] for row in t] for t in tables])
                print(f"({i + 1}/{total}) Page", file=sys.stderr, flush=True)
            with open(out, "w", encoding="utf-8") as f:
                json.dump(pages, f, ensure_ascii=False)
        else:
            print("unknown mode: " + mode, file=sys.stderr)
            return 2
    finally:
        cv.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
