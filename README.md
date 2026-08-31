# RGB BTCPay Server Plugin

> **Beta notice:** This plugin is beta because the RGB protocol implementation it builds on is still prerelease — no stable release of the underlying RGB libraries exists yet. Their versions are fixed by a committed lockfile, but a prerelease implementation can still change. Test thoroughly in a development environment before using in production.

Accept RGB asset payments (tokens, stablecoins) in BTCPay Server.

[![BTCPay Server](https://img.shields.io/badge/BTCPay%20Server-Plugin-brightgreen)](https://btcpayserver.org)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com)

## Features

- Accept RGB payments alongside Bitcoin. The supported schema is **NIA** (non-inflatable fungible
  assets). CFA and UDA are **not** supported, and the plugin declares that to rgb-lib rather than
  claiming otherwise, so an asset it could not afterwards enumerate or spend is never accepted
  into a wallet in the first place
- Issue new RGB assets directly from BTCPay
- Two-step invoice settlement (Processing → Settled) matching BTCPay's native Bitcoin flow
- Full UTXO management for RGB allocations
- BTC transaction history for wallet operations
- Configurable minimum confirmations for payment settlement
- Native rgb-lib integration (no external RGB Node required)

## Installation

### Via Plugin Builder (Recommended)

1. Go to your BTCPay Server **Settings** → **Plugins**
2. Search for "RGB Payments"
3. Click **Install**
4. Restart BTCPay Server

### Manual Installation

1. Download the latest release from the [Plugin Builder](https://plugin-builder.btcpayserver.org/public/plugins)
2. Extract to your BTCPay Server plugins directory
3. Restart BTCPay Server

## Configuration

### Environment Variables

```bash
# Indexer for blockchain data. This overrides the per-network default from the Network Defaults
# table below for EVERY network, so set it only when you run your own indexer, and make sure the
# value matches the network your wallets are on. Leave it unset to use the defaults.
RGB_ELECTRUM_URL=ssl://electrum.iriswallet.com:50003

# Parent directory for RGB wallet data; each wallet lands in
# <base>/<Network>/rgb-wallets/<wallet-id>. Without rgb.json the base is this variable, or the
# parent of the BTCPay data directory when it is unset. Two cases ignore it: an explicit
# rgb_base_dir in rgb.json always wins, and an rgb.json that omits the key keeps the built-in
# default /data whenever that directory already exists on the host (a warning names both paths).
RGB_BASE_DIR=/data

# RGB proxy endpoint for consignment exchange
RGB_PROXY_ENDPOINT=rpcs://proxy.iriswallet.com/0.2/json-rpc

# Bounds on automatic colorable-UTXO creation, which signs and broadcasts a Bitcoin
# transaction unattended. Set the cap to 0 to disable it entirely; the "Create UTXOs"
# button keeps working. Automatic creation also requires RGB to be enabled for the store.
RGB_MAX_AUTO_COLORABLE_UTXOS=50
# Minimum gap between automatic creations for one wallet. A value of 1..19 is raised
# to 20 and a value of 0 or less falls back to 30, because the gate compares against
# an instant stamped mid-sweep and a gap under twice the 10-minute sweep interval
# could never take effect.
RGB_AUTO_UTXO_COOLDOWN_MINUTES=30
RGB_AUTO_UTXO_MAX_BACKOFF_MINUTES=160

# Deadlines for the out-of-process RGB send. Each of send_begin and send_end pays a fresh
# rgb-lib wallet construction plus an indexer handshake and chain sync before the native call
# starts, and send_begin also uploads a consignment to the RGB proxy — all inside this budget.
# Raise it if sends fail on a slow or congested indexer; the default is 30 seconds and a send
# that exceeds it is killed and retried identically forever. Accepted range 1-600 seconds; a
# larger value is raised to 600, never ignored.
RGB_NATIVE_SEND_TIMEOUT_SECONDS=30
RGB_NATIVE_SEND_CPU_LIMIT_SECONDS=30

# Largest amount of memory the out-of-process RGB send helper may use, in bytes. Accepted range
# 67108864..2147483648; a value outside it is clamped rather than ignored. The budget covers the
# whole helper, including the rgb-lib wallet construction and chain sync every send pays before
# the native call starts, so a wallet holding many transfers or allocations can need more than
# the shipped 512 MiB. Raise it if sends are stopped with a native memory limit message; a wallet
# whose helper is stopped there every time cannot move its assets at all.
RGB_NATIVE_SEND_RAM_CAP_BYTES=536870912

# Largest amount of memory the out-of-process backup restore may use, in bytes. Accepted range
# 624951296..4294967296; a value outside it is clamped rather than ignored, so this knob can only
# raise the budget. This limit is measured on the whole restore helper process, while the pre-flight
# guard bounds only the scrypt arena declared inside the backup file, so the floor is that arena
# ceiling plus the resident set the helper needs outside it: a floor at the arena ceiling alone would
# kill a backup that guard had just passed.
RGB_RESTORE_RAM_CAP_BYTES=624951296

# Largest number of staging entries a restored wallet directory may hold. This counts directories
# as well as files, and it is counted after rgb-lib decompresses the wallet directory, while
# backup validation counts only the entries of the outer archive — which holds a single encrypted
# file — so a backup validation accepted can still be refused here. rgb-lib never prunes the
# per-transfer files and directories it writes, so a wallet with many thousands of transfers
# reaches this count legitimately while staying far under RGB_RESTORE_DISK_CAP_BYTES. A value of
# 0 or less is ignored; a value below 1000 is raised to 1000; there is no upper clamp.
RGB_RESTORE_MAX_STAGING_ENTRIES=20000

# Deadlines for the out-of-process backup restore. Accepted range 1-3600 seconds; a larger value
# is raised to 3600. Raising these does not weaken the restore watchdog's other limits: its disk,
# memory and staging-entry caps are evaluated on every poll independently of the deadline, so a
# hostile backup is still killed on whichever cap it breaches first.
RGB_RESTORE_TIMEOUT_SECONDS=30
RGB_RESTORE_CPU_LIMIT_SECONDS=30

# Largest wallet directory a restore may unpack into its staging directory, in bytes. Accepted
# range 52428800..4294967296; a value outside it is clamped rather than ignored. This cap is
# measured on the wallet directory AFTER rgb-lib decompresses it, while the upload bound and
# backup validation measure the compressed, encrypted archive, so a backup those accepted can
# still be refused here. Raise it if a restore of a genuine, large wallet is stopped with a
# staging size limit message; the backup file is undamaged and the restore can simply be retried.
RGB_RESTORE_DISK_CAP_BYTES=536870912
```

An unparseable or non-positive value for any of the four timing knobs, or for any of the byte caps
above, is ignored, leaving the configured value in place. A value above the stated range is raised to
the range's maximum rather than ignored, so over-asking never silently leaves the 30-second default
behind.

### Configuration File

Alternatively, create `rgb.json` in your BTCPay Server data directory:
```json
{
  "rgb_base_dir": "/data",
  "native_send_timeout_seconds": 30,
  "max_auto_colorable_utxos": 50,
  "checkout_invoice_hot_scan_window_hours": 72
}
```

`checkout_invoice_hot_scan_window_hours` sizes the *hot* tier of the payment-detection scan for
invoices created before this release. Each poll the listener inspects three slices of a store's
unfinished checkout invoices: the newest few, a page of every invoice BTCPay could still credit walked
by a durable cursor, and a page of the older tail walked by a second cursor. An invoice created by this
release records BTCPay's own absolute monitoring deadline, so it stays in that middle tier until two
days past **its own** deadline however the store or that invoice's `checkout.monitoringMinutes` is set;
an invoice created by an earlier release has no such record and falls back to its expiry plus this
window instead. A brand-new invoice is therefore inspected on the next poll however large the history
is, every still-payable invoice is revisited on a schedule set by how many are in flight rather than by
how many the store has ever created, and nothing in the tail is ever left permanently unvisited. No
checkout row is ever discarded: an unpaid one simply ages into the tail. The window is never shorter
than the store's BTCPay **monitoring expiration** plus two days, and never shorter than 48 hours; a
value outside those bounds is clamped, never honoured.

`rgb_base_dir` is the parent of every wallet's RGB data directory, and the key name matters:
an unrecognised key is ignored silently. If you write this file, **set `rgb_base_dir`
explicitly** — the file replaces the whole configuration object, so omitting the key falls back to
the built-in default `/data`. The plugin only substitutes the directory it would have chosen without
the file when `/data` does not exist at all, and logs a warning naming both paths when it declines;
that keeps an existing `/data` deployment where it is, and an existing wallet directory is never
moved between parents.

The knob variables above are applied after the file, so they win over it. `RGB_BASE_DIR` is the one
exception: an explicit `rgb_base_dir` in the file wins over it.

### Upgrading from 1.0.9

Two changes in this release require merchant action. Neither is applied automatically, and until you
act, **RGB is unavailable at checkout**. Invoice creation itself fails only when no payment method on
the invoice ends up priced or awaiting activation — so an RGB-only store fails outright if RGB is
eager, but still creates the invoice if RGB is configured as a lazy payment method.

**1. Rate rules must be rewritten against the contract-derived pricing code.** Pricing is no longer
keyed on the asset ticker; it is keyed on a code derived from the contract id, of the form `RGB2`
followed by 64 hex characters. A rule written against a ticker (`USDT_USD = ...`) no longer matches
anything, and a wildcard rule (`X_X = ...`) never priced a contract. Open **RGB Wallet → Settings**:
the page prints this store's exact pricing code, a rate rule to paste, and a fixed-rate form for the
case where you are deliberately asserting a 1:1 peg. If your store is still on BTCPay's default
exchange rates, the page also explains that a rule naming the code requires rate scripting.

You will also now receive a notification the first time an invoice is refused for this reason —
**but only for invoices priced in your store's default currency**, which is also the pair the RGB
settings page checks. That is the case where ordinary checkout stops, so it is the one worth
interrupting you for; a per-currency notification would fire once for every quote currency a store
ever sees.

If your integration prices invoices in some other currency, neither surface will mention it: each pair
needs its own rate rule. **Wherever you see it, the RGB refusal itself always names the exact
`RGB2…_<CURRENCY>` pair to add** — what varies is only whether you meet it as an error or have to go
looking for it.

What decides that is not your store's configuration but a single question: **does any payment method on
the invoice end up either priced or awaiting activation?** BTCPay rejects invoice creation only when
*none* does.

- **At least one does** — creation **succeeds**, and RGB's refusal is recorded only in that invoice's own
  event log, on the invoice page in BTCPay. This is the usual outcome, and there are two ways to reach it
  that are easy to miss: another enabled method priced successfully, *or* RGB itself is configured as a
  **lazy** payment method, in which case it is not evaluated at creation at all and the refusal is
  recorded later, when a customer activates RGB at checkout.
- **None does** — creation **fails** and the error carries the refusal. This needs every method on the
  invoice to be unavailable, so it is not simply "RGB is the only method": an RGB-only store whose RGB is
  lazy still creates the invoice, and an RGB-only store whose RGB is eager does fail — as does a store
  with other methods when those also fail their own rate or availability checks.
- **Zero-price invoices** skip payment-method pricing altogether, so nothing is evaluated and nothing is
  recorded either way.

So if RGB silently stops appearing on invoices priced in a non-default currency, open a recent invoice
and read its event log — that is where the answer is in every case except the hard failure, which tells
you directly. If you create invoices in several currencies, add a rule for each pair rather than relying
on any of these notices.

**2. Two store settings were removed, not renamed.**

- `maxAllocationsPerUtxo` is gone from the payment-method configuration. The value lives on the
  wallet row, is fixed when the wallet is created, and the settings page shows it read-only.
- `allowOneToOneRateFallback` is gone. The opt-in 1:1 rate fallback was **deleted** rather than
  narrowed, because it applied to any quote currency. A store that had it enabled loses it the first
  time the configuration is re-serialized and **cannot re-enable it** — that is by design. If you
  intend a 1:1 peg, assert it explicitly with the fixed-rate rule the settings page prints, which
  binds the peg to one contract and one quote currency instead of all of them.

Both keys are simply absent from the configuration type, so sending either through the Greenfield
API is ignored rather than rejected.

**3. CFA is no longer declared to rgb-lib.** No action is needed, but the behaviour is worth knowing.
The plugin reads only the NIA collection from rgb-lib's asset list, so a CFA asset was never shown,
never priced and never spendable — yet earlier versions told rgb-lib they supported the schema, so
such an asset could be accepted into a wallet and then sit there invisible. It is now refused at the
boundary instead. If a wallet took in a CFA asset under an earlier version it was already invisible
and still is; nothing about this release makes a previously usable asset unusable.

### Network Defaults

| Network | Default Electrum URL | Proxy Endpoint |
|---------|---------------------|----------------|
| Mainnet | ssl://electrum.iriswallet.com:50003 | rpcs://proxy.iriswallet.com/0.2/json-rpc |
| Testnet | ssl://electrum.iriswallet.com:50013 | rpcs://proxy.iriswallet.com/0.2/json-rpc |
| Signet | ssl://electrum.iriswallet.com:50033 | rpcs://proxy.iriswallet.com/0.2/json-rpc |
| Utexo | https://esplora-api.utexo.com | rpcs://rgb-proxy.utexo.com/json-rpc |
| Regtest | tcp://regtest.thunderstack.org:50001 | rpc://regtest.thunderstack.org:3000/json-rpc |

## User Guide

### Step 1: Create an RGB Wallet

1. Open your BTCPay Server store
2. In the left sidebar, click **RGB Wallet**
3. You will land on the **Setup** page
4. Enter a wallet name (e.g. "My RGB Wallet")
5. Select the network — **Regtest**, **Testnet**, **Signet**, **Utexo** or **Mainnet**
6. Click **Create Wallet**

The plugin generates a new wallet with two keypairs: one for regular BTC transactions and one for RGB (colored) operations. The mnemonic is encrypted and stored securely within BTCPay.

### Step 2: Fund the Wallet with BTC

RGB operations require on-chain Bitcoin for transaction fees and UTXO creation.

1. On the **RGB Wallet** dashboard, copy the wallet address shown in the "Wallet Address" card
2. Send a small amount of BTC to this address (0.01 BTC is enough for regtest/testnet)
3. Wait for the transaction to confirm (30+ blocks on regtest)
4. Click the **Refresh** button on the dashboard to update balances

You should see your BTC balance update in the "BTC Balance" card.

### Step 3: Create Colorable UTXOs

RGB assets are stored on special "colorable" UTXOs. You need to create them before you can receive any RGB payments.

1. On the dashboard, click **Manage UTXOs** (or navigate to **UTXOs** in the sidebar)
2. Click the **Create UTXOs** button
3. Wait for the transaction to confirm (30+ blocks on regtest)
4. Go back to the dashboard and click **Refresh**

The "Colorable UTXOs" card on the dashboard should now show a non-zero count. Each UTXO can hold multiple RGB allocations.

### Step 4: Issue an RGB Asset (Optional)

If you want to create your own token:

1. On the dashboard, click **Issue New Asset** (or navigate to **Assets** → **Issue New Asset**)
2. Fill in the form:
   - **Ticker** — Short symbol, 2-8 characters (e.g. "USDT", "TOKEN")
   - **Name** — Full name of the asset (e.g. "My Stablecoin")
   - **Amount** — Total supply to issue
   - **Precision** — Decimal places (0 = integer tokens, 2 = cents, 8 = like satoshis)
3. Click **Issue Asset**

The new asset will appear on your Assets page and in the dashboard.

### Step 5: Configure Payment Settings

1. Navigate to **Settings** (gear icon on the dashboard, or sidebar)
2. Under **Payment Configuration**:
   - **Accepted Asset** — Select the RGB asset customers must use for payment. RGB invoices will only accept this asset.
3. Under **UTXO Settings** (optional):
   - **UTXO Count** — How many colorable UTXOs to create at once (default: 4)
   - **UTXO Size** — Size of each UTXO in satoshis (default: 1000)
   - **Max Allocations per UTXO** — display only. It is fixed on the wallet row when the wallet is
     created and is no longer settable here or through the API.
4. Under **Settlement**:
   - **Min Confirmations** — Number of blockchain confirmations required before marking a payment as settled (default: 1)
5. Click **Save Payment Settings**

### Step 6: Accept Payments

Once configured, RGB will appear as a payment method on your invoices:

1. Create an invoice in BTCPay (via UI or API)
2. On the checkout page, the customer will see an **RGB** payment option
3. The customer copies the RGB invoice string (starts with `rgb:`)
4. The customer pays using any RGB-compatible wallet (e.g. Iris Wallet, BitMask)
5. The invoice will transition through these states:
   - **New** — Waiting for payment
   - **Processing** — Payment detected, waiting for blockchain confirmations
   - **Settled** — Payment fully confirmed

### Sending BTC and RGB Assets

**Send RGB Asset** takes an `rgb:` invoice from the recipient, the asset and the amount. Every send
passes the pre-sign gate described under [Security Model](#security-model) before anything is signed.

**Send BTC** moves plain (vanilla) sats out of the wallet — used to recover funds, or to clear a
stuck rgb-lib reservation.

**This path spends confirmed outputs only.** An unconfirmed output can still be replaced or evicted
by whoever created it, and a payment built on one can then never confirm — while BTCPay has already
reported it sent, with a txid. So the send form shows two figures:

- **Vanilla (confirmed, sendable)** — what the wallet can actually spend right now. This is the
  number **Send max** fills in and the maximum the amount field will accept.
- **Awaiting confirmation** — deposits seen but not yet mined. This is *not* spendable, and appears
  only when there is some.

If the confirmed balance cannot cover the amount plus the network fee, the send is refused and the
message tells you how much is confirmed, roughly what the fee would be, how much is still waiting to
be mined, and an amount that will go through. Waiting for a block is the whole remedy — nothing needs
to be reconfigured. Sending the full confirmed balance deducts the fee from the amount rather than
refusing, so the destination receives slightly less than the figure shown.

### Monitoring

- **Dashboard** — Overview of BTC balance, colored balance, UTXO count, and asset list
- **Transfers** — View all incoming and outgoing RGB transfers with status (Pending → Settled)
- **BTC Transactions** — View on-chain Bitcoin transactions (UTXO creation, RGB sends, etc.)
- **UTXOs** — See which UTXOs hold RGB allocations and which are available

### Deleting a Wallet

1. Go to **Settings** → scroll to **Danger Zone**
2. Click **Delete Wallet**
3. Confirm the action

This removes the wallet from BTCPay (DB records, assets, invoices) but leaves the wallet data directory on disk for backup purposes.

Deletion is **refused**, not queued, while the wallet could still be mid-transfer — the message names
which case you hit: a pending durable recovery, a staged outbound transfer rgb-lib has not resolved,
or native access still in flight. This is deliberate: deleting the row is what the startup recovery
sweep uses to find such a transfer again, so removing it early would strand the transfer with no way
back. Let the transfer settle or fail, then retry the deletion.

## Invoice Settlement Flow

The plugin follows BTCPay's native two-step payment lifecycle:

```
Customer pays
       │
       ▼
RGB transfer detected (status: WaitingConfirmations)
       │
       ▼
BTCPay invoice → Processing
       │
       ▼ (blockchain confirmations reach threshold)
       │
RGB transfer confirmed (status: Settled)
       │
       ▼
BTCPay invoice → Settled
```

The plugin polls for transfer updates every 10 seconds. The number of confirmations required is configurable in Settings (default: 1).

## Building from Source

### Prerequisites

- .NET 10.0 SDK
- BTCPay Server source (as submodule)

### Build

```bash
git clone --recursive https://github.com/UTEXO-Protocol/rgb-btcpay-plugin
cd rgb-btcpay-plugin
dotnet build
```

### Plugin Builder Deployment

This plugin is designed for the [BTCPay Plugin Builder](https://github.com/btcpayserver/btcpayserver-plugin-builder):

1. Fork this repository
2. Register at https://plugin-builder.btcpayserver.org
3. Add your repository
4. Plugin Builder will build and publish automatically

## Architecture

```
BTCPayServer.Plugins.RgbUtexo/
├── Controllers/          # MVC controllers
├── Data/                 # EF Core entities & migrations
├── Models/               # View models
├── PaymentHandler/       # BTCPay payment integration
├── Services/
│   ├── RgbLibService.cs       # rgb-lib P/Invoke wrapper
│   ├── RgbLibWalletHandle.cs  # Wallet lifecycle management
│   ├── RGBWalletService.cs    # Wallet business logic
│   ├── MemoryWalletSigner.cs  # Local PSBT signing (NBitcoin)
│   ├── RgbWalletSignerProvider.cs # Signer management
│   ├── MnemonicProtectionService.cs # Mnemonic encryption
│   └── RGBInvoiceListener.cs  # Payment detection & settlement
├── Views/                # Razor views
└── native/rgb-verify/    # Rust native trust core (rgbverifycffi):
                          #   independent invoice decode + consignment
                          #   commitment verification for the pre-sign gate
                          #   (does NOT link rgb-lib — separate trust domain)
```

## Security Model

This plugin implements a **server-side custodial hot-wallet**. The BTCPay server operator holds the keys.

- **Mnemonic storage:** BIP-39 seed phrases are generated server-side and stored in the BTCPay database, encrypted with ASP.NET DataProtection.
- **Signing:** All transaction signing happens in-process on the BTCPay server. Any user with `CanModifyStoreSettings` permission can trigger signing operations.
- **Key access:** The seed phrase can be viewed by store admins after password re-verification (rate-limited).

> **Important:** This is custodial software. If the server is compromised, wallet funds are at risk. For production deployments, ensure your BTCPay server is properly secured. Back up your DataProtection key ring — losing it means losing access to all encrypted mnemonics.

### RGB Send Intent Verification (pre-sign gate)

Before signing any RGB **send**, the plugin independently verifies — outside the in-process `rgb-lib`, in a separate native trust core (`rgbverifycffi`, which does not link `rgb-lib`) — that the transaction it is about to sign commits to *exactly* the intended transfer:

- the RGB invoice decodes to the expected contract/asset ID, recipient blinded seal, amount, and network (decoded independently, never via `rgb-lib`);
- the operator-approved asset and amount match the invoice;
- the consignment commits to that single contract and transfer — no decoy, hidden, or co-located commitment;
- change returns to a wallet-owned script.

In addition, the plugin applies local BTC-level policy checks: it rejects BTC outputs to unknown scripts above the configured policy limit, restricts wallet/change outputs to locally-derived scripts, enforces fee and output-count limits, rejects non-zero-value `OP_RETURN` outputs, and routes all signing through the in-process wallet signer.

The gate is **fail-closed**: any verification failure aborts the transfer (`rgb-lib` `FailTransfers`) and nothing is signed. Because the verification baseline is the independent native decoder — never `rgb-lib`'s own decode — a compromised or malicious `rgb-lib` cannot construct a PSBT that passes the checks while diverting the transfer. By design a verifier bug can only cause a false *reject* (a legitimate send is blocked), never a false *accept* (funds diverted).

This closes the earlier trust boundary where RGB transfer construction was trusted entirely to `rgb-lib`.

A non-custodial mode with external signer support (offline PSBT signing, hardware wallet integration) is planned for a future release.

### DataProtection Key Backup

The mnemonic encryption keys are the `key-*.xml` files written **directly in your BTCPay data
directory** — for a default mainnet install, `~/.btcpayserver/Main/key-*.xml`. There is no
`DataProtection/` subdirectory; backing one up copies nothing. The directory is per network
(`Main`, `TestNet`, `Signet`, `RegTest`), and the ring that matters is the one belonging to the
data directory BTCPay actually ran with when the wallet was created — starting BTCPay with a
different `BTCPAY_NETWORK` makes it read a different ring, and the mnemonics stop decrypting
until the original ring is copied across.

If these files are lost (disk failure, container recreation without a persistent volume), every
encrypted mnemonic becomes permanently unrecoverable and the assets go with them. **Back up the
key ring together with the `RGB_Wallets` rows — either alone is useless.**

## Dependencies

- **RgbLib** v0.3.0-beta.30 - Native rgb-lib bindings
- **rgbverifycffi** - In-repo native trust core for pre-sign RGB intent verification (`native/rgb-verify`, does not link rgb-lib)
- **NBitcoin** - Bitcoin primitives and PSBT signing
- **Npgsql.EntityFrameworkCore.PostgreSQL** - Database persistence

## Troubleshooting

### "InsufficientAllocationSlots"
Create more colorable UTXOs: go to **RGB Wallet** → **UTXOs** → **Create UTXOs**.

### "InsufficientAssignments"
The wallet has the asset but no spendable balance on colorable UTXOs. Create new UTXOs and wait for confirmations.

### Invoice stays in "Processing"
The blockchain hasn't reached the required number of confirmations yet. Wait for more blocks to be mined, or reduce Min Confirmations in Settings.

### Invoice stays in "New" after payment
1. Check Electrum connection: **Settings** → **Test Connection**
2. Ensure blocks are being mined (relevant for regtest/testnet)
3. Click **Refresh** on the RGB Wallet dashboard to trigger a manual sync

### "RGB pre-sign verification library could not be loaded"

At startup the plugin checks that the pre-sign verification library (`rgbverifycffi`) loads and exports the symbols it needs. If it does not, the plugin logs one error naming your runtime identifier, the file name it expected, and every path it searched — then keeps running.

While that error is present, **all RGB asset sends are rejected**. This is by design: the send path refuses to sign without the independent verification described in [RGB Send Intent Verification (pre-sign gate)](#rgb-send-intent-verification-pre-sign-gate). **Receiving RGB assets and the rest of the plugin are unaffected.**

The message tells you which of four things happened:

| The error says | What it means |
|---|---|
| the library **is absent from this build** | no candidate file existed — a known packaging defect in the plugin distribution, not a problem with your server |
| a file **exists but could not be loaded** | the file is there but unusable: architecture mismatch, corruption, or incompatible system libraries (commonly a glibc floor newer than the host) |
| the library **loaded but is the wrong version** | it loaded and an expected symbol is missing — an ABI/version mismatch between the plugin and the native library |
| the **self-check failed** | the check itself raised an exception, which the message names |

In every case, please report it at https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/issues and quote the whole message.

### Plugin not loading
Check BTCPay logs for errors:
```bash
docker logs btcpay
```

If BTCPay auto-disabled the plugin after a crash, first fix the reported crash or missing dependency.
Then open **Server Settings → Plugins → Manage Plugins**, choose this plugin, and use BTCPay's
**Enable** action; restart BTCPay when the UI asks. `Plugins/commands` is only a transient command queue
that the host consumes, so deleting it does not re-enable a plugin already recorded in
`Plugins/disabled`.

### Connection errors
Verify your Electrum server is reachable. Check `RGB_ELECTRUM_URL` environment variable or `rgb.json` configuration.

## Platform Support

| Platform | Status |
|----------|--------|
| Linux x64 | Supported |
| Linux ARM64 | Not supported (`RgbVerifyCffi 0.11.1-rc.10-native.2` does supply a `linux-arm64` gate native, but `RgbLib 0.3.0-beta.30` carries no `linux-arm64` core native, so the pair is incomplete) |
| macOS ARM64 (Apple Silicon) | Not supported: both natives are now available from packages (`RgbLib` supplies the core, `RgbVerifyCffi` the gate), but this RID has not been reviewed or end-to-end verified, and the contract does not declare it |
| macOS x64 (Intel) | Not supported (neither native is available for this RID) |
| Windows | Not supported (`RgbVerifyCffi` ships no `win-x64` gate native; `RgbLib` does supply a `win-x64` core, and that extra core creates no support on its own) |

Support is an explicit end-to-end claim and requires both `rgbverifycffi` and `rgblibcffi` for a RID.
An extra native supplied by either package does not expand this table by itself. The reviewed matrix is
`scripts/plugin-artifact-contract.json`, which declares exactly one supported plugin RID: `linux-x64`.

### How the `linux-x64` gate native reaches you

The `linux-x64` gate native comes from the **`RgbVerifyCffi` package on nuget.org**, pinned by the
`<PackageReference Include="RgbVerifyCffi" Version="[0.11.1-rc.10-native.2]" />` item in
`BTCPayServer.Plugins.RgbUtexo.csproj` and hash-locked in `packages.lock.json`. The square brackets are
NuGet's exact-version syntax and are load-bearing: `scripts/verify-gate-native-package-hashes.sh`
rejects any range form, because a floating version lets the gate native change under a hash line
nobody rewrote. No binary is tracked in this repository.

BTCPay's hosted Plugin Builder runs only `dotnet restore` + `dotnet publish`, the package drops its
asset at `runtimes/linux-x64/native/librgbverifycffi.so`, and the `.btcpay` bundle is a flat ZIP of
that publish directory, so the package's native is in the artifact a merchant installs.

The core native (`librgblibcffi.so`) comes from the `RgbLib` package on nuget.org, so a `linux-x64`
publish carries the complete pair the pre-sign gate needs.

Three checks bind that binary, all runnable from a clone:

- **existence and layout** — `scripts/verify_plugin_artifact.py` against a publish directory or the
  `.btcpay`, using the declarative contract above;
- **package-origin integrity plus one byte-continuity hop** — `--provenance strict --package-cache <dir>`
  requires the shipped entry to be declared `assetType: native` of `RgbVerifyCffi` in the plugin's own
  `.deps.json` **and** byte-identical to the global-packages-cache copy of that pinned version. A native
  substituted anywhere in this repository fails this check instead of passing it;
- **loadability** — `scripts/verify-artifact-native-loads.sh <artifact>` extracts the archive entry and
  `dlopen`s it inside a Debian 12 container, resolving all five exports.

Alongside them, `native/rgb-verify/gate-native-source-manifest.txt` records per-file hashes of the crate
sources and the build recipe, and `scripts/verify-tracked-gate-native-freshness.sh` checks them against
the working tree. That is **recorded-input consistency** only: the manifest no longer records any binary,
and it cannot show that the published package was compiled from the inputs it records.

`native/rgb-verify/gate-native-package-manifest.txt` records the other end: the pinned package version and
the sha256 of every native `RgbVerifyCffi` delivers, one line per RID, and
`scripts/verify-gate-native-package-hashes.sh` checks them against the copies a restore placed in the
NuGet package cache. It also refuses a `PackageReference` that is not an exact single-version pin. That is
what keeps a version bump from shipping unreviewed native bytes: the hashes have to be rewritten in the
same diff, where a reviewer sees them.

Rebuild and re-stage the natives locally with `scripts/pack-rgbverify.sh --stage`, which builds each RID
in `rust:1-bookworm` (the glibc floor) or on the host and asserts the exact export set. Staged natives are
gitignored build artifacts used to pack a package; they are not what a clean publish ships.

**Known limitation.** Strict mode gives **package-origin integrity** — a known publisher at a pinned
version, hash-locked — plus **byte continuity** on the package-cache-to-artifact hop. It does **not**
establish **source-to-binary provenance**: nothing here checks how the published package's binary was
produced. Closing that needs a build attestation over the exact published nupkg, tied to the reviewed
source revision, and no workflow in this repository emits one. Note also that the Rust build is **not**
byte-reproducible — two builds from byte-identical sources produce the same size but different SHA-256
and different build IDs — so a committed expected-hash pin is not implementable either.

## Building from source

This repository uses NuGet lockfiles (`packages.lock.json`) for both the plugin and test projects to pin transitive dependencies at known-good versions.

- First-time clone / clean restore: `dotnet restore --use-lock-file`
- Standard build verification: `dotnet restore --locked-mode` — this fails the build if any resolved version drifts from the committed lockfile.
- After a BTCPay submodule update (or any change to the plugin's direct/transitive packages): `dotnet restore --force-evaluate` regenerates the lockfile to match the new graph. Commit the regenerated `packages.lock.json`.
- The lockfile pins each resolved version together with its `contentHash`, so a package whose bytes change under a fixed version fails restore. It does NOT verify NuGet author signatures or build provenance — those are deferred release-process controls.

### Packaging the gate native (`RgbVerifyCffi`)

The pre-sign verification library (`native/rgb-verify`, built by `native/rgb-verify/build-native.sh`) is packed as a native-only NuGet package, and the plugin consumes it through the `PackageReference` described under [Platform Support](#platform-support). For step-by-step publishing instructions — including who is permitted to push the package ID, the API-key scope required, and what to do after a version bump — see [`native/rgb-verify/PUBLISHING.md`](native/rgb-verify/PUBLISHING.md).

```bash
# stage every gate-package RID and pack (host RID natively, cross RIDs in containers)
scripts/pack-rgbverify.sh --require-all-rids --version <NEW-VERSION>

# pack only what is already staged (what CI's assemble job does)
scripts/pack-rgbverify.sh --pack-only --require-all-rids --version <NEW-VERSION>

# run the pack-pipeline checks: package layout, both RID guards, and the Debian load check
scripts/pack-rgbverify.sh --verify
```

`<NEW-VERSION>` must be a version that has never been published: the currently pinned
`0.11.1-rc.10-native.2` cannot be re-packed, because Rust builds are not byte-reproducible and a
restore that already holds that version fails with `NU1403`.

`--stage` and `--pack-only` are independent switches, not modes; passing neither does both. The
gate-package RID set (`linux-x64`, `linux-arm64`, `osx-arm64`) is declared in the packaging project's
`GateRid` items, and the canonical pack refuses to omit one. This is package coverage, not a claim that
the complete plugin supports all three RIDs; the plugin matrix currently claims only `linux-x64`.

The package lands in `local-nuget-feed/`, which is **not** a source in the committed `nuget.config` — a folder source that cannot exist in a fresh clone fails restore with `NU1301` for every consumer, and a local source ahead of nuget.org could shadow the published trust core. Supply it on the command line instead:

```bash
dotnet restore <project> \
  --source https://api.nuget.org/v3/index.json \
  --source "$PWD/local-nuget-feed" \
  --force-evaluate
```

The feed path must be **absolute**. Measured on this project graph: both `--source ./local-nuget-feed` and the bare relative form fail with `NU1101 Unable to find package RgbVerifyCffi` when run from the repo root, while the identical restore with an absolute path succeeds. Reproduced with a cold cache in both directions; the reason relative resolution misses the feed here was not established, so use an absolute path.

`--force-evaluate` is needed because Rust builds are not byte-reproducible: re-packing at a version already restored elsewhere otherwise fails with `NU1403`.

**glibc floor.** The canonical Linux natives are built in `rust:1-bookworm` (Debian 12), because a native linked against a newer glibc than the deployment target fails to `dlopen` there. `scripts/verify-native-loads-debian.sh` loads the packed `linux-x64` native inside a Debian 12 container and resolves all five exports, so a floor mistake surfaces at pack time rather than at a merchant's startup.

For releases, `.github/workflows/pack-native.yml` (manual dispatch) builds each RID on a runner of that architecture, checks the exports with a tool that can read that object format, and uploads the assembled `.nupkg` as an artifact. It deliberately neither tags a release nor publishes to nuget.org — see [`native/rgb-verify/PUBLISHING.md`](native/rgb-verify/PUBLISHING.md) for the publish step. A `linux-arm64` gate native does now exist and ships in the package; the platform table above still declines that RID because `RgbLib` carries no `linux-arm64` core native, so the pair is incomplete.

### Verifying a plugin artifact

One verifier checks both a Release publish directory and the final `.btcpay` ZIP against the same
declarative contract:

```bash
python3 scripts/verify_plugin_artifact.py publish-out \
  --provenance strict \
  --package-cache "${NUGET_PACKAGES:-$HOME/.nuget/packages}"

python3 scripts/verify_plugin_artifact.py BTCPayServer.Plugins.RgbUtexo.btcpay \
  --provenance strict \
  --package-cache "${NUGET_PACKAGES:-$HOME/.nuget/packages}"
```

`strict` requires the shipped gate native to be declared `assetType: native` of `RgbVerifyCffi` in the
plugin's own `.deps.json` and byte-identical to the global-packages-cache copy of the pinned version —
**package-origin integrity plus one byte-continuity hop**. It is what CI and the release workflow run,
and it applies to *every* gate native the artifact carries, so an extra undeclared one fails the run
rather than passing quietly. The weaker `pre-package` mode remains for inspecting a hand-staged tree; in
that mode the gate native is only checked for existence and non-emptiness, and the verifier says so on
its own output. Neither mode is a statement about how the package's binary was produced. A locally built
three-RID gate package can be checked without network access:

```bash
python3 scripts/verify_plugin_artifact.py \
  local-nuget-feed/RgbVerifyCffi.<VERSION>.nupkg \
  --gate-package --provenance strict
```

## License

MIT License - See LICENSE file

## Support

- GitHub Issues: [Create Issue](https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/issues)
- BTCPay Server Community: https://chat.btcpayserver.org
