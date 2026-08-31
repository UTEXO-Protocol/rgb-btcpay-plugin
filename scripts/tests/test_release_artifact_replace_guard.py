from __future__ import annotations

import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release.yml"
STEP_NAME = "- name: Create / replace GitHub Release"
ARTIFACT_NAME = "BTCPayServer.Plugins.RgbUtexo.btcpay"
TAG = "v9.9.9"

FAKE_GH = """#!/bin/sh
set -eu
if [ "$1" = "release" ] && [ "$2" = "view" ]; then
  [ "$FAKE_RELEASE_EXISTS" = "true" ] && exit 0 || exit 1
fi
if [ "$1" = "release" ] && [ "$2" = "download" ]; then
  [ "$FAKE_DOWNLOAD_SUCCEEDS" = "true" ] || exit 1
  out=""
  prev=""
  for a in "$@"; do
    if [ "$prev" = "-O" ]; then out="$a"; fi
    prev="$a"
  done
  printf '%s  %s\\n' "$FAKE_EXISTING_HASH" "$ARTIFACT_NAME" > "$out"
  exit 0
fi
if [ "$1" = "release" ] && [ "$2" = "delete" ]; then
  echo DELETE_CALLED >> "$GH_CALL_LOG"
  exit 0
fi
if [ "$1" = "release" ] && [ "$2" = "create" ]; then
  echo CREATE_CALLED >> "$GH_CALL_LOG"
  exit 0
fi
echo "fake gh: unhandled args: $*" >&2
exit 1
"""


def extract_release_step_script() -> str:
    text = WORKFLOW.read_text(encoding="utf-8")
    start = text.index(STEP_NAME)
    run_marker = "run: |\n"
    run_at = text.index(run_marker, start)
    body_start = run_at + len(run_marker)
    lines = text[body_start:].splitlines(keepends=True)

    body_indent = None
    collected: list[str] = []
    for line in lines:
        stripped = line if line.strip() == "" else line.lstrip(" ")
        indent = len(line) - len(line.lstrip(" "))
        if line.strip() == "":
            collected.append(line)
            continue
        if body_indent is None:
            body_indent = indent
        if indent < body_indent:
            break
        collected.append(line[body_indent:])
    script = "".join(collected)
    assert "gh release delete" in script, "extraction missed the delete call — indentation math is wrong"
    assert "gh release create" in script, "extraction missed the create call — indentation math is wrong"
    return script


class ReleaseArtifactReplaceGuardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.workdir = Path(self.tmp.name)

        self.bindir = self.workdir / "bin"
        self.bindir.mkdir()
        gh_path = self.bindir / "gh"
        gh_path.write_text(FAKE_GH, encoding="utf-8")
        gh_path.chmod(gh_path.stat().st_mode | stat.S_IEXEC)

        self.script_path = self.workdir / "release-step.sh"
        self.script_path.write_text(extract_release_step_script(), encoding="utf-8")

        self.call_log = self.workdir / "gh-calls.log"
        (self.workdir / f"{ARTIFACT_NAME}.sha256").write_text(
            f"newhash000  {ARTIFACT_NAME}\n", encoding="utf-8"
        )

    def run_step(self, *, release_exists: bool, download_succeeds: bool,
                 existing_hash: str, allow_replace: bool) -> subprocess.CompletedProcess:
        import os
        env = dict(os.environ)
        env["PATH"] = f"{self.bindir}:{env['PATH']}"
        env["TAG"] = TAG
        env["ARTIFACT_NAME"] = ARTIFACT_NAME
        env["GH_TOKEN"] = "unused"
        env["PRERELEASE"] = "false"
        env["ALLOW_ARTIFACT_REPLACE"] = "true" if allow_replace else "false"
        env["FAKE_RELEASE_EXISTS"] = "true" if release_exists else "false"
        env["FAKE_DOWNLOAD_SUCCEEDS"] = "true" if download_succeeds else "false"
        env["FAKE_EXISTING_HASH"] = existing_hash
        env["GH_CALL_LOG"] = str(self.call_log)
        return subprocess.run(
            ["bash", str(self.script_path)],
            cwd=self.workdir,
            env=env,
            capture_output=True,
            text=True,
        )

    def calls(self) -> str:
        return self.call_log.read_text(encoding="utf-8") if self.call_log.exists() else ""

    def test_no_existing_release_creates_without_deleting(self):
        result = self.run_step(release_exists=False, download_succeeds=False,
                                existing_hash="", allow_replace=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn("DELETE_CALLED", self.calls())
        self.assertIn("CREATE_CALLED", self.calls())

    def test_matching_hash_replaces_without_opt_in(self):
        result = self.run_step(release_exists=True, download_succeeds=True,
                                existing_hash="newhash000", allow_replace=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("DELETE_CALLED", self.calls())
        self.assertIn("CREATE_CALLED", self.calls())

    def test_mismatched_hash_without_opt_in_refuses(self):
        result = self.run_step(release_exists=True, download_succeeds=True,
                                existing_hash="oldhash111", allow_replace=False)
        self.assertEqual(result.returncode, 1)
        self.assertNotIn("DELETE_CALLED", self.calls())
        self.assertNotIn("CREATE_CALLED", self.calls())
        self.assertIn("Refusing to replace it silently", result.stdout)

    def test_mismatched_hash_with_opt_in_replaces(self):
        result = self.run_step(release_exists=True, download_succeeds=True,
                                existing_hash="oldhash111", allow_replace=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("DELETE_CALLED", self.calls())
        self.assertIn("CREATE_CALLED", self.calls())

    def test_unreadable_published_hash_without_opt_in_refuses(self):
        result = self.run_step(release_exists=True, download_succeeds=False,
                                existing_hash="", allow_replace=False)
        self.assertEqual(result.returncode, 1)
        self.assertNotIn("DELETE_CALLED", self.calls())
        self.assertNotIn("CREATE_CALLED", self.calls())

    def test_unreadable_published_hash_with_opt_in_replaces(self):
        result = self.run_step(release_exists=True, download_succeeds=False,
                                existing_hash="", allow_replace=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("DELETE_CALLED", self.calls())
        self.assertIn("CREATE_CALLED", self.calls())


if __name__ == "__main__":
    unittest.main()
