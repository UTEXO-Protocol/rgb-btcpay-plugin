from __future__ import annotations

import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import warnings
import zipfile


REPO_ROOT = Path(__file__).resolve().parents[2]
VERIFIER = REPO_ROOT / "scripts" / "verify_plugin_artifact.py"
CONTRACT_PATH = REPO_ROOT / "scripts" / "plugin-artifact-contract.json"
ARCHITECTURE_MODULE = REPO_ROOT / "scripts" / "native_architecture.py"

sys.path.insert(0, str(REPO_ROOT / "scripts"))
import native_architecture

CAPTURED_ELF_HEADER = bytes.fromhex(
    "7f454c4602010100000000000000000003003e00010000000000000000000000"
)
CAPTURED_MACHO_HEADER = bytes.fromhex(
    "cffaedfe0c000001000000000600000011000000e00800008500900000000000"
)
CAPTURED_PE_HEADER = bytes.fromhex(
    "4d5a90000300000004000000ffff0000b800000000000000400000000000000000000000000000000000000000000000"
    "000000000000000000000000100100000e1fba0e00b409cd21b8014ccd21546869732070726f6772616d2063616e6e6f"
    "742062652072756e20696e20444f53206d6f64652e0d0d0a24000000000000006261fa52260094012600940126009401"
    "2f78070134009401af8b950024009401af8b970022009401af8b90002e009401af8b9100370094015f8195002d009401"
    "be8d9700220094012600950163019401be8d90004a0094012600940122049401be8d940027009401be8d960027009401"
    "5269636826009401000000000000000000000000000000000000000000000000504500006486050094d0576a00000000"
    "00000000f00022200b020e3300dec301"
)


class ArtifactFixture:
    def __init__(self, root: Path, strict_gate: bool = False):
        self.root = root
        self.publish = root / "publish"
        self.cache = root / "packages"
        self.publish.mkdir()
        self.contract = json.loads(CONTRACT_PATH.read_text(encoding="utf-8"))
        self.gate_path = "runtimes/linux-x64/native/librgbverifycffi.so"
        self.core_path = "runtimes/linux-x64/native/librgblibcffi.so"

        plain_files = {
            "btcpay.plugin.json": b"{}",
            "BTCPayServer.Plugins.RgbUtexo.dll": b"plugin",
            "RgbRestoreHelper.dll": b"helper",
            "RgbRestoreHelper.runtimeconfig.json": b"{}",
            "SharpCompress.dll": b"sharp",
            self.gate_path: b"gate",
            self.core_path: b"core",
        }
        for relative, data in plain_files.items():
            self.write(relative, data)

        plugin_packages = {
            "RgbLib/0.3.0-test": {
                "runtime": {"lib/net8.0/RgbLib.dll": {}},
                "runtimeTargets": {
                    self.core_path: {"rid": "linux-x64", "assetType": "native"}
                },
            },
            "SharpCompress/0.50.4-test": {
                "runtime": {"lib/net10.0/SharpCompress.dll": {}}
            },
        }
        if strict_gate:
            plugin_packages["RgbVerifyCffi/1.2.3-test"] = {
                "runtimeTargets": {
                    self.gate_path: {"rid": "linux-x64", "assetType": "native"}
                }
            }
        self.write_json(
            "BTCPayServer.Plugins.RgbUtexo.deps.json",
            {"targets": {"net10.0": plugin_packages}},
        )
        self.write_json(
            "RgbRestoreHelper.deps.json",
            {
                "targets": {
                    "net10.0": {
                        "RgbLib/0.3.0-test": {
                            "runtime": {"lib/net8.0/RgbLib.dll": {}},
                            "runtimeTargets": {
                                self.core_path: {"rid": "linux-x64", "assetType": "native"}
                            },
                        }
                    }
                }
            },
        )
        self.cache_write("RgbLib", "0.3.0-test", self.core_path, b"core")
        if strict_gate:
            self.cache_write("RgbVerifyCffi", "1.2.3-test", self.gate_path, b"gate")

    def write(self, relative: str, data: bytes) -> None:
        path = self.publish / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def write_json(self, relative: str, value: object) -> None:
        self.write(relative, json.dumps(value).encode())

    def cache_write(self, package: str, version: str, relative: str, data: bytes) -> None:
        path = self.cache / package.lower() / version / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def archive(self, name: str = "plugin.btcpay", prefix: str = "") -> Path:
        destination = self.root / name
        with zipfile.ZipFile(destination, "w") as archive:
            for path in sorted(self.publish.rglob("*")):
                if path.is_file():
                    relative = path.relative_to(self.publish).as_posix()
                    archive.write(path, prefix + relative)
        return destination

    def contract_file(self, contract: dict | None = None) -> Path:
        path = self.root / "contract.json"
        path.write_text(json.dumps(contract or self.contract), encoding="utf-8")
        return path


