"""Create a disposable Unity project and run the package's regression suite."""
import argparse
import json
import shutil
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--unity", required=True, type=Path, help="Path to an installed Unity 6 Editor executable")
parser.add_argument("--project", required=True, type=Path, help="New, disposable project directory")
parser.add_argument("--package", type=Path, default=Path(__file__).resolve().parents[1], help="Package folder to install (defaults to this checkout)")
parser.add_argument("--unity-version", default="6000.0.48f1", help="Exact version of the supplied Editor (default: tested development version)")
args = parser.parse_args()
unity = args.unity.resolve()
project = args.project.resolve()
package = args.package.resolve()
if not unity.is_file() or not (package / "package.json").is_file():
    parser.error("Unity executable or package.json was not found")
if project.exists() and any(project.iterdir()):
    parser.error("Use a new or empty project folder; existing projects are never overwritten")

(project / "Assets/Editor").mkdir(parents=True, exist_ok=True)
(project / "ProjectSettings").mkdir()
(project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: " + args.unity_version + "\n", encoding="utf-8")
(project / "Packages").mkdir()
manifest = json.loads((package / "package.json").read_text(encoding="utf-8-sig"))
dependencies = {manifest["name"]: "file:" + package.as_posix()}
for module in ("physics", "imgui", "imageconversion", "jsonserialize"):
    dependencies["com.unity.modules." + module] = "1.0.0"
(project / "Packages/manifest.json").write_text(json.dumps({"dependencies": dependencies}, indent=2), encoding="utf-8")
shutil.copy2(package / "Tests~/BridgeRegressionRunner.cs", project / "Assets/Editor/BridgeRegressionRunner.cs")
log = project / "regression.log"
result = subprocess.run([str(unity), "-batchmode", "-nographics", "-projectPath", str(project), "-executeMethod", "BridgeRegressionRunner.Run", "-logFile", str(log)], cwd=project)
report = project / "regression-results.txt"
if report.exists():
    print(report.read_text(encoding="utf-8-sig"))
else:
    print("No regression report was produced. Inspect " + str(log))
raise SystemExit(result.returncode if result.returncode else (0 if report.exists() else 1))
