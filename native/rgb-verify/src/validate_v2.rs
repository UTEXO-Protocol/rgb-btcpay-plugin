use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::str::FromStr;

use bdk_wallet::bitcoin::bip32::{ChildNumber, DerivationPath, Fingerprint, KeySource, Xpub};
use bdk_wallet::bitcoin::secp256k1::Secp256k1;
use bdk_wallet::bitcoin::{Network as BitcoinNetwork, OutPoint as BdkOutPoint};
use bdk_wallet::descriptor::Segwitv0;
use bdk_wallet::file_store::Store;
use bdk_wallet::keys::{DerivableKey, DescriptorKey, DescriptorKey::Public};
use bdk_wallet::{ChangeSet, KeychainKind, Wallet};
use serde::{Deserialize, Serialize};

use rgbcore::commit_verify::mpc::{Commitment, Message, ProtocolId};
use rgbcore::validation::ValidationConfig;
use rgbcore::{ChainNet, ContractId, Operation, TransitionBundle, Txid};
use rgbstd::containers::{ConsignmentExt, Fascia, FileContent, Transfer};
use rgbstd::persistence::fs::FsBinStore;
use rgbstd::persistence::{MemIndex, MemStash, MemState, Stock};
use schemata::TS_TRANSFER;

use crate::commitment::recompute_commitment;
use crate::inputs::{scan_inputs_exhaustive, CarryForwardProof, ObservedInput};
use crate::validate::{
    build_resolver, extract_legs, extract_prevouts, select_anchored_bundle,
    terminal_only_consignment, trusted_types_for, verify_anchor, witness_prevouts, Leg,
};

const BDK_MAGIC: &[u8] = b"bdk_db";
const PURPOSE: u32 = 86;
const BTC_COIN_MAINNET: u32 = 0;
const BTC_COIN_TESTNET: u32 = 1;
const RGB_COIN_MAINNET: u32 = 827_166;
const RGB_COIN_TESTNET: u32 = 827_167;

#[derive(Clone, Copy)]
enum WalletAccount {
    Colored,
    Vanilla,
}

impl WalletAccount {
    fn keychain(self) -> KeychainKind {
        match self {
            Self::Colored => KeychainKind::External,
            Self::Vanilla => KeychainKind::Internal,
        }
    }

