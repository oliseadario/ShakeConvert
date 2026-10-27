# Clip Studio Paint (.clip) -> full-resolution PNG, entirely local.
#   clipengine.py <input.clip> <output.png>
# 1) clip_to_psd.py (MIT, https://github.com/dobrokot/clip_to_psd) rebuilds the layers into a PSD
# 2) psd-tools (MIT) merges the layers with their blend modes -> final image
import os
import runpy
import shutil
import sys
import tempfile
import warnings

warnings.filterwarnings("ignore")


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: clipengine.py input.clip output.png", file=sys.stderr)
        return 2
    src, out = sys.argv[1], sys.argv[2]
    here = os.path.dirname(os.path.abspath(__file__))
    tmp = tempfile.mkdtemp(prefix="shakeconvert_clip_")
    psd = os.path.join(tmp, "canvas.psd")
    try:
        argv = sys.argv
        sys.argv = ["clip_to_psd.py", src, "-o", psd, "--blank-psd-preview", "--log-level", "WARNING"]
        try:
            runpy.run_path(os.path.join(here, "clip_to_psd.py"), run_name="__main__")
        except SystemExit as e:
            if e.code not in (0, None):
                raise
        finally:
            sys.argv = argv
        print("(1/2) Page", file=sys.stderr, flush=True)

        from psd_tools import PSDImage
        image = PSDImage.open(psd).composite(force=True)
        if image is None:
            print("no visible layers", file=sys.stderr)
            return 1
        image.save(out, "PNG")
        print("(2/2) Page", file=sys.stderr, flush=True)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
