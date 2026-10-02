#!/usr/bin/env python3
"""Package already-built Framework WPF payloads without bundling a runtime."""

import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import xml.etree.ElementTree as ET


def pe_machine(path: Path) -> int:
    with path.open("rb") as stream:
        if stream.read(2) != b"MZ":
            raise ValueError(f"Not a Windows executable: {path}")
        stream.seek(0x3C)
        offset = struct.unpack("<I", stream.read(4))[0]
        stream.seek(offset)
        if stream.read(4) != b"PE\0\0":
            raise ValueError(f"Invalid Windows executable: {path}")
        return struct.unpack("<H", stream.read(2))[0]


def validate_runtime(source, expected):
    actual = {p.name: p for p in source.glob('*.dll') if p.name != 'AI.Desktop.Setup.Core.dll'}
    if set(actual) != set(expected):
        raise ValueError('Runtime DLL inventory differs from reviewed license mapping')
    result = []
    for name, path in sorted(actual.items()):
        entry = expected[name]
        if hashlib.sha256(path.read_bytes()).hexdigest() != entry['sha256']:
            raise ValueError('Runtime DLL bytes differ from reviewed package asset')
        result.append(dict(path=name, ownership='third_party', **entry))
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--makensis", default=os.environ.get("AI_SETUP_MAKENSIS", "makensis"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    app = root / "App"
    version = ET.parse(app / "AI.Desktop.Setup.csproj").findtext(".//Version")
    if not version or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        raise SystemExit("Set a three-part numeric Version in AI.Desktop.Setup.csproj.")
    output = args.output.resolve()
    compiler_version = subprocess.check_output([args.makensis, ("/VERSION" if os.name == "nt" else "-VERSION")], text=True).strip()
    # Older NSIS versions lack later Windows security fixes. Keep build tooling
    # explicit in the receipt; no opaque executable packer or custom CLR loader.
    match = re.match(r"v?(\d+)\.(\d+)", compiler_version)
    if compiler_version not in ("v3.12", "3.12"):
        raise SystemExit("Exactly NSIS 3.12 is required.")
    entries = {}
    expected_runtime = json.loads((root / "build/runtime-inventory.json").read_text())
    runtime_inventory = {}
    payload_sizes = {}
    for architecture, key, machine, sku in [
        ("x64", "setupWindowsX64", 0x8664, ".NETFramework,Version=v4.8"),
        ("arm64", "setupWindowsArm64", 0xAA64, ".NETFramework,Version=v4.8.1"),
    ]:
        source = output / ("publish-" + architecture)
        executable = source / "AI.Desktop.Setup.exe"
        config = source / "AI.Desktop.Setup.exe.config"
        if pe_machine(executable) != machine:
            raise SystemExit(f"Wrong architecture in {executable}")
        runtime = ET.parse(config).find("./startup/supportedRuntime")
        if runtime is None or runtime.get("sku") != sku:
            raise SystemExit(f"Missing or incorrect supportedRuntime in {config}")
        if (source / "System.Private.CoreLib.dll").exists() or (source / "coreclr.dll").exists():
            raise SystemExit("Refusing to package a bundled .NET runtime.")
        runtime_inventory[architecture] = validate_runtime(source, expected_runtime)
        payload = output / ("payload-" + architecture)
        payload.mkdir(exist_ok=False)
        for item in sorted(source.rglob("*")):
            if item.is_symlink():
                raise SystemExit(f"Refusing payload symlink: {item}")
            if not item.is_file() or item.suffix.lower() in {".pdb", ".xml"}:
                continue
            destination = payload / item.relative_to(source)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, destination)
        # Distribute owned and upstream dependency notices alongside the executable.
        shutil.copy2(root / "LICENSE", payload / "LICENSE.txt")
        shutil.copy2(root / "THIRD-PARTY-NOTICES.md", payload / "THIRD-PARTY-NOTICES.md")
        shutil.copytree(root / "licenses", payload / "licenses")
        assets = json.loads((root / "Core/obj/project.assets.json").read_text())
        for package, entry in assets["libraries"].items():
            if entry["type"] != "package" or package.startswith("Microsoft.NETFramework.ReferenceAssemblies"):
                continue
            for folder in assets["packageFolders"]:
                package_path = Path(folder) / entry["path"]
                for notice in sorted(package_path.glob("*")):
                    if notice.is_file() and notice.name.lower().startswith(("license", "notice", "third-party-notices")):
                        destination = payload / "licenses" / package.split("/")[0] / notice.name
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(notice, destination)
                if package_path.exists():
                    break
        for name, entry in expected_runtime.items():
            if entry['notice_required']:
                notice = payload / 'licenses' / entry['package'] / 'THIRD-PARTY-NOTICES.TXT'
                if not notice.is_file() or hashlib.sha256(notice.read_bytes()).hexdigest() != '6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21':
                    raise ValueError('Required package notices are missing or changed')
        filename = f"AI-Desktop-Setup-{version}-{architecture}.exe"
        target = output / filename
        if target.exists():
            raise SystemExit(f"Refusing to overwrite a versioned artifact: {target}")
        subprocess.run([
            args.makensis, ("/NOCONFIG" if os.name == "nt" else "-NOCONFIG"),
            ("/V2" if os.name == "nt" else "-V2"),
            *[("/D" if os.name == "nt" else "-D") + value for value in
              (f"PAYLOAD_DIR={payload}", f"OUTPUT_FILE={target}", f"VERSION={version}", f"ARCH={architecture}")],
            str(root / "packaging/launcher.nsi"),
        ], check=True)
        digest = hashlib.sha256(target.read_bytes()).hexdigest()
        entries[key] = dict(file=filename, sha256=digest,
                            version=version + "-preview", bytes=target.stat().st_size, signed=False)
        payload_sizes[architecture] = sum(p.stat().st_size for p in payload.rglob("*") if p.is_file())
    timestamp = datetime.datetime.now(datetime.timezone.utc).isoformat()
    (output / "manifest.json").write_text(json.dumps(dict(updated_at=timestamp, downloads=entries), indent=2) + "\n")
    (output / "SHA256SUMS").write_text("".join(v["sha256"] + "  " + v["file"] + "\n" for v in entries.values()))
    (output / "packaging.json").write_text(json.dumps(dict(
        version=version, generated_at=timestamp, nsis=compiler_version,
        payload_bytes=payload_sizes, runtime_bundled=False, signed=False, preview=True,
        runtime_inventory=runtime_inventory,
    ), indent=2) + "\n")
    print("Unsigned preview artifacts: " + str(output))
    for key, value in entries.items():
        print(f"{key}: {value['bytes']:,} bytes")
    print("These files have NOT been published or verified on native Windows.")


if __name__ == "__main__":
    main()
