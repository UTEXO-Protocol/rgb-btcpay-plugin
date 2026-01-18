const path = require('path');
const fs = require('fs');

const SDK_PATHS = [
    process.env.RGB_SDK_PATH,
    path.resolve(__dirname, 'rgb-sdk', 'index.cjs'),
    path.resolve(__dirname, '../rgb-sdk/dist/index.cjs'),
    path.resolve(__dirname, '../../rgb-sdk/dist/index.cjs')
].filter(Boolean);

let sdk = null;
let walletManager = null;
let walletFingerprint = null;

function ensureSdk() {
    if (sdk) return sdk;
    
    const allowedDirs = [
        __dirname,
        path.resolve(__dirname, '..'),
        path.resolve(__dirname, '../..'),
        path.resolve(__dirname, 'rgb-sdk')
    ].map(d => path.resolve(d));
    
    for (const sdkPath of SDK_PATHS) {
        if (!sdkPath) continue;
        
        const normalizedPath = path.resolve(sdkPath);
        
        const isAllowed = allowedDirs.some(dir => normalizedPath.startsWith(dir + path.sep) || normalizedPath === dir);
        if (!isAllowed) {
            console.warn('SDK path outside allowed directories:', normalizedPath);
            continue;
        }
        
        try {
            if (fs.existsSync(normalizedPath)) {
                sdk = require(normalizedPath);
                return sdk;
            }
        } catch (e) {
            console.warn('Failed to load SDK from', normalizedPath, e.message);
        }
    }
    throw new Error('RGB SDK not found');
}

module.exports.initWallet = function(callback, cfg) {
    try {
        if (!cfg || (!cfg.XpubVanilla && !cfg.xpubVanilla)) {
            return callback(null, { success: false, error: 'XpubVanilla is required' });
        }
        
        ensureSdk();
        
        const rgbNodeEndpoint = cfg.RgbNodeEndpoint || cfg.rgbNodeEndpoint;
        const network = cfg.Network || cfg.network;
        
        if (!rgbNodeEndpoint) {
            return callback(null, { success: false, error: 'RGB node endpoint is required' });
        }
        if (!network) {
            return callback(null, { success: false, error: 'Network is required' });
        }
        
        const walletConfig = {
            xpub_van: cfg.XpubVanilla || cfg.xpubVanilla,
            xpub_col: cfg.XpubColored || cfg.xpubColored,
            master_fingerprint: cfg.MasterFingerprint || cfg.masterFingerprint,
            rgb_node_endpoint: rgbNodeEndpoint,
            network: network
        };
        
        walletFingerprint = walletConfig.master_fingerprint;
        walletManager = new sdk.WalletManager(walletConfig);
        
        walletManager.registerWallet()
            .then(() => callback(null, { success: true }))
            .catch(e => callback(null, { success: false, error: e.message }));
    } catch (e) {
        callback(null, { success: false, error: e.message });
    }
};

module.exports.getStatus = function(callback) {
    try {
        const status = {
            SdkLoaded: sdk !== null,
            WalletInitialized: walletManager !== null,
            Connected: walletManager !== null
        };
        
        if (walletManager) {
            walletManager.getBtcBalance()
                .then(balance => {
                    status.BtcBalance = balance;
                    callback(null, status);
                })
                .catch(() => {
                    callback(null, status);
                });
        } else {
            callback(null, status);
        }
    } catch (e) {
        callback(null, { 
            SdkLoaded: false, 
            WalletInitialized: false,
            Connected: false,
            Error: e.message 
        });
    }
};

module.exports.signPsbt = function(callback, psbtBase64) {
    callback(null, { 
        success: false, 
        error: 'Remote signing disabled for security. Use local signing via RgbWalletSignerProvider.' 
    });
};

module.exports.createUtxos = function(callback, num, size) {
    if (!walletManager) {
        return callback(null, { success: false, error: 'Wallet not initialized' });
    }
    
    walletManager.createUtxos({ up_to: true, num: num || 5, size: size || 10000, fee_rate: 2 })
        .then(result => {
            callback(null, { success: true, UtxosCreated: result });
        })
        .catch(err => {
            if (err.message && err.message.includes('AllocationsAlreadyAvailable')) {
                callback(null, { success: true, UtxosCreated: 0 });
            } else {
                callback(null, { success: false, error: err.message });
            }
        });
};

module.exports.issueAssetNia = function(callback, ticker, name, amounts, precision) {
    if (!walletManager) {
        return callback(null, { success: false, error: 'Wallet not initialized' });
    }
    
    walletManager.issueAssetNia({ ticker, name, amounts, precision: precision || 0 })
        .then(result => {
            callback(null, { 
                success: true, 
                AssetId: result.asset?.asset_id 
            });
        })
        .catch(err => {
            callback(null, { success: false, error: err.message });
        });
};

module.exports.sendBtc = function(callback, address, amount, feeRate) {
    if (!walletManager) {
        return callback(null, { success: false, error: 'Wallet not initialized' });
    }
    
    walletManager.sendBtc({ address, amount, fee_rate: feeRate || 2 })
        .then(txid => {
            callback(null, { success: true, Txid: txid });
        })
        .catch(err => {
            callback(null, { success: false, error: err.message });
        });
};

module.exports.sendRgb = function(callback, invoice, amount, assetId) {
    if (!walletManager) {
        return callback(null, { success: false, error: 'Wallet not initialized' });
    }
    
    walletManager.send({ invoice, amount, asset_id: assetId })
        .then(result => {
            callback(null, { success: true, Txid: result.txid });
        })
        .catch(err => {
            callback(null, { success: false, error: err.message });
        });
};

module.exports.disposeWallet = function(callback) {
    if (walletManager) {
        try {
            walletManager.dispose();
        } catch (e) {
        }
        walletManager = null;
    }
    walletFingerprint = null;
    callback(null, { success: true, status: 'disposed' });
};
