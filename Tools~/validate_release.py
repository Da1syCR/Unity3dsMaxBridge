"""Portable source/package checks. Python 3 standard library only."""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
errors = []


def check(condition, message):
    if not condition:
        errors.append(message)


manifest = json.loads((ROOT / "package.json").read_text(encoding="utf-8-sig"))
check(manifest["name"] == "com.da1sycr.unity3dsmaxbridge", "Wrong package name")
check(manifest.get("license") == "MIT", "License metadata is missing")
check(manifest.get("dependencies", {}).get("com.unity.formats.fbx") == "5.1.3", "Unexpected FBX dependency")
for name in ("LICENSE", "README.md", "README.ru.md", "CONTRIBUTING.md", "CHANGELOG.md"):
    check((ROOT / name).is_file(), "Missing " + name)
for name in ("README.md", "README.ru.md"):
    text = (ROOT / name).read_text(encoding="utf-8-sig")
    check("#v" + manifest["version"] in text, "Install URL version mismatch: " + name)

assembly = json.loads((ROOT / "Editor/Unity3dsMaxBridge.Editor.asmdef").read_text(encoding="utf-8-sig"))
check(assembly["name"] == "Unity3dsMaxBridge.Editor", "Wrong assembly name")
template = (ROOT / "Editor/MaxBridgeConnection.txt").read_text(encoding="utf-8-sig")
check(template.count("{{BRIDGE_DIRECTORY}}") == 1, "Connector path placeholder is missing or duplicated")
check("macroScript Unity3dsMaxBridge" in template, "Saved Max toolbar action is missing")
check(not re.search(r"[\u0400-\u04ff]", template), "Max must remain English-only")

guids = {}
for path in ROOT.rglob("*"):
    relative = path.relative_to(ROOT)
    if not path.is_file() or ".git" in relative.parts:
        continue
    if path.suffix == ".meta":
        found = re.search(r"^guid: ([0-9a-f]{32})$", path.read_text(encoding="utf-8-sig"), re.M)
        check(found is not None, "Invalid meta GUID: " + str(relative))
        if found:
            guid = found.group(1)
            check(guid not in guids, "Duplicate meta GUID: " + str(relative))
            guids[guid] = relative
    if path.suffix not in (".cs", ".txt", ".md", ".json", ".asmdef"):
        continue
    text = path.read_text(encoding="utf-8-sig")
    check(not re.search(r"\b[A-Za-z]:[\\/]", text), "Machine-specific drive path: " + str(relative))
    check(not re.search(r"/(?:Users|home)/[A-Za-z]", text), "Machine-specific user path: " + str(relative))
    check(not re.search(r"(?:gh[pousr]_[A-Za-z0-9]{25,}|github_pat_[A-Za-z0-9_]{25,})", text), "Possible token: " + str(relative))

for path in (ROOT / "Editor").glob("*"):
    if path.is_file() and path.suffix != ".meta":
        check(Path(str(path) + ".meta").is_file(), "Missing Unity meta: " + path.name)

if errors:
    raise SystemExit("\n".join("FAIL " + error for error in errors))
print("PASS package metadata, install URLs, English Max, portable paths, token scan and Unity GUIDs")
