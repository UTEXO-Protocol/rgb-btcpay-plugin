#!/usr/bin/env python3
"""Assert that a native library's object header declares the architecture its RID claims."""

from __future__ import annotations

import argparse
from pathlib import Path, PurePosixPath
import re
import struct
import sys
import zipfile


EXPECTED_MACHINE_BY_RID = {
    "linux-x64": "ELF-64 x86-64",
    "linux-arm64": "ELF-64 AArch64",
    "osx-arm64": "Mach-O-64 arm64",
    "osx-x64": "Mach-O-64 x86-64",
    "win-x64": "PE-64 x86-64",
}

HEADER_BYTES_NEEDED = 4096

ELF_MAGIC = b"\x7fELF"
ELF_CLASS_NAMES = {1: "32", 2: "64"}
ELF_BYTE_ORDERS = {1: "<", 2: ">"}
ELF_MACHINE_NAMES = {62: "x86-64", 183: "AArch64"}

MACHO_MAGIC_LAYOUTS = {
    b"\xcf\xfa\xed\xfe": ("<", "64"),
    b"\xce\xfa\xed\xfe": ("<", "32"),
    b"\xfe\xed\xfa\xcf": (">", "64"),
    b"\xfe\xed\xfa\xce": (">", "32"),
}
MACHO_UNIVERSAL_MAGICS = {
    b"\xca\xfe\xba\xbe",
    b"\xbe\xba\xfe\xca",
    b"\xca\xfe\xba\xbf",
    b"\xbf\xba\xfe\xca",
}
MACHO_CPU_NAMES = {0x0100000C: "arm64", 0x01000007: "x86-64"}

PE_DOS_MAGIC = b"MZ"
PE_SIGNATURE = b"PE\x00\x00"
PE_LFANEW_OFFSET = 0x3C
PE_COFF_MACHINE_NAMES = {0x8664: "x86-64", 0xAA64: "arm64", 0x014C: "x86"}
PE_OPTIONAL_MAGIC_CLASSES = {0x010B: "32", 0x020B: "64"}

RUNTIMES_ENTRY = re.compile(r"^runtimes/([^/]+)/native/.+$")
STAGING_ENTRY = re.compile(r"^([^/]+)/native/.+$")

NATIVE_LIBRARY_SUFFIXES = (".so", ".dylib", ".dll")

REPAIR_BY_RID = {
    "linux-x64": "bash scripts/pack-rgbverify.sh --stage",
}
REPAIR_OTHERWISE = (
    "rebuild it on a host of that architecture with native/rgb-verify/build-native.sh, or let"
    " .github/workflows/pack-native.yml rebuild the RIDs it has jobs for"
)


class ArchitectureError(Exception):
    pass


def names_a_native_library(entry: str) -> bool:
    basename = PurePosixPath(entry).name
    return not basename.startswith(".") and basename.endswith(NATIVE_LIBRARY_SUFFIXES)


def expected_machine(rid: str) -> str:
    try:
        return EXPECTED_MACHINE_BY_RID[rid]
    except KeyError:
        raise ArchitectureError(
            f"RID {rid} has no expected machine type recorded, so its architecture cannot be judged and"
            " is rejected rather than assumed correct. Known RIDs: "
            + ", ".join(sorted(EXPECTED_MACHINE_BY_RID))
            + ". Add the RID to EXPECTED_MACHINE_BY_RID in scripts/native_architecture.py if it is"
            " genuinely shipped."
        ) from None


def describe_first_bytes(data: bytes) -> str:
    return data[:8].hex(" ") if data else "none, the file is empty"


def observed_elf_machine(data: bytes) -> str:
    if len(data) < 20:
        return f"truncated ELF header, only {len(data)} bytes"
    byte_order = ELF_BYTE_ORDERS.get(data[5])
    if byte_order is None:
        return f"ELF with unrecognized data encoding {data[5]}"
    elf_class = ELF_CLASS_NAMES.get(data[4], f"class{data[4]}")
    machine = struct.unpack_from(byte_order + "H", data, 18)[0]
    return f"ELF-{elf_class} {ELF_MACHINE_NAMES.get(machine, f'machine 0x{machine:04x}')}"