    fn coin(self, network: BitcoinNetwork) -> u32 {
        match (self, network == BitcoinNetwork::Bitcoin) {
            (Self::Colored, true) => RGB_COIN_MAINNET,
            (Self::Colored, false) => RGB_COIN_TESTNET,
            (Self::Vanilla, true) => BTC_COIN_MAINNET,
            (Self::Vanilla, false) => BTC_COIN_TESTNET,
        }
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub(crate) struct ValidateV2Request {
    consignment_path: String,
    fascia_path: String,
    unsigned_txid: String,
    opret_commitment_bytes: String,
    entropy: u64,
    indexer_url: String,
    network: String,
    stock_dir: String,
    bdk_store_path: String,
    account_xpub_vanilla: String,
    account_xpub_colored: String,
    master_fingerprint: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ValidatedTransferV2 {
    validation_version: u16,
    contract_id: String,
    chain_net: String,
    witness_txid: String,
    prevouts: Vec<String>,
    legs: Vec<Leg>,
    inputs_accounted: bool,
    inputs: Vec<ObservedInput>,
    commitment_matches: bool,
    witness_id_matches: bool,
    committed_contract_ids: Vec<String>,
    verified_contract_ids: Vec<String>,
    main_transition_id: String,
    verified_transition_ids: Vec<String>,
    carry_forwards: Vec<CarryForwardProof>,
}

pub(crate) fn validate_v2(request_json: String) -> Result<String, String> {
    let request: ValidateV2Request = serde_json::from_str(&request_json)
        .map_err(|e| format!("invalid validate_v2 request: {e}"))?;
    if request.consignment_path.is_empty()
        || request.fascia_path.is_empty()
        || request.stock_dir.is_empty()
        || request.bdk_store_path.is_empty()
    {
        return Err("validate_v2 request contains an empty required path".to_string());
    }

    let chain_net = ChainNet::from_str(&request.network)
        .map_err(|_| format!("unsupported chain net: {}", request.network))?;
    let bitcoin_network = bitcoin_network(chain_net)?;
    let txid =
        Txid::from_str(&request.unsigned_txid).map_err(|_| "invalid unsigned txid".to_string())?;

    let intended = Transfer::load_file(&request.consignment_path)
        .map_err(|e| format!("failed to load intended consignment: {e}"))?;
    let intended_types = trusted_types_for(intended.schema_id())?;
    let intended_contract = intended.contract_id();
    let intended_bundle = select_anchored_bundle(&intended, txid)?;
    if intended_bundle.bundle.known_transitions.len() != 1 {
        return Err(format!(
            "intended consignment must disclose exactly one main transition, found {}",
            intended_bundle.bundle.known_transitions.len()
        ));
    }
    let main = &intended_bundle
        .bundle
        .known_transitions
        .iter()
        .next()
        .expect("checked non-empty")
        .transition;
    if main.transition_type != TS_TRANSFER {
        return Err(format!(
            "intended transition type {} is not a transfer",
            main.transition_type
        ));
    }
    let main_transition_id = main.id();
    let intended_witness = intended_bundle
        .pub_witness
        .tx()
        .ok_or_else(|| "intended bundle does not embed its witness transaction".to_string())?;
    verify_anchor(intended_bundle, intended_contract, intended_witness)?;

    let mut intended_resolver = build_resolver(&request.indexer_url)?;
    intended_resolver.add_consignment_txes(&terminal_only_consignment(&intended, txid)?);
    intended
        .clone()
        .validate(
            &intended_resolver,
            &ValidationConfig {
                chain_net,
                trusted_typesystem: intended_types,
                ..Default::default()
            },
        )
        .map_err(|e| format!("intended consignment validation failed: {e}"))?;

    let fascia_json = fs::read_to_string(&request.fascia_path)
        .map_err(|e| format!("failed to read fascia: {e}"))?;
    let fascia: Fascia =
        serde_json::from_str(&fascia_json).map_err(|e| format!("failed to parse fascia: {e}"))?;
    if fascia.witness_id() != txid {
        return Err("fascia witness id does not match the unsigned txid".to_string());
    }
    let fascia_witness = fascia
        .seal_witness()
        .public
        .tx()
        .ok_or_else(|| "fascia does not embed its witness transaction".to_string())?;
    if fascia_witness != intended_witness {
        return Err(
            "fascia and intended consignment embed different witness transactions".to_string(),
        );
    }

    let expected_bytes = hex::decode(request.opret_commitment_bytes.trim())
        .map_err(|_| "opret commitment is not valid hex".to_string())?;
    let expected = Commitment::copy_from_slice(&expected_bytes)
        .map_err(|_| "opret commitment must be 32 bytes".to_string())?;
    let mut messages = BTreeMap::new();
    let mut bundles = BTreeMap::<ContractId, TransitionBundle>::new();
    for (contract_id, bundle) in fascia.bundles() {
        if bundles.insert(*contract_id, bundle.clone()).is_some() {
            return Err(format!("fascia contains duplicate contract {contract_id}"));
        }
        messages.insert(
            ProtocolId::from(*contract_id),
            Message::from(bundle.bundle_id()),
        );
    }
    if recompute_commitment(&messages, request.entropy)? != expected {
        return Err("opret commitment does not commit the complete fascia".to_string());
    }

    let fascia_intended = bundles
        .get(&intended_contract)
        .ok_or_else(|| "fascia does not contain the intended contract bundle".to_string())?;
    if fascia_intended.bundle_id() != intended_bundle.bundle.bundle_id() {
        return Err("intended consignment bundle does not match the fascia bundle".to_string());
    }
    if !fascia_intended
        .known_transitions
        .iter()
        .any(|known| known.opid == main_transition_id && known.transition == *main)
    {
        return Err("fascia does not disclose the exact intended transition".to_string());
    }

    let store = FsBinStore::new(std::path::PathBuf::from(&request.stock_dir))
        .map_err(|e| format!("failed to open stock dir: {e}"))?;
    let stock = Stock::<MemStash, MemState, MemIndex>::load(store, false)
        .map_err(|e| format!("failed to load stock: {e}"))?;
    let intended_info = stock
        .contract_info(intended_contract)
        .map_err(|e| format!("failed to load intended Stock contract: {e}"))?;
    if intended_info.schema_id != intended.schema_id() {
        return Err(
            "intended consignment schema differs from the Stock contract schema".to_string(),
        );
    }

    fully_validate_fascia_contracts(&stock, &fascia, &bundles, chain_net, &request.indexer_url)?;

    let mut scan = scan_inputs_exhaustive(
        &stock,
        intended_contract,
        main_transition_id,
        &bundles,
        &witness_prevouts(intended_witness),
    )?;
    let mut legs = extract_legs(main)?;
    attach_concrete_derivations(
        &mut scan.carry_forwards,
        &mut legs,
        &request.bdk_store_path,
        &request.account_xpub_vanilla,
        &request.account_xpub_colored,
        &request.master_fingerprint,
        bitcoin_network,
    )?;

    let mut committed_contract_ids = bundles.keys().map(ToString::to_string).collect::<Vec<_>>();
    committed_contract_ids.sort();
    if committed_contract_ids != scan.verified_contract_ids {
        return Err("committed and exhaustively verified contract sets differ".to_string());
    }

    let response = ValidatedTransferV2 {
        validation_version: 2,
        contract_id: intended_contract.to_string(),
        chain_net: intended.genesis.chain_net.prefix().to_string(),
        witness_txid: intended_witness.compute_txid().to_string(),
        prevouts: extract_prevouts(intended_witness),
        legs,
        inputs_accounted: true,
        inputs: scan.inputs,
        commitment_matches: true,
        witness_id_matches: true,
        committed_contract_ids,
        verified_contract_ids: scan.verified_contract_ids,
        main_transition_id: main_transition_id.to_string(),
        verified_transition_ids: scan.verified_transition_ids,
        carry_forwards: scan.carry_forwards,
    };
    serde_json::to_string(&response).map_err(|e| e.to_string())
}

fn fully_validate_fascia_contracts(
    stock: &Stock<MemStash, MemState, MemIndex>,
    fascia: &Fascia,
    bundles: &BTreeMap<ContractId, TransitionBundle>,
    chain_net: ChainNet,
    indexer_url: &str,
) -> Result<(), String> {
    for (contract_id, bundle) in bundles {
        let info = stock
            .contract_info(*contract_id)
            .map_err(|e| format!("failed to load fascia contract {contract_id}: {e}"))?;
        let trusted_typesystem = trusted_types_for(info.schema_id)?;
        let opids = bundle
            .known_transitions
            .iter()
            .map(|known| known.opid)
            .collect::<BTreeSet<_>>();
        if opids.is_empty() {
            return Err(format!(
                "fascia contract {contract_id} discloses no transitions"
            ));
        }
        let transfer = stock
            .transfer_from_fascia(*contract_id, [], [], opids, fascia)
            .map_err(|e| format!("failed to reconstruct fascia contract {contract_id}: {e}"))?;
        if transfer.schema_id() != info.schema_id {
            return Err(format!(
                "fascia contract {contract_id} reconstructed with a mismatched schema"
            ));
        }
        let mut resolver = build_resolver(indexer_url)?;
        resolver.add_consignment_txes(&transfer);
        transfer
            .validate(
                &resolver,
                &ValidationConfig {
                    chain_net,
                    trusted_typesystem,
                    ..Default::default()
                },
            )
            .map_err(|e| format!("fascia contract {contract_id} validation failed: {e}"))?;
    }
    Ok(())
}

fn attach_concrete_derivations(
    proofs: &mut [CarryForwardProof],
    legs: &mut [Leg],
    bdk_store_path: &str,
    xpub_vanilla: &str,
    xpub_colored: &str,
    fingerprint: &str,
    network: BitcoinNetwork,
) -> Result<(), String> {
    if proofs
        .iter()
        .all(|proof| proof.successor_outpoint.is_none())
        && legs.iter().all(|leg| leg.concrete_outpoint().is_none())
    {
        return Ok(());
    }
    let colored =
        descriptor_from_account_xpub(xpub_colored, fingerprint, network, WalletAccount::Colored)?;
    let vanilla =
        descriptor_from_account_xpub(xpub_vanilla, fingerprint, network, WalletAccount::Vanilla)?;
    let (_, changeset) = Store::<ChangeSet>::load(BDK_MAGIC, bdk_store_path)
        .map_err(|e| format!("failed to load BDK snapshot: {e}"))?;
    let changeset = changeset.ok_or_else(|| "BDK snapshot contains no wallet state".to_string())?;
    let wallet = Wallet::load()
        .descriptor(WalletAccount::Colored.keychain(), Some(colored))
        .descriptor(WalletAccount::Vanilla.keychain(), Some(vanilla))
        .check_network(network)
        .load_wallet_no_persist(changeset)
        .map_err(|e| format!("failed to authenticate BDK snapshot: {e}"))?
        .ok_or_else(|| "BDK snapshot does not contain a wallet".to_string())?;

    let coin = if network == BitcoinNetwork::Bitcoin {
        RGB_COIN_MAINNET
    } else {
        RGB_COIN_TESTNET
    };
    for proof in proofs {
        let Some(outpoint) = &proof.successor_outpoint else {
            continue;
        };
        let parsed = BdkOutPoint::from_str(outpoint)
            .map_err(|_| format!("carry successor outpoint {outpoint} is malformed"))?;
        let local = wallet.get_utxo(parsed).ok_or_else(|| {
            format!("concrete carry successor {outpoint} is absent from the BDK snapshot")
        })?;
        if local.keychain != KeychainKind::External || local.is_spent {
            return Err(format!(
                "concrete carry successor {outpoint} is not an unspent RGB-colored BDK output"
            ));
        }
        proof.derivation_path = Some(format!(
            "m/{PURPOSE}'/{coin}'/0'/0/{}",
            local.derivation_index
        ));
    }

    for leg in legs {
        let Some(outpoint) = leg.concrete_outpoint().map(ToOwned::to_owned) else {
            continue;
        };
        let parsed = BdkOutPoint::from_str(&outpoint)
            .map_err(|_| format!("main change outpoint {outpoint} is malformed"))?;
        let local = wallet.get_utxo(parsed).ok_or_else(|| {
            format!("main concrete change {outpoint} is absent from the BDK snapshot")
        })?;
        if local.keychain != KeychainKind::External || local.is_spent {
            return Err(format!(
                "main concrete change {outpoint} is not an unspent RGB-colored BDK output"
            ));
        }
        leg.set_derivation_path(format!(
            "m/{PURPOSE}'/{coin}'/0'/0/{}",
            local.derivation_index
        ));
    }
    Ok(())
}

fn descriptor_from_account_xpub(
    xpub: &str,
    fingerprint: &str,
    network: BitcoinNetwork,
    account: WalletAccount,
) -> Result<String, String> {
    let account_xpub = Xpub::from_str(xpub).map_err(|_| "invalid account xpub".to_string())?;
    let master_fingerprint =
        Fingerprint::from_str(fingerprint).map_err(|_| "invalid master fingerprint".to_string())?;
    let coin = account.coin(network);
    // BDK's External/Internal keychain slot selects between rgb-lib's colored and vanilla
    // descriptors. It is not the BIP32 branch within either descriptor: rgb-lib beta.30 persists
    // both watch-only account descriptors at branch /0/*.
    descriptor_from_parsed_account_xpub(account_xpub, master_fingerprint, coin, 0)
}

fn descriptor_from_parsed_account_xpub(
    account_xpub: Xpub,
    master_fingerprint: Fingerprint,
    coin: u32,
    derivation_branch: u32,
) -> Result<String, String> {
    let branch = ChildNumber::from_normal_idx(derivation_branch)
        .map_err(|_| "invalid wallet derivation branch".to_string())?;
    let derived = account_xpub
        .derive_pub(&Secp256k1::new(), &DerivationPath::from(vec![branch]))
        .map_err(|e| format!("failed to derive account xpub: {e}"))?;
    let origin_path = DerivationPath::from(vec![
        ChildNumber::from_hardened_idx(PURPOSE).expect("valid purpose"),
        ChildNumber::from_hardened_idx(coin).expect("valid coin"),
        ChildNumber::from_hardened_idx(0).expect("valid account"),
        branch,
    ]);
    let origin: KeySource = (master_fingerprint, origin_path);
    let descriptor_key: DescriptorKey<Segwitv0> = derived
        .into_descriptor_key(Some(origin), DerivationPath::default())
        .map_err(|e| format!("failed to construct descriptor key: {e}"))?;
    let Public(key, _, _) = descriptor_key else {
        return Err("public xpub unexpectedly produced a secret descriptor".to_string());
    };
    Ok(format!("tr({key})"))
}

fn bitcoin_network(chain_net: ChainNet) -> Result<BitcoinNetwork, String> {
    match chain_net {
        ChainNet::BitcoinMainnet => Ok(BitcoinNetwork::Bitcoin),
        ChainNet::BitcoinTestnet3 => Ok(BitcoinNetwork::Testnet),
        ChainNet::BitcoinTestnet4 => Ok(BitcoinNetwork::Testnet4),
        ChainNet::BitcoinSignet | ChainNet::BitcoinSignetCustom => Ok(BitcoinNetwork::Signet),
        ChainNet::BitcoinRegtest => Ok(BitcoinNetwork::Regtest),
        ChainNet::LiquidMainnet | ChainNet::LiquidTestnet => {
            Err("Liquid networks are not supported by the plugin wallet".to_string())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use bdk_wallet::bitcoin::bip32::Xpriv;

    fn hardened(index: u32) -> ChildNumber {
        ChildNumber::from_hardened_idx(index).expect("test derivation index is valid")
    }

    fn account_xpub(master: &Xpriv, coin: u32) -> Xpub {
        let secp = Secp256k1::new();
        let path = DerivationPath::from(vec![hardened(PURPOSE), hardened(coin), hardened(0)]);
        let account = master
            .derive_priv(&secp, &path)
            .expect("test account derivation succeeds");
        Xpub::from_priv(&secp, &account)
    }

    #[test]
    fn generated_bdk_append_log_authenticates_rgb_lib_keychain_layout() {
        // This is a generated BDK persistence fixture. It exercises the exact descriptor and
        // append-log authentication path used by validate_v2, but is not an rgb-lib E2E fixture.
        let network = BitcoinNetwork::Regtest;
        let secp = Secp256k1::new();
        let master = Xpriv::new_master(network, &[0x42; 32]).expect("valid test seed");
        let fingerprint = master.fingerprint(&secp).to_string();
        let colored_xpub = account_xpub(&master, RGB_COIN_TESTNET).to_string();
        let vanilla_xpub = account_xpub(&master, BTC_COIN_TESTNET).to_string();

        let colored = descriptor_from_account_xpub(
            &colored_xpub,
            &fingerprint,
            network,
            WalletAccount::Colored,
        )
        .expect("colored descriptor");
        let vanilla = descriptor_from_account_xpub(
            &vanilla_xpub,
            &fingerprint,
            network,
            WalletAccount::Vanilla,
        )
        .expect("vanilla descriptor");

        assert_eq!(WalletAccount::Colored.keychain(), KeychainKind::External);
        assert_eq!(WalletAccount::Vanilla.keychain(), KeychainKind::Internal);
        assert!(colored.starts_with(&format!(
            "tr([{fingerprint}/{PURPOSE}'/{RGB_COIN_TESTNET}'/0'/0]"
        )));
        assert!(vanilla.starts_with(&format!(
            "tr([{fingerprint}/{PURPOSE}'/{BTC_COIN_TESTNET}'/0'/0]"
        )));
        assert!(colored.ends_with("/*)"));
        assert!(vanilla.ends_with("/*)"));

        let temp_dir = tempfile::tempdir().expect("temporary fixture directory");
        let store_path = temp_dir.path().join("bdk_db_watch_only");
        let mut store = Store::<ChangeSet>::create(BDK_MAGIC, &store_path)
            .expect("create generated BDK append log");
        let mut persisted = Wallet::create(colored.clone(), vanilla.clone())
            .network(network)
            .create_wallet(&mut store)
            .expect("create generated wallet");
        persisted.reveal_next_address(KeychainKind::External);
        persisted.reveal_next_address(KeychainKind::Internal);
        assert!(persisted
            .persist(&mut store)
            .expect("persist generated wallet"));
        drop(persisted);
        drop(store);

        let (_, changeset) =
            Store::<ChangeSet>::load(BDK_MAGIC, &store_path).expect("reload generated append log");
        let changeset = changeset.expect("generated wallet changeset");
        let loaded = Wallet::load()
            .descriptor(KeychainKind::External, Some(colored.clone()))
            .descriptor(KeychainKind::Internal, Some(vanilla.clone()))
            .check_network(network)
            .load_wallet_no_persist(changeset.clone())
            .expect("descriptors authenticate the generated append log")
            .expect("generated wallet exists");
        assert_eq!(
            loaded
                .public_descriptor(KeychainKind::External)
                .to_string()
                .split('#')
                .next(),
            Some(colored.as_str())
        );
        assert_eq!(
            loaded
                .public_descriptor(KeychainKind::Internal)
                .to_string()
                .split('#')
                .next(),
            Some(vanilla.as_str())
        );

        let wrong_branch_one_vanilla = descriptor_from_parsed_account_xpub(
            Xpub::from_str(&vanilla_xpub).expect("test vanilla xpub"),
            Fingerprint::from_str(&fingerprint).expect("test fingerprint"),
            BTC_COIN_TESTNET,
            1,
        )
        .expect("wrong branch-one descriptor");
        let mismatch = Wallet::load()
            .descriptor(KeychainKind::External, Some(colored))
            .descriptor(KeychainKind::Internal, Some(wrong_branch_one_vanilla))
            .check_network(network)
            .load_wallet_no_persist(changeset);
        assert!(
            mismatch.is_err(),
            "a vanilla /1 descriptor must fail production /0 BDK snapshot authentication"
        );
    }

    #[test]
    fn mainnet_descriptors_keep_exact_coins_and_keychain_branches() {
        let network = BitcoinNetwork::Bitcoin;
        let secp = Secp256k1::new();
        let master = Xpriv::new_master(network, &[0x24; 32]).expect("valid test seed");
        let fingerprint = master.fingerprint(&secp).to_string();
        let colored = descriptor_from_account_xpub(
            &account_xpub(&master, RGB_COIN_MAINNET).to_string(),
            &fingerprint,
            network,
            WalletAccount::Colored,
        )
        .expect("colored descriptor");
        let vanilla = descriptor_from_account_xpub(
            &account_xpub(&master, BTC_COIN_MAINNET).to_string(),
            &fingerprint,
            network,
            WalletAccount::Vanilla,
        )
        .expect("vanilla descriptor");

        assert!(colored.starts_with(&format!(
            "tr([{fingerprint}/{PURPOSE}'/{RGB_COIN_MAINNET}'/0'/0]"
        )));
        assert!(vanilla.starts_with(&format!(
            "tr([{fingerprint}/{PURPOSE}'/{BTC_COIN_MAINNET}'/0'/0]"
        )));
    }
}
