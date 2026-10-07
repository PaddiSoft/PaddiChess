#!/usr/bin/env python3
"""Refresh the checked-in release notices from restored NuGet packages.

Run after dotnet restore. This is an offline maintainer task, never a build
requirement. Packages omitting license text use reviewed, pinned upstream copies.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import shutil
import tempfile
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = ROOT / "PaddiChess/Packaging/ThirdParty"
FALLBACKS = {
    "Avalonia": ["Avalonia-LICENSE.md", "Avalonia-NOTICE.md"],
    "Avalonia.BuildServices": ["Avalonia.BuildServices-LICENSE.txt"],
    "MicroCom.Runtime": ["MicroCom-LICENSE.txt"],
    "Microsoft.IO.RecyclableMemoryStream": ["RecyclableMemoryStream-LICENSE.txt"],
    "Tmds.DBus.Protocol": ["Tmds.DBus-COPYING.txt"],
}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=pathlib.Path,
                        default=ROOT / "PaddiChess/obj/project.assets.json")
    args = parser.parse_args()
    assets = json.loads(args.assets.read_text(encoding="utf-8"))
    upstream = {item["file"]: item for item in
                json.loads((DEST / "Upstream/sources.json").read_text(encoding="utf-8"))}
    for filename, item in upstream.items():
        if hashlib.sha256((DEST / "Upstream" / filename).read_bytes()).hexdigest() != item["sha256"]:
            raise ValueError(f"Pinned upstream notice changed: {filename}")
    folders = [pathlib.Path(p) for p in assets["packageFolders"]]
    # Self-contained WinML is consumed via its C API using PackageDownload. NuGet
    # excludes those packages from libraries, so include their original notices too.
    external = ET.parse(ROOT / "PaddiChess/PaddiChess.csproj")
    for item in external.findall(".//PackageDownload"):
        name, version = item.attrib["Include"], item.attrib["Version"].strip("[]")
        relative = f"{name.lower()}/{version}"
        package = next((p / relative for p in folders if (p / relative).is_dir()), None)
        if package is None:
            raise ValueError(f"Restore the Windows RID before collecting notices for {name}/{version}")
        assets["libraries"][f"{name}/{version}"] = {
            "type": "package", "path": relative,
            "files": [p.relative_to(package).as_posix() for p in package.rglob("*") if p.is_file()]}
    records = []
    with tempfile.TemporaryDirectory(prefix="paddi-licenses-") as temporary:
        output = pathlib.Path(temporary)
        for identity, entry in sorted(assets["libraries"].items()):
            if entry.get("type") != "package":
                continue
            name, version = identity.split("/", 1)
            # The diagnostics package is explicitly disabled in Release.
            if name == "AvaloniaUI.DiagnosticsSupport":
                continue
            package = next((p / entry["path"] for p in folders
                            if (p / entry["path"]).is_dir()), None)
            if package is None:
                raise ValueError(f"Restore package {identity} before collecting notices")
            metadata = next(e for e in ET.parse(next(package.glob("*.nuspec"))).getroot()
                            if e.tag.rsplit("}", 1)[-1] == "metadata")
            values = {e.tag.rsplit("}", 1)[-1]: e for e in metadata}
            repository = values.get("repository")
            license_element = values.get("license")
            declaration = license_element.text if license_element is not None else ""
            license_path = (declaration if license_element is not None and
                            license_element.get("type") == "file" else None)
            notices = []
            for filename in entry.get("files", []):
                basename = pathlib.PurePosixPath(filename).name.lower()
                if (filename == license_path or basename.startswith(("license", "licence")) or
                        "third-party-notice" in basename or "thirdpartynotice" in basename):
                    source = package / filename
                    if source.is_file():
                        notices.append((source, source.name, f"NuGet:{identity}/{filename}"))
            family = name if name in FALLBACKS else (
                "Avalonia" if name.startswith("Avalonia.") else None)
            if not notices and family is not None:
                for filename in FALLBACKS[family]:
                    commit = repository.get("commit") if repository is not None else None
                    if not commit or f"/{commit}/" not in upstream[filename]["url"]:
                        raise ValueError(f"Review and update the pinned upstream notice for {identity}")
                    notices.append((DEST / "Upstream" / filename, filename,
                                    f"Upstream/{filename}"))
            if not notices:
                raise ValueError(f"No reviewed license text available for {identity}")
            destination = output / f"{name}-{version}"
            destination.mkdir()
            copied = []
            for source, filename, origin in notices:
                contents = source.read_bytes()
                target = destination / filename
                if target.exists() and target.read_bytes() != contents:
                    raise ValueError(f"Ambiguous notice filenames in {identity}")
                target.write_bytes(contents)
                copied.append({"file": f"NuGet/{destination.name}/{filename}",
                               "source": origin, "sha256": hashlib.sha256(contents).hexdigest()})
            records.append({"package": name, "version": version,
                            "declaration": declaration,
                            "repository": dict(repository.attrib) if repository is not None else {},
                            "notices": copied})
        # Do not leave a partially regenerated set when validation fails.
        target = DEST / "NuGet"
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(output, target)
    (DEST / "dependencies.json").write_text(json.dumps(records, indent=2, ensure_ascii=False) + "\n",
                                            encoding="utf-8")
    rows = ["# Third-party dependency notices", "",
            "These are the original notices for the pinned NuGet dependencies. The set includes",
            "all supported desktop platforms and build tools; a particular release may use only",
            "a subset. Debug-only Avalonia diagnostics are excluded from Release.", "",
            "Inter font: [SIL Open Font License](Upstream/Inter-LICENSE.txt).",
            "The package wrapper license does not replace the font license.", "",
            "Pinned upstream sources and hashes: [sources.json](Upstream/sources.json).",
            "Package versions, repository commits and notice hashes: [dependencies.json](dependencies.json).", "",
            "| Package | Version | License declaration | Notices |", "|---|---|---|---|"]
    for record in records:
        links = ", ".join(f"[{pathlib.PurePosixPath(n['file']).name}]({n['file']})"
                          for n in record["notices"])
        rows.append(f"| {record['package']} | {record['version']} | {record['declaration']} | {links} |")
    rows.extend(["", "## Other bundled components", "",
                 "Pikafish engine: see `Engine/Copying.txt`, `Engine/AUTHORS`, `Engine/README.md`.",
                 "The engine NNUE weights have their own `Engine/NNUE-License.md`.",
                 "The standalone rules helper's corresponding source is `Native/PikafishRules-source.zip`.",
                 "OCR model: see `Assets/Ocr/SOURCE.md` and its Apache license.",
                 "The macOS compatibility code notice is `Native/Licenses/yabai-MIT.txt`.",
                 "The .NET runtime, if bundled, retains its own `LICENSE.txt` and `ThirdPartyNotices.txt`.",
                 "Paddi source license is the release root `LICENSE`.", "",
                 "This dependency inventory does not grant new redistribution rights to game skins,",
                 "sound assets, engine weights or downloaded engines. Those require their own provenance.", "",
                 "Maintainers: after updating packages, run `dotnet restore`, then",
                 "`python3 scripts/collect-dependency-licenses.py`; review and commit the changed notices.", ""])
    (DEST / "README.md").write_text("\n".join(rows), encoding="utf-8")
    print(f"Collected original notices for {len(records)} package versions.")


if __name__ == "__main__":
    main()