def observed_macho_machine(data: bytes) -> str:
    byte_order, macho_class = MACHO_MAGIC_LAYOUTS[data[:4]]
    if len(data) < 8:
        return f"truncated Mach-O header, only {len(data)} bytes"
    cpu = struct.unpack_from(byte_order + "I", data, 4)[0]
    return f"Mach-O-{macho_class} {MACHO_CPU_NAMES.get(cpu, f'cputype 0x{cpu:08x}')}"


def observed_pe_machine(data: bytes) -> str:
    if len(data) < PE_LFANEW_OFFSET + 4:
        return f"truncated PE DOS header, only {len(data)} bytes"
    lfanew = struct.unpack_from("<I", data, PE_LFANEW_OFFSET)[0]
    if lfanew + 26 > len(data):
        return f"PE header offset 0x{lfanew:x} lies outside the {len(data)} bytes read"
    if data[lfanew : lfanew + 4] != PE_SIGNATURE:
        return f"MZ image whose PE signature is absent at offset 0x{lfanew:x}"
    machine = struct.unpack_from("<H", data, lfanew + 4)[0]
    optional_magic = struct.unpack_from("<H", data, lfanew + 24)[0]
    pe_class = PE_OPTIONAL_MAGIC_CLASSES.get(optional_magic, f"optional-magic-0x{optional_magic:04x}")
    return f"PE-{pe_class} {PE_COFF_MACHINE_NAMES.get(machine, f'machine 0x{machine:04x}')}"


def observed_machine(data: bytes) -> str:
    head = data[:4]
    if head == ELF_MAGIC:
        return observed_elf_machine(data)
    if head in MACHO_MAGIC_LAYOUTS:
        return observed_macho_machine(data)
    if head in MACHO_UNIVERSAL_MAGICS:
        return "Mach-O universal archive, which is not a single-architecture object"
    if data[:2] == PE_DOS_MAGIC:
        return observed_pe_machine(data)
    return f"not a recognizable native object, first bytes {describe_first_bytes(data)}"


def assert_bytes(rid: str, label: str, data: bytes) -> str:
    expected = expected_machine(rid)
    observed = observed_machine(data)
    if observed != expected:
        raise ArchitectureError(
            f"{label} claims RID {rid}, which requires {expected}, but its object header declares"
            f" {observed}. A package that names a RID it cannot load on is worse than one that omits it."
            f" Repair: {REPAIR_BY_RID.get(rid, REPAIR_OTHERWISE)}"
        )
    return observed


def read_header(path: Path) -> bytes:
    with path.open("rb") as handle:
        return handle.read(HEADER_BYTES_NEEDED)


def assert_paths(pairs: list[tuple[str, Path]]) -> list[str]:
    results = []
    for rid, path in pairs:
        if not path.is_file():
            raise ArchitectureError(f"no file to inspect for RID {rid}: {path}")
        results.append(f"{path}: {assert_bytes(rid, str(path), read_header(path))}")
    return results


def zip_native_entries(archive: zipfile.ZipFile) -> list[tuple[str, str, bytes]]:
    found = []
    for info in archive.infolist():
        if info.is_dir():
            continue
        name = PurePosixPath(info.filename.replace("\\", "/")).as_posix().removeprefix("./")
        match = RUNTIMES_ENTRY.match(name)
        if match and names_a_native_library(name):
            with archive.open(info) as member:
                found.append((match.group(1), name, member.read(HEADER_BYTES_NEEDED)))
    return found


def directory_native_entries(root: Path, pattern: re.Pattern[str]) -> list[tuple[str, str, bytes]]:
    found = []
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        match = pattern.match(relative)
        if match and names_a_native_library(relative):
            found.append((match.group(1), str(path), read_header(path)))
    return found


def assert_entries(entries: list[tuple[str, str, bytes]], empty_message: str) -> list[str]:
    if not entries:
        raise ArchitectureError(empty_message)
    return [f"{label}: {assert_bytes(rid, label, data)}" for rid, label, data in entries]


