#!/usr/bin/env bash
#
# Headless compile check for the gameplay scripts in Assets/objects/.
#
# Unity's generated Assembly-CSharp.csproj carries a "do not modify" banner and is
# rewritten by the editor, and it will not list a newly added .cs file until Unity next
# refreshes. So we never touch it: we copy it to a scratch location under Temp/ (which
# Unity's .gitignore already excludes), rewrite its <Compile Include> list to the current
# contents of Assets/objects/ using absolute paths, and build that copy.
#
# Absolute paths matter. The generated csproj mixes absolute HintPaths (the Unity editor's
# own DLLs) with paths relative to the project root (everything under Library/, including
# Unity.InputSystem.dll). Both those and the <Compile Include> sources are absolutized here,
# otherwise the scratch csproj silently loses the package assemblies and reports errors that
# have nothing to do with the code being checked.
#
# This does NOT require the Unity Editor to be closed - it never takes the project lock.
# It is a compile check only. It proves the code builds; it proves nothing about behaviour.
#
# Usage:  ./tools/build-check.sh
# Passes when the output ends with "Build succeeded." and "0 Error(s)".

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC_CSPROJ="$ROOT/Assembly-CSharp.csproj"
SCRATCH="$ROOT/Temp/build-check"
OUT_CSPROJ="$SCRATCH/BuildCheck.csproj"

if [ ! -f "$SRC_CSPROJ" ]; then
    echo "ERROR: $SRC_CSPROJ not found."
    echo "Open the project in Unity once so it generates the csproj, then re-run."
    exit 1
fi

mkdir -p "$SCRATCH"

python - "$SRC_CSPROJ" "$OUT_CSPROJ" "$ROOT" <<'PY'
import os
import re
import sys

src_csproj, out_csproj, root = sys.argv[1], sys.argv[2], sys.argv[3]

# utf-8-sig: the generated csproj starts with a BOM.
text = open(src_csproj, encoding='utf-8-sig').read()

sources = []
for dirpath, _dirnames, filenames in os.walk(os.path.join(root, 'Assets', 'objects')):
    for name in filenames:
        if name.endswith('.cs'):
            sources.append(os.path.abspath(os.path.join(dirpath, name)))
sources.sort()

if not sources:
    raise SystemExit('ERROR: no .cs files found under Assets/objects/')

# Drop every existing <Compile Include .../> line, then splice ours into the first ItemGroup.
text, removed = re.subn(r'[ \t]*<Compile Include="[^"]*"[ \t]*/>\r?\n', '', text)
if removed == 0:
    raise SystemExit('ERROR: no <Compile Include> entries in the generated csproj - '
                     'its format changed, update this script')

block = '\n'.join('    <Compile Include="%s" />' % s.replace('/', '\\') for s in sources)
text = text.replace('<ItemGroup>', '<ItemGroup>\n' + block, 1)

# Absolutize every relative HintPath (they are relative to the project root, and the scratch
# csproj does not live there). Anything already rooted - "C:\..." - is left alone.
win_root = os.path.abspath(root).replace('/', '\\').rstrip('\\')


def absolutize(match):
    path = match.group(1)
    if re.match(r'^[A-Za-z]:[\\/]', path) or path.startswith('\\\\'):
        return match.group(0)
    return '<HintPath>%s\\%s</HintPath>' % (win_root, path.lstrip('\\/'))


text, fixed = re.subn(r'<HintPath>([^<]*)</HintPath>', absolutize, text)

with open(out_csproj, 'w', encoding='utf-8') as handle:
    handle.write(text)

print('build-check: %d reference path(s) checked' % fixed)
print('build-check: compiling %d file(s)' % len(sources))
for s in sources:
    print('   ', os.path.relpath(s, root).replace('\\', '/'))
print()
PY

dotnet build "$OUT_CSPROJ" -v:q -nologo
