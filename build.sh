#!/usr/bin/env sh
set -e
cd "$(dirname "$0")"
mkdir -p build

if [ ! -f build/libfunchook.so ]; then
  echo ">> building funchook (once)..."
  cmake -S third_party/funchook -B build/funchook \
    -DFUNCHOOK_BUILD_SHARED=ON -DFUNCHOOK_BUILD_STATIC=OFF \
    -DFUNCHOOK_BUILD_TESTS=OFF -DCMAKE_BUILD_TYPE=Release >/dev/null
  cmake --build build/funchook -j >/dev/null
  cp -a build/funchook/libfunchook.so* build/
fi

# #206: the extraction boundary. managed/Alloy/Scene/ holds the RE knowledge -- which engine field means
# what -- and that is the part a future backend would not rewrite, so it stays free of GL. A call that has
# to live there says why on its own line with a `gl-ok:` marker.
# ...including the `using static` that would let every call through as a bare name.
if grep -rnE '\bGl\.|using static .*\bGl;' managed/Alloy/Scene/ | grep -v 'gl-ok:'; then
  echo "!! Alloy/Scene must not call GL (see the lines above; #206). Move it to the submission side," >&2
  echo "!! or keep it and write the reason on the same line as a 'gl-ok: <why>' comment." >&2
  exit 1
fi

# #443 (#429 section 2's exit criterion): an engine offset read by Alloy lives in EngineLayout and nowhere
# else, so that the unresolved counter is the truth. The declaration form an offset takes here is a
# hex-valued integer const -- anything matching that has to say on its own line why it is NOT a place in
# memory (a GL enum, a shader-param index, a bit mask, a sentinel value).
if grep -rnE '(const|readonly)[[:space:]]+(int|uint|long|ulong|byte|ushort|short|nint)\b[^;]*0x' \
     --include='*.cs' managed/Alloy/ | grep -v 'not-an-offset:'; then
  echo "!! bare hex constant under managed/Alloy/ (see the lines above; #443). An engine offset belongs in" >&2
  echo "!! managed/Framework/EngineLayout.cs with a class and a form; anything else says why on the same" >&2
  echo "!! line as a 'not-an-offset: <why>' comment." >&2
  exit 1
fi

# #447 (#429 section 3): a symbol's Windows resolution is DECLARED, never absent. A missing _win key used to
# mean both "never looked" and "deliberately Linux-only"; now every `public const string` in SymbolMap.cs
# carries a row -- Nm(), At(), LinuxOnly("why") or Hole("why") -- and only Hole counts against the port.
# The census must see EVERY declarator, not the first per line: `public const string A = "a", B = "b";`
# and its continuation lines hid five constants from the first draft of this gate (and from the sweep that
# filled the table), which the Proton log then reported as unresolved. Read declarators, not lines.
missing=$(comm -23 \
  <(tr '\n' ' ' < managed/Framework/SymbolMap.cs | grep -oP 'public const string\s+\K[^;]*' \
      | grep -oP '(\w+)(?=\s*=\s*")' | sort -u) \
  <(sed -n '/Dictionary<string, Win> _win/,/^    };/p' managed/Framework/SymbolMap.cs \
      | grep -oP '^\s*\[\K\w+' | sort -u))
if [ -n "$missing" ]; then
  echo "!! symbol constant with no Windows row (#447):" >&2
  echo "$missing" | sed 's/^/!!   /' >&2
  echo "!! every symbol in SymbolMap.cs resolves to Nm(), At(), LinuxOnly(\"why\") or Hole(\"why\")." >&2
  exit 1
fi

echo ">> building managed lib (C# NativeAOT -> sowx_managed.so)..."
SOWX_VERSION=$(git describe --tags --always 2>/dev/null || echo dev)
# Quiet on success, but NEVER swallow a failure: discarding stderr here once hid a compile error behind a
# stale staged .so, and run-demo.sh happily booted the old build.
if ! dotnet publish managed/sowx_managed.csproj -c Release -o build \
     -p:InformationalVersion="$SOWX_VERSION" > build-managed.log 2>&1; then
  echo ">> managed build FAILED:"; grep -E "error|rror" build-managed.log | head -20; exit 1
fi

echo ">> building native bootstrap (libsowx.so)..."
cc -shared -fPIC -O2 -o build/libsowx.so native/bootstrap.c -ldl

echo ">> staging runtime dir (sow-client-extender/)..."
# One ready-to-run/ship folder: the wrapper loads the .so files beside it.
# cp -L on libfunchook resolves the SOVERSION symlink to a real file named
# libfunchook.so -- DllImport("funchook") needs that exact name.
OUT=sow-client-extender
rm -rf "$OUT" && mkdir -p "$OUT"
cp -a build/libsowx.so build/sowx_managed.so "$OUT/"
cp -aL build/libfunchook.so "$OUT/libfunchook.so"
cp packaging/sowx-launch "$OUT/sowx-launch"
cp packaging/README.txt "$OUT/README.txt"
chmod +x "$OUT/sowx-launch"

echo ">> artifacts:"
ls -1 "$OUT"