def assert_package(path: Path) -> list[str]:
    if path.is_dir():
        entries = directory_native_entries(path, RUNTIMES_ENTRY)
    else:
        if not path.is_file():
            raise ArchitectureError(f"package to inspect does not exist: {path}")
        try:
            with zipfile.ZipFile(path) as archive:
                entries = zip_native_entries(archive)
        except (OSError, zipfile.BadZipFile) as fault:
            raise ArchitectureError(f"cannot read package {path}: {fault}") from None
    return assert_entries(
        entries,
        f"{path} contains no runtimes/<rid>/native/ native library, so no architecture was verified. A check that"
        " passes on a package with no natives in it is not a check.",
    )


def assert_staging_tree(path: Path) -> list[str]:
    if not path.is_dir():
        raise ArchitectureError(
            f"staging tree to inspect is not a directory: {path}. Stage the natives before packing."
        )
    return assert_entries(
        directory_native_entries(path, STAGING_ENTRY),
        f"{path} contains no <rid>/native/ native library, so no architecture was verified. A check that passes on"
        " an empty staging tree is not a check.",
    )


def synthetic_native(rid: str) -> bytes:
    expected = expected_machine(rid)
    if expected.startswith("ELF-64 "):
        machine = next(code for code, name in ELF_MACHINE_NAMES.items() if expected.endswith(name))
        header = ELF_MAGIC + bytes([2, 1, 1, 0]) + bytes(8)
        header += struct.pack("<HHI", 3, machine, 1)
        return header.ljust(64, b"\x00")
    if expected.startswith("Mach-O-64 "):
        cpu = next(code for code, name in MACHO_CPU_NAMES.items() if expected.endswith(name))
        return struct.pack("<4sIIIIII", b"\xcf\xfa\xed\xfe", cpu, 0, 6, 0, 0, 0).ljust(32, b"\x00")
    if expected.startswith("PE-64 "):
        machine = next(code for code, name in PE_COFF_MACHINE_NAMES.items() if expected.endswith(name))
        lfanew = 64
        header = PE_DOS_MAGIC.ljust(PE_LFANEW_OFFSET, b"\x00") + struct.pack("<I", lfanew)
        header = header.ljust(lfanew, b"\x00") + PE_SIGNATURE
        header += struct.pack("<HHIIIHH", machine, 0, 0, 0, 0, 0, 0)
        header += struct.pack("<H", 0x020B)
        return header.ljust(128, b"\x00")
    raise ArchitectureError(f"no synthetic header shape is defined for {expected}")


def parse_pair(value: str) -> tuple[str, Path]:
    rid, separator, raw = value.partition("=")
    if not separator or not rid or not raw:
        raise ArchitectureError(f"--assert must be RID=PATH; got: {value}")
    return rid, Path(raw).expanduser()


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assert", dest="assertions", action="append", metavar="RID=PATH")
    parser.add_argument("--package", type=Path, metavar="PATH")
    parser.add_argument("--staging-tree", type=Path, metavar="PATH")
    parser.add_argument("--synthesize", nargs=2, metavar=("RID", "PATH"))
    return parser.parse_args(argv)


def run(args: argparse.Namespace) -> list[str]:
    modes = [args.assertions, args.package, args.staging_tree, args.synthesize]
    if sum(1 for mode in modes if mode) != 1:
        raise ArchitectureError(
            "exactly one of --assert, --package, --staging-tree or --synthesize is required"
        )
    if args.assertions:
        return assert_paths([parse_pair(value) for value in args.assertions])
    if args.package:
        return assert_package(args.package)
    if args.staging_tree:
        return assert_staging_tree(args.staging_tree)
    rid, destination = args.synthesize[0], Path(args.synthesize[1])
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(synthetic_native(rid))
    return [f"wrote a synthetic {expected_machine(rid)} header for {rid} to {destination}"]


def main(argv: list[str] | None = None) -> int:
    try:
        for line in run(parse_args(argv if argv is not None else sys.argv[1:])):
            print(line)
        return 0
    except (ArchitectureError, OSError) as fault:
        print(f"native architecture check failed: {fault}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
