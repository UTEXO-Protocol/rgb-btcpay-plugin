# RGB BTCPay Server Plugin

Accept RGB asset payments (tokens, stablecoins) in BTCPay Server.

[![BTCPay Server](https://img.shields.io/badge/BTCPay%20Server-Plugin-brightgreen)](https://btcpayserver.org)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blue)](https://dotnet.microsoft.com)

## Features

- Accept RGB20 token payments alongside Bitcoin
- Issue new RGB assets directly from BTCPay
- Automatic invoice settlement on payment confirmation
- Full UTXO management for RGB allocations
- Works with Thunderstack RGB infrastructure

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

### RGB Node URL

The plugin connects to an RGB Node backend. Configure via:

**Option 1: Environment Variable**
```bash
RGB_NODE_URL=https://rgb-node.thunderstack.org
```

**Option 2: Configuration File**

Create `rgb.json` in your BTCPay Server data directory:
```json
{
  "rgb_node_url": "https://rgb-node.thunderstack.org",
  "network": "mainnet"
}
```

### Network Defaults

| Network | Default RGB Node URL |
|---------|---------------------|
| Mainnet | https://rgb-node.thunderstack.org |
| Testnet | https://rgb-node.test.thunderstack.org |
| Regtest | http://127.0.0.1:8000 (local) |

### Testnet Integration Testing

To test with Bitcoin testnet and the Thunderstack RGB testnet infrastructure:

1. Set BTCPay Server network to `testnet`:
   ```bash
   BTCPAY_NETWORK=testnet
   ```

2. The plugin will automatically use `https://rgb-node.test.thunderstack.org`

3. Get testnet BTC from a faucet and fund your RGB wallet

## Quick Start

1. **Create Store** - If you don't have one already
2. **Setup RGB Wallet** - Go to Store → RGB Wallet → Setup
3. **Issue Asset** (Optional) - RGB Wallet → Issue New Asset
4. **Configure Payment** - RGB Wallet → Settings → Select asset to accept
5. **Enable Payments** - Click "Enable RGB Payments"

## Usage

### Accepting Payments

1. Create an invoice in BTCPay
2. Customer selects "RGB" payment method
3. Customer scans QR code / copies RGB invoice
4. Customer pays with RGB-compatible wallet
5. Invoice auto-settles on confirmation

### Managing UTXOs

RGB requires "colorable" UTXOs for asset operations:

1. Go to RGB Wallet → UTXOs
2. Click "Create UTXOs" if count is low
3. Wait for confirmation

### Issuing Assets

1. Go to RGB Wallet → Issue New Asset
2. Enter ticker, name, amount, precision
3. Click Issue

## Building from Source

### Prerequisites

- .NET 8.0 SDK
- BTCPay Server source (as submodule)

### Build

```bash
# Clone with submodules
git clone --recursive https://github.com/your-org/btcpay-rgb-plugin

# Bundle RGB SDK (required for PSBT signing)
cd BTCPayServer.Plugins.RGB/Scripts
./bundle-sdk.sh

# Build
cd ..
dotnet build
```

### SDK Bundling

The plugin uses Jering.Javascript.NodeJS to execute the RGB SDK for cryptographic operations.
Before deploying, bundle the SDK:

```bash
./Scripts/bundle-sdk.sh
```

This copies the compiled SDK into `Scripts/rgb-sdk/` which gets deployed with the plugin.

### Plugin Builder Deployment

This plugin is designed for the [BTCPay Plugin Builder](https://github.com/btcpayserver/btcpayserver-plugin-builder):

1. Fork this repository
2. Register at https://plugin-builder.btcpayserver.org
3. Add your repository
4. Plugin Builder will build and publish automatically

## Architecture

```
BTCPayServer.Plugins.RGB/
├── Controllers/          # MVC controllers
├── Data/                 # EF Core entities & migrations
├── Models/               # View models
├── PaymentHandler/       # BTCPay payment integration
├── Scripts/              # Jering.NodeJS wrapper for RGB SDK
├── Services/             # Core services
│   ├── RgbNodeClient.cs      # HTTP client for RGB Node API
│   ├── RgbSdkService.cs      # NodeJS interop for PSBT signing
│   ├── RGBWalletService.cs   # Wallet management
│   └── RGBInvoiceListener.cs # Payment detection
└── Views/                # Razor views
```

## Dependencies

- **Jering.Javascript.NodeJS** - NodeJS interop for RGB SDK operations
- **Npgsql.EntityFrameworkCore.PostgreSQL** - Database persistence
- **NBitcoin** - Bitcoin primitives

## Troubleshooting

### "InsufficientAllocationSlots"
Create more colorable UTXOs via RGB Wallet → UTXOs → Create UTXOs

### Invoice stays pending after payment
1. Check RGB Node is accessible (Settings → Test Connection)
2. Ensure blocks are being mined (regtest)
3. Click "Refresh" on RGB Wallet page

### Plugin not loading
Check BTCPay logs: `docker logs btcpay`

### Connection errors
Verify `RGB_NODE_URL` environment variable or `rgb.json` configuration

## License

MIT License - See LICENSE file

## Support

- GitHub Issues: [Create Issue](https://github.com/your-org/btcpay-rgb-plugin/issues)
- BTCPay Server Community: https://chat.btcpayserver.org