class VerifyPluginArtifactTests(unittest.TestCase):
    def fixture(self, strict_gate: bool = False):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        return ArtifactFixture(Path(temporary.name), strict_gate)

    def run_verify(
        self,
        fixture: ArtifactFixture,
        artifact: Path | None = None,
        *,
        strict: bool = False,
        contract: Path | None = None,
        gate_package: bool = False,
        gate_native_source: list[str] | None = None,
    ) -> subprocess.CompletedProcess[str]:
        command = [sys.executable, str(VERIFIER), str(artifact or fixture.publish)]
        command += ["--contract", str(contract or CONTRACT_PATH)]
        command += ["--provenance", "strict" if strict else "pre-package"]
        if strict and not gate_package:
            command += ["--package-cache", str(fixture.cache)]
        if gate_package:
            command.append("--gate-package")
        for value in gate_native_source or []:
            command += ["--gate-native-source", value]
        return subprocess.run(command, text=True, capture_output=True, check=False)

    def assert_failed(self, result: subprocess.CompletedProcess[str], message: str) -> None:
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn(message, result.stderr)

    def test_accepts_complete_publish_tree_and_archive(self):
        fixture = self.fixture()
        for artifact in (fixture.publish, fixture.archive()):
            with self.subTest(artifact=artifact):
                result = self.run_verify(fixture, artifact)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("PRE-PACKAGE MODE", result.stdout)
                self.assertIn("supported plugin RIDs verified: linux-x64", result.stdout)

    def test_rejects_removal_of_each_helper_file(self):
        for helper_file in (
            "RgbRestoreHelper.dll",
            "RgbRestoreHelper.deps.json",
            "RgbRestoreHelper.runtimeconfig.json",
        ):
            with self.subTest(helper_file=helper_file):
                fixture = self.fixture()
                (fixture.publish / helper_file).unlink()
                self.assert_failed(self.run_verify(fixture), f"missing required artifact path: {helper_file}")

    def test_rejects_missing_sharpcompress(self):
        fixture = self.fixture()
        (fixture.publish / "SharpCompress.dll").unlink()
        self.assert_failed(self.run_verify(fixture), "missing required artifact path: SharpCompress.dll")

    def test_rejects_missing_gate_for_claimed_rid(self):
        fixture = self.fixture()
        (fixture.publish / fixture.gate_path).unlink()
        self.assert_failed(self.run_verify(fixture), f"missing required artifact path: {fixture.gate_path}")

    def test_rejects_missing_core_for_claimed_rid(self):
        fixture = self.fixture()
        (fixture.publish / fixture.core_path).unlink()
        self.assert_failed(self.run_verify(fixture), f"missing required artifact path: {fixture.core_path}")

    def test_rejects_linux_arm64_claim_with_gate_but_no_core(self):
        fixture = self.fixture()
        arm_gate = "runtimes/linux-arm64/native/librgbverifycffi.so"
        arm_core = "runtimes/linux-arm64/native/librgblibcffi.so"
        fixture.write(arm_gate, b"extra gate")
        contract = copy.deepcopy(fixture.contract)
        contract["plugin"]["supported_rids"]["linux-arm64"] = {
            "gate": arm_gate,
            "core": arm_core,
        }
        self.assert_failed(
            self.run_verify(fixture, contract=fixture.contract_file(contract)),
            f"missing required artifact path: {arm_core}",
        )

    def test_rejects_required_archive_file_under_extra_prefix(self):
        fixture = self.fixture()
        archive_path = fixture.root / "prefixed-gate.btcpay"
        with zipfile.ZipFile(archive_path, "w") as archive:
            for path in sorted(fixture.publish.rglob("*")):
                if not path.is_file():
                    continue
                relative = path.relative_to(fixture.publish).as_posix()
                stored = "publish-out/" + relative if relative == fixture.gate_path else relative
                archive.write(path, stored)
        self.assert_failed(
            self.run_verify(fixture, archive_path),
            f"missing required artifact path: {fixture.gate_path}",
        )

    def test_rejects_duplicate_required_archive_entry(self):
        fixture = self.fixture()
        archive_path = fixture.archive("duplicate.btcpay")
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(archive_path, "a") as archive:
                archive.writestr(fixture.gate_path, b"duplicate gate")
        self.assert_failed(
            self.run_verify(fixture, archive_path),
            f"required artifact path occurs 2 times; expected exactly one: {fixture.gate_path}",
        )

    def test_strict_mode_rejects_hand_staged_gate(self):
        fixture = self.fixture()
        self.assert_failed(
            self.run_verify(fixture, strict=True),
            f"{fixture.gate_path} is not declared as a native asset of RgbVerifyCffi",
        )

    def test_strict_mode_rejects_byte_mismatched_gate(self):
        fixture = self.fixture(strict_gate=True)
        fixture.cache_write("RgbVerifyCffi", "1.2.3-test", fixture.gate_path, b"different")
        self.assert_failed(
            self.run_verify(fixture, strict=True),
            f"{fixture.gate_path} is not byte-identical to the RgbVerifyCffi package-cache copy",
        )

    def test_extra_native_assets_do_not_create_support_claims(self):
        fixture = self.fixture()
        fixture.write("runtimes/linux-arm64/native/librgbverifycffi.so", b"extra gate")
        win_core = "runtimes/win-x64/native/rgblibcffi.dll"
        fixture.write(win_core, b"extra core")
        deps_path = fixture.publish / "BTCPayServer.Plugins.RgbUtexo.deps.json"
        deps = json.loads(deps_path.read_text(encoding="utf-8"))
        deps["targets"]["net10.0"]["RgbLib/0.3.0-test"]["runtimeTargets"][win_core] = {
            "rid": "win-x64",
            "assetType": "native",
        }
        fixture.write_json("BTCPayServer.Plugins.RgbUtexo.deps.json", deps)
        fixture.cache_write("RgbLib", "0.3.0-test", win_core, b"extra core")
        result = self.run_verify(fixture)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("supported plugin RIDs verified: linux-x64", result.stdout)
        self.assertNotIn("supported plugin RIDs verified: linux-x64, linux-arm64", result.stdout)
        self.assertNotIn("win-x64", result.stdout)

    def gate_source(self, fixture: ArtifactFixture, name: str, data: bytes) -> Path:
        path = fixture.root / name
        path.write_bytes(data)
        return path

    def test_gate_native_source_binds_matching_build_output(self):
        fixture = self.fixture()
        source = self.gate_source(fixture, "built-gate.so", b"gate")
        for artifact in (fixture.publish, fixture.archive()):
            with self.subTest(artifact=artifact):
                result = self.run_verify(
                    fixture, artifact, gate_native_source=[f"linux-x64={source}"]
                )
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn(
                    "gate native byte-bound to the build output for RIDs: linux-x64", result.stdout
                )

    def test_gate_native_source_rejects_byte_mismatched_build_output(self):
        fixture = self.fixture()
        source = self.gate_source(fixture, "built-gate.so", b"a different build")
        self.assert_failed(
            self.run_verify(fixture, gate_native_source=[f"linux-x64={source}"]),
            f"{fixture.gate_path} is not byte-identical to the build output it must come from",
        )

    def test_gate_native_source_rejects_value_without_rid_separator(self):
        fixture = self.fixture()
        self.assert_failed(
            self.run_verify(fixture, gate_native_source=["nonsense"]),
            "--gate-native-source must be RID=PATH; got: nonsense",
        )

    def test_gate_native_source_rejects_rid_absent_from_contract(self):
        fixture = self.fixture()
        source = self.gate_source(fixture, "built-gate.so", b"gate")
        self.assert_failed(
            self.run_verify(fixture, gate_native_source=[f"win-x64={source}"]),
            "--gate-native-source names RID win-x64, which the contract does not list as supported",
        )

    def test_gate_native_source_rejects_supported_rid_left_unbound(self):
        fixture = self.fixture()
        arm_gate = "runtimes/linux-arm64/native/librgbverifycffi.so"
        arm_core = "runtimes/linux-arm64/native/librgblibcffi.so"
        fixture.write(arm_gate, b"arm gate")
        fixture.write(arm_core, b"arm core")
        contract = copy.deepcopy(fixture.contract)
        contract["plugin"]["supported_rids"]["linux-arm64"] = {"gate": arm_gate, "core": arm_core}
        source = self.gate_source(fixture, "built-gate.so", b"gate")
        self.assert_failed(
            self.run_verify(
                fixture,
                contract=fixture.contract_file(contract),
                gate_native_source=[f"linux-x64={source}"],
            ),
            "every supported RID must be bound to a build output; unbound: linux-arm64",
        )

    def test_gate_native_source_rejects_repeated_rid(self):
        fixture = self.fixture()
        source = self.gate_source(fixture, "built-gate.so", b"gate")
        self.assert_failed(
            self.run_verify(
                fixture, gate_native_source=[f"linux-x64={source}", f"linux-x64={source}"]
            ),
            "--gate-native-source names RID linux-x64 more than once",
        )

    def test_gate_native_source_rejects_missing_build_output(self):
        fixture = self.fixture()
        missing = fixture.root / "never-built.so"
        self.assert_failed(
            self.run_verify(fixture, gate_native_source=[f"linux-x64={missing}"]),
            f"--gate-native-source for linux-x64 is not an existing file: {missing}",
        )

    def test_gate_native_source_rejects_empty_build_output(self):
        fixture = self.fixture()
        source = self.gate_source(fixture, "built-gate.so", b"")
        self.assert_failed(
            self.run_verify(fixture, gate_native_source=[f"linux-x64={source}"]),
            f"--gate-native-source for linux-x64 is empty: {source}",
        )

    def test_gate_native_source_rejected_for_gate_package_inspection(self):
        fixture = self.fixture()
        package = fixture.root / "RgbVerifyCffi.1.2.3-test.nupkg"
        with zipfile.ZipFile(package, "w") as archive:
            archive.writestr("RgbVerifyCffi.nuspec", "<package/>")
        source = self.gate_source(fixture, "built-gate.so", b"gate")
        self.assert_failed(
            self.run_verify(
                fixture,
                package,
                strict=True,
                gate_package=True,
                gate_native_source=[f"linux-x64={source}"],
            ),
            "--gate-native-source applies to a plugin artifact, not to --gate-package inspection",
        )

    def test_absent_gate_native_source_reports_no_binding_and_still_passes(self):
        fixture = self.fixture()
        result = self.run_verify(fixture)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn(
            "gate native is NOT byte-bound to a build output: --gate-native-source was not given",
            result.stdout,
        )
        self.assertIn("PRE-PACKAGE MODE", result.stdout)

    def gate_package(
        self,
        fixture: ArtifactFixture,
        *,
        overrides: dict[str, bytes] | None = None,
        extras: dict[str, bytes] | None = None,
        compression: int = zipfile.ZIP_STORED,
        name: str = "RgbVerifyCffi.1.2.3-test.nupkg",
    ) -> Path:
        package = fixture.root / name
        gate_contract = fixture.contract["gate_package"]
        with zipfile.ZipFile(package, "w", compression) as archive:
            archive.writestr("RgbVerifyCffi.nuspec", "<package><metadata><id>RgbVerifyCffi</id></metadata></package>")
            archive.writestr(gate_contract["placeholder"], b"")
            for rid, relative in gate_contract["required_assets"].items():
                payload = (overrides or {}).get(rid, native_architecture.synthetic_native(rid))
                archive.writestr(relative, payload)
            for relative, payload in (extras or {}).items():
                archive.writestr(relative, payload)
        return package

    def test_strict_gate_package_inspection_accepts_three_rid_fixture(self):
        fixture = self.fixture()
        result = self.run_verify(
            fixture, self.gate_package(fixture), strict=True, gate_package=True
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("linux-x64, linux-arm64, osx-arm64", result.stdout)
        self.assertIn("linux-x64=ELF-64 x86-64", result.stdout)
        self.assertIn("linux-arm64=ELF-64 AArch64", result.stdout)
        self.assertIn("osx-arm64=Mach-O-64 arm64", result.stdout)

    def test_gate_package_rejects_x86_64_bytes_in_the_arm64_slot(self):
        fixture = self.fixture()
        package = self.gate_package(
            fixture, overrides={"linux-arm64": native_architecture.synthetic_native("linux-x64")}
        )
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertNotEqual(0, result.returncode, result.stdout)
        for expected in ("linux-arm64", "ELF-64 AArch64", "ELF-64 x86-64"):
            self.assertIn(expected, result.stderr)

    def test_gate_package_rejects_real_x86_64_native_in_the_arm64_slot(self):
        fixture = self.fixture()
        package = self.gate_package(fixture, overrides={"linux-arm64": CAPTURED_ELF_HEADER})
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("ELF-64 x86-64", result.stderr)

    def test_gate_package_rejects_plain_text_where_a_native_belongs(self):
        fixture = self.fixture()
        package = self.gate_package(fixture, overrides={"linux-x64": b"native"})
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("not a recognizable native object", result.stderr)

    def test_gate_package_rejects_truncated_elf_without_raising(self):
        fixture = self.fixture()
        package = self.gate_package(fixture, overrides={"linux-x64": CAPTURED_ELF_HEADER[:8]})
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("truncated ELF header", result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_gate_package_rejects_extra_mislabelled_entry_outside_the_contract(self):
        fixture = self.fixture()
        package = self.gate_package(
            fixture,
            extras={
                "runtimes/linux-arm64/native/other.so": native_architecture.synthetic_native("linux-x64")
            },
        )
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("runtimes/linux-arm64/native/other.so", result.stderr)

    def test_gate_package_rejects_entry_under_an_unknown_rid(self):
        fixture = self.fixture()
        package = self.gate_package(
            fixture,
            extras={"runtimes/solaris-sparc/native/librgbverifycffi.so": CAPTURED_ELF_HEADER},
        )
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("solaris-sparc", result.stderr)

    def test_gate_package_accepts_incidental_files_beside_the_natives(self):
        fixture = self.fixture()
        package = self.gate_package(
            fixture,
            extras={
                "runtimes/linux-x64/native/.DS_Store": b"\x00\x00\x00\x01Bud1",
                "runtimes/osx-arm64/native/librgbverifycffi.h": b"void rgbverify_validate(void);\n",
            },
        )
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_gate_package_still_catches_a_mislabelled_library_beside_an_incidental_file(self):
        fixture = self.fixture()
        package = self.gate_package(
            fixture,
            extras={
                "runtimes/linux-arm64/native/.DS_Store": b"Bud1",
                "runtimes/linux-arm64/native/other.so": native_architecture.synthetic_native("linux-x64"),
            },
        )
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("runtimes/linux-arm64/native/other.so", result.stderr)

    def test_gate_package_accepts_benign_extra_entry_and_deflate(self):
        fixture = self.fixture()
        package = self.gate_package(
            fixture,
            extras={"docs/readme.txt": b"not part of the contract\n"},
            compression=zipfile.ZIP_DEFLATED,
            name="RgbVerifyCffi.1.2.3-deflated.nupkg",
        )
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("gate package native architectures proved", result.stdout)


class NativeArchitectureTests(unittest.TestCase):
    def test_captured_real_headers_report_the_machine_their_rid_requires(self):
        for rid, captured in (
            ("linux-x64", CAPTURED_ELF_HEADER),
            ("osx-arm64", CAPTURED_MACHO_HEADER),
            ("win-x64", CAPTURED_PE_HEADER),
        ):
            with self.subTest(rid=rid):
                self.assertEqual(
                    native_architecture.expected_machine(rid),
                    native_architecture.observed_machine(captured),
                )

    def test_synthetic_headers_agree_with_the_captured_real_headers(self):
        for rid, captured in (
            ("linux-x64", CAPTURED_ELF_HEADER),
            ("osx-arm64", CAPTURED_MACHO_HEADER),
            ("win-x64", CAPTURED_PE_HEADER),
        ):
            with self.subTest(rid=rid):
                self.assertEqual(
                    native_architecture.observed_machine(captured),
                    native_architecture.observed_machine(native_architecture.synthetic_native(rid)),
                )

    def test_synthetic_header_for_the_rid_no_machine_has_ever_built(self):
        self.assertEqual(
            native_architecture.expected_machine("linux-arm64"),
            native_architecture.observed_machine(native_architecture.synthetic_native("linux-arm64")),
        )

    def test_elf_class_is_part_of_the_identity(self):
        thirty_two_bit = bytearray(CAPTURED_ELF_HEADER)
        thirty_two_bit[4] = 1
        self.assertEqual("ELF-32 x86-64", native_architecture.observed_machine(bytes(thirty_two_bit)))
        self.assertNotEqual(
            native_architecture.expected_machine("linux-x64"),
            native_architecture.observed_machine(bytes(thirty_two_bit)),
        )

    def test_big_endian_elf_machine_is_read_with_the_declared_byte_order(self):
        big_endian = bytearray(CAPTURED_ELF_HEADER)
        big_endian[5] = 2
        big_endian[18:20] = (183).to_bytes(2, "big")
        self.assertEqual("ELF-64 AArch64", native_architecture.observed_machine(bytes(big_endian)))

    def test_non_native_inputs_are_named_rather_than_raising(self):
        for data, needle in (
            (b"", "the file is empty"),
            (b"native", "not a recognizable native object"),
            (CAPTURED_ELF_HEADER[:8], "truncated ELF header"),
            (b"\xca\xfe\xba\xbe" + bytes(28), "universal archive"),
        ):
            with self.subTest(needle=needle):
                self.assertIn(needle, native_architecture.observed_machine(data))

    def test_incidental_files_beside_a_native_are_not_treated_as_natives(self):
        for incidental in (
            "runtimes/linux-x64/native/.DS_Store",
            "runtimes/linux-x64/native/._librgbverifycffi.so",
            "runtimes/linux-x64/native/librgbverifycffi.h",
            "runtimes/linux-x64/native/notes.txt",
        ):
            with self.subTest(incidental=incidental):
                self.assertFalse(native_architecture.names_a_native_library(incidental))

    def test_real_library_names_are_treated_as_natives(self):
        for name in (
            "runtimes/linux-x64/native/librgbverifycffi.so",
            "runtimes/osx-arm64/native/librgbverifycffi.dylib",
            "runtimes/win-x64/native/rgbverifycffi.dll",
        ):
            with self.subTest(name=name):
                self.assertTrue(native_architecture.names_a_native_library(name))

    def test_staging_tree_ignores_a_ds_store_but_still_checks_the_library(self):
        with tempfile.TemporaryDirectory() as directory:
            native_directory = Path(directory) / "linux-x64" / "native"
            native_directory.mkdir(parents=True)
            (native_directory / ".DS_Store").write_bytes(b"\x00\x00\x00\x01Bud1")
            library = native_directory / "librgbverifycffi.so"
            library.write_bytes(native_architecture.synthetic_native("linux-x64"))
            accepted = self.run_module("--staging-tree", directory)
            self.assertEqual(0, accepted.returncode, accepted.stdout + accepted.stderr)
            library.write_bytes(native_architecture.synthetic_native("linux-arm64"))
            rejected = self.run_module("--staging-tree", directory)
            self.assertNotEqual(0, rejected.returncode, rejected.stdout)
            self.assertIn("ELF-64 AArch64", rejected.stderr)

    def test_staging_tree_rejects_a_directory_holding_only_incidental_files(self):
        with tempfile.TemporaryDirectory() as directory:
            native_directory = Path(directory) / "linux-x64" / "native"
            native_directory.mkdir(parents=True)
            (native_directory / ".DS_Store").write_bytes(b"Bud1")
            result = self.run_module("--staging-tree", directory)
            self.assertNotEqual(0, result.returncode, result.stdout)
            self.assertIn("no <rid>/native/ native library", result.stderr)

    def test_expected_machine_rejects_an_unknown_rid(self):
        with self.assertRaises(native_architecture.ArchitectureError) as caught:
            native_architecture.expected_machine("solaris-sparc")
        self.assertIn("solaris-sparc", str(caught.exception))

    def run_module(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(ARCHITECTURE_MODULE), *arguments],
            text=True,
            capture_output=True,
            check=False,
        )

    def test_cli_assert_accepts_a_matching_rid_and_rejects_a_mismatched_one(self):
        with tempfile.TemporaryDirectory() as directory:
            native = Path(directory) / "librgbverifycffi.so"
            native.write_bytes(native_architecture.synthetic_native("linux-x64"))
            matched = self.run_module("--assert", f"linux-x64={native}")
            self.assertEqual(0, matched.returncode, matched.stdout + matched.stderr)
            self.assertIn("ELF-64 x86-64", matched.stdout)
            mismatched = self.run_module("--assert", f"linux-arm64={native}")
            self.assertNotEqual(0, mismatched.returncode, mismatched.stdout)
            self.assertIn("ELF-64 AArch64", mismatched.stderr)
            self.assertIn("ELF-64 x86-64", mismatched.stderr)

    def test_cli_staging_tree_rejects_a_tree_with_no_natives(self):
        with tempfile.TemporaryDirectory() as directory:
            result = self.run_module("--staging-tree", directory)
            self.assertNotEqual(0, result.returncode, result.stdout)
            self.assertIn("no <rid>/native/ native library", result.stderr)

    def test_cli_requires_exactly_one_mode(self):
        result = self.run_module()
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("exactly one of", result.stderr)


if __name__ == "__main__":
    unittest.main()
