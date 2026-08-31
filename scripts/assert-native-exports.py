#!/usr/bin/env python3
"""Assert a gate native exports exactly the symbols RgbNativeSelfCheck requires."""

from __future__ import annotations

import argparse
import ctypes
from pathlib import Path, PurePosixPath
import sys


REQUIRED_EXPORTS = (
    "rgbverify_decode_invoice",
    "rgbverify_validate",
    "rgbverify_commitment_check",
    "rgbverify_validate_v2",
    "rgbverify_string_free",
)

SPELLED_COUNT = "five"
SPELLED_COUNTS = {5: "five"}

SYMBOL_PREFIX_BY_FORMAT = {"elf": "", "macho": "_"}

AUTHORITY = "Services/RgbNativeSelfCheck.cs RequiredExports()"

INEXACTNESS_NOTE = (
    "Matching is exact: rgbverify_validate is NOT satisfied by rgbverify_validate_v2, which is what a"
    " substring match accepted."
)


class ExportError(Exception):
    pass


def spelled_count() -> str:
    expected = SPELLED_COUNTS.get(len(REQUIRED_EXPORTS))
    if expected != SPELLED_COUNT:
        raise ExportError(
            f"the messages in this script say {SPELLED_COUNT!r} exports but REQUIRED_EXPORTS holds"
            f" {len(REQUIRED_EXPORTS)}. A count written into a message must match the list it counts."
            f" Update both, and keep the list equal to {AUTHORITY}."
        )
    return SPELLED_COUNT


def probe_by_loading(path: Path) -> str:
    count = spelled_count()
    if not path.is_file():
        raise ExportError(f"no file to probe for exports: {path}")
    try:
        library = ctypes.CDLL(str(path))
    except OSError as fault:
        raise ExportError(
            f"{path} could not be loaded at all, so its exports were never inspected: {fault}."
            " A wrong architecture, a missing shared dependency, or a glibc floor above the"
            " deployment target all present this way. This is NOT a report of a missing export."
        ) from None
    absent = [name for name in REQUIRED_EXPORTS if not hasattr(library, name)]
    if absent:
        raise ExportError(
            f"{path} loaded, but these required exports are absent: {', '.join(absent)}."
            f" All {len(REQUIRED_EXPORTS)} symbols in {AUTHORITY} must resolve. {INEXACTNESS_NOTE}"
        )
    return f"loaded {path} with all {count} exports"


def probe_by_symbol_table(label: str, text: str, object_format: str) -> str:
    count = spelled_count()
    prefix = SYMBOL_PREFIX_BY_FORMAT[object_format]
    observed = {fields[-1] for fields in (line.split() for line in text.splitlines()) if fields}
    if not observed:
        raise ExportError(
            f"the symbol table read from {label} contains no symbols at all, so nothing was verified."
            " A check that passes on an empty symbol table is not a check."
        )
    absent = [name for name in REQUIRED_EXPORTS if prefix + name not in observed]
    if absent:
        raise ExportError(
            f"{label} declares a {object_format} symbol table missing these required exports,"
            f" each looked up exactly as '{prefix}<name>': {', '.join(absent)}."
            f" {len(observed)} symbols were observed. {INEXACTNESS_NOTE}"
        )
    return (
        f"the {object_format} symbol table from {label} declares all {count} exports,"
        f" spelled '{prefix}<name>'"
    )


def read_symbol_table(source: str) -> tuple[str, str]:
    if source == "-":
        return "standard input", sys.stdin.read()
    path = Path(source).expanduser()
    if not path.is_file():
        raise ExportError(f"no symbol table to read: {path}")
    return str(path), path.read_text(encoding="utf-8", errors="replace")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--load", type=Path, metavar="PATH")
    parser.add_argument("--symbol-table", metavar="PATH_OR_DASH")
    parser.add_argument("--format", dest="object_format", choices=sorted(SYMBOL_PREFIX_BY_FORMAT))
    return parser.parse_args(argv)


def run(args: argparse.Namespace) -> str:
    if bool(args.load) == bool(args.symbol_table):
        raise ExportError("exactly one of --load or --symbol-table is required")
    if args.load:
        if args.object_format:
            raise ExportError(
                "--format applies to --symbol-table only; a load probe resolves symbols through"
                " dlsym, which needs no spelling rule"
            )
        return probe_by_loading(args.load)
    if not args.object_format:
        raise ExportError(
            "--symbol-table requires --format elf or --format macho, because the expected spelling"
            " differs: ELF declares 'name' and Mach-O declares '_name'. Guessing it would reintroduce"
            " the inexact match this check exists to close."
        )
    label, text = read_symbol_table(args.symbol_table)
    return probe_by_symbol_table(label, text, args.object_format)


def main(argv: list[str] | None = None) -> int:
    try:
        print(run(parse_args(argv if argv is not None else sys.argv[1:])))
        return 0
    except (ExportError, OSError) as fault:
        print(f"gate native export check failed: {fault}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
