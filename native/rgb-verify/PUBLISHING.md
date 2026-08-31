# Publishing `RgbVerifyCffi` to NuGet

`RgbVerifyCffi` is the native trust core for RGB pre-sign intent verification (`rgbverifycffi`). It is
built from the Rust sources in this directory and does **not** link rgb-lib. The plugin consumes it as a
`PackageReference`, so the gate native reaches merchants through the package rather than through a binary
committed to this repository.

## Who is allowed to publish

The NuGet package ID is claimed by whichever account first pushed it. **A different account pushing the
same ID is rejected with HTTP 403**, however valid its API key is. So before a new maintainer can publish:

* add their nuget.org account as a co-owner on the package page (Manage Owners), or
* transfer ownership to them, or
* publish under a different ID they own — which means changing `PackageId` in
  `packaging/RgbVerifyCffi.csproj`, the `PackageReference` in `BTCPayServer.Plugins.RgbUtexo.csproj`, and
  regenerating both `packages.lock.json` files.

Prefer owning the package through a nuget.org **organization** rather than a personal account, so
ownership does not depend on one individual.

## Prerequisites

* Docker (the Linux RIDs are built in `rust:1-bookworm` containers).
* An API key from nuget.org with the **"Push new packages and package versions"** scope. For a brand-new
  package ID also set the package glob to the ID, because there is no existing package to select.
* The RID set is declared once, in the `GateRid` items of `packaging/RgbVerifyCffi.csproj`. Adding or
  dropping a RID is a one-line change there and every guard and script reads it from that one place.

## Build, pack and verify

```bash
bash scripts/pack-rgbverify.sh --stage --require-all-rids --version <VERSION>
bash scripts/pack-rgbverify.sh --verify
```

The first command builds every declared RID, stages them under `runtimes/<rid>/native/`, and packs into
`local-nuget-feed/`. `--require-all-rids` makes a missing RID a hard failure instead of silently shipping
an incomplete package — always pass it for a release. `--verify` re-checks the packed natives, runs all
three pack-time guards, and loads the extracted native on Debian; it needs the staged natives, so run it
after `--stage`, never on a bare checkout.

The guards are not optional and must not be worked around. They check that each native exists, that its
ELF `e_machine` / Mach-O cputype actually matches the RID it is filed under, and that it exports every
symbol in `RgbNativeSelfCheck.RequiredExports()`.

## Push

```bash
read -rs NUGET_API_KEY && export NUGET_API_KEY
dotnet nuget push "local-nuget-feed/RgbVerifyCffi.<VERSION>.nupkg" \
  --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json
```

`read -rs` keeps the key out of shell history. Never commit a key, and never paste one into an issue, a
log, or a chat.

## After publishing

Point the plugin at the new version and refresh the lockfiles:

```bash
# edit BTCPayServer.Plugins.RgbUtexo.csproj: <PackageReference Include="RgbVerifyCffi" Version="<VERSION>" />
dotnet restore BTCPayServer.Plugins.RgbUtexo.csproj --force-evaluate
dotnet restore BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj --force-evaluate
```

Both lockfiles must be regenerated and committed together with the version change: the test project has a
`ProjectReference` to the plugin, so its lockfile captures the plugin's transitive graph. CI runs
`dotnet restore --locked-mode` and fails on drift.

Then confirm the shipped artifact really carries the package's bytes, rather than a stray local file:

```bash
dotnet publish BTCPayServer.Plugins.RgbUtexo.csproj -c Release -o <out>
python3 scripts/verify_plugin_artifact.py <out> \
  --provenance strict --package-cache "${NUGET_PACKAGES:-$HOME/.nuget/packages}"
```

Strict mode requires the gate native in the publish to be declared `assetType: native` of
`RgbVerifyCffi` in the plugin's own `.deps.json` **and** to be byte-identical to the restored package
copy. That declaration is what distinguishes a package-delivered native from a local one — a hash on its
own cannot, because a local copy of the same build has the same hash.

## Two things that cannot be undone

* **A published version is permanent.** nuget.org allows unlisting, never deletion. Use an unambiguous
  prerelease suffix while iterating.
* **The first push claims the package ID** for that account, permanently.

## Signing

A push authenticated with an API key gets a nuget.org **repository** signature. That attests the package
was not altered on the feed; it is not a publisher attestation. **Author** signing is separate and needs a
code-signing certificate registered with the owning account. Do not describe a package as author-signed
without checking:

```bash
dotnet nuget verify --all "<path-to>.nupkg"
```
