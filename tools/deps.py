#!/usr/bin/env python3
"""CX-57: the pinned audio runtime, fetched once and checked by sha256.

    tools/deps.py <name> <dest>

Unpacks the named archive's wanted members under <dest> (idempotent: a stamp file names the sha it came from).
The archives cache in $CX_DEPS_CACHE (default ~/.cache/cx-deps), so a clean build does not re-download 180 MB.

  openal-src   OpenAL Soft 1.25.2 source (LGPL-2.1): the Linux libopenal.so.1 is built from it, and its AL
               headers (efx-presets.h included) are what native/cxaudio.c compiles against on both OSes.
  openal-win   the upstream Win64 soft_oal.dll of the same release, shipped as cx_openal.dll.
  steamaudio   Steam Audio 4.8.1 (Apache-2.0): libphonon.so / phonon.dll only -- the headers are vendored in
               third_party/steamaudio.
"""
import hashlib, os, pathlib, shutil, sys, tarfile, urllib.request, zipfile

DEPS = {
    'openal-src': ('https://github.com/kcat/openal-soft/archive/refs/tags/1.25.2.tar.gz',
                   'fb27e5839aa11f0e5b9d33756965291fad5d6909ab928ea1f796f4a1a6877894', 'openal-soft-1.25.2.tar.gz',
                   None),
    'openal-win': ('https://github.com/kcat/openal-soft/releases/download/1.25.2/openal-soft-1.25.2-bin.zip',
                   '67a0c4b800bd860c93c04f38caf8cbe4875f9c84700ac430efc451f70e265434', 'openal-soft-1.25.2-bin.zip',
                   ('openal-soft-1.25.2-bin/bin/Win64/soft_oal.dll', 'openal-soft-1.25.2-bin/COPYING')),
    'steamaudio': ('https://github.com/ValveSoftware/steam-audio/releases/download/v4.8.1/steamaudio_4.8.1.zip',
                   '4a0aa5ec1176f38f0b0993a37c2259d9e86f27e22d5e24f83ec4c3cb9a1d5449', 'steamaudio_4.8.1.zip',
                   ('steamaudio/lib/linux-x64/libphonon.so', 'steamaudio/lib/windows-x64/phonon.dll')),
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def fetch(name):
    url, want, file, _ = DEPS[name]
    cache = pathlib.Path(os.environ.get('CX_DEPS_CACHE', pathlib.Path.home() / '.cache' / 'cx-deps'))
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / file
    if not path.exists() or sha256(path) != want:
        print(f'>> fetching {url}', file=sys.stderr)
        tmp = path.with_suffix('.part')
        with urllib.request.urlopen(url) as r, open(tmp, 'wb') as f:
            shutil.copyfileobj(r, f)
        got = sha256(tmp)
        if got != want:
            tmp.unlink()
            sys.exit(f'!! {file}: sha256 {got} != pinned {want}')
        tmp.replace(path)
    return path


def main():
    name, dest = sys.argv[1], pathlib.Path(sys.argv[2])
    _, want, _, members = DEPS[name]
    stamp = dest / f'.{name}.sha256'
    if stamp.exists() and stamp.read_text().strip() == want:
        return
    path = fetch(name)
    dest.mkdir(parents=True, exist_ok=True)
    if path.name.endswith('.tar.gz'):
        with tarfile.open(path) as t:
            t.extractall(dest, filter='data')
    else:
        with zipfile.ZipFile(path) as z:
            for m in members:
                out = dest / pathlib.Path(m).name
                with z.open(m) as src, open(out, 'wb') as dst:
                    shutil.copyfileobj(src, dst)
    stamp.write_text(want)


if __name__ == '__main__':
    main()
