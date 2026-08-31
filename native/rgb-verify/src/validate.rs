use std::collections::BTreeMap;
use std::str::FromStr;

use amplify::confinement::Confined;
use amplify::{ByteArray, Wrapper};
use serde::Serialize;

use rgbcore::bitcoin::{OutPoint, Transaction};
use rgbcore::dbc::{Method, Proof};
use rgbcore::seals::txout::TxPtr;
use rgbcore::validation::ValidationConfig;
use rgbcore::{Assign, ChainNet, ContractId, OpId, Opout, Transition, Txid};
use rgbstd::containers::{ConsignmentExt, FileContent, Transfer, WitnessBundle};
use rgbstd::contract::IssuerWrapper;
use rgbstd::indexers::AnyResolver;
use rgbstd::persistence::fs::FsBinStore;
use rgbstd::persistence::{MemIndex, MemStash, MemState, Stock};

use strict_types::TypeSystem;

use schemata::{
    CollectibleFungibleAsset, NonInflatableAsset, CFA_SCHEMA_ID, NIA_SCHEMA_ID, TS_TRANSFER,
};

use crate::inputs::{scan_inputs, ObservedInput};

#[derive(Serialize)]
pub(crate) struct Leg {
    #[serde(rename = "assignmentType")]
    assignment_type: u16,
    #[serde(rename = "sealKind")]
    seal_kind: String,
    #[serde(rename = "sealBytes")]
    seal_bytes: Option<String>,
    #[serde(rename = "witnessVout")]
    witness_vout: Option<u32>,
    outpoint: Option<String>,
    #[serde(rename = "derivationPath")]
    derivation_path: Option<String>,
    amount: u64,
}

impl Leg {
    pub(crate) fn concrete_outpoint(&self) -> Option<&str> {
        (self.seal_kind == "revealedConcreteOutpoint")
            .then(|| self.outpoint.as_deref())
            .flatten()
    }

    pub(crate) fn set_derivation_path(&mut self, path: String) {
        self.derivation_path = Some(path);
    }
}

#[derive(Serialize)]
struct ValidatedTransfer {
    #[serde(rename = "contractId")]
    contract_id: String,
    #[serde(rename = "chainNet")]
    chain_net: String,
    #[serde(rename = "witnessTxid")]
    witness_txid: String,
    prevouts: Vec<String>,
    legs: Vec<Leg>,
    #[serde(rename = "inputsAccounted")]
    inputs_accounted: bool,
    inputs: Vec<ObservedInput>,
}

pub(crate) fn validate(
    consignment_path: String,
    unsigned_txid: String,
    indexer_url: String,
    network: String,
    stock_dir: String,
) -> Result<String, String> {
    let consignment = Transfer::load_file(&consignment_path)
        .map_err(|e| format!("failed to load consignment: {e}"))?;

    let trusted_typesystem = trusted_types_for(consignment.schema_id())?;

    let chain_net =
        ChainNet::from_str(&network).map_err(|_| format!("unsupported chain net: {network}"))?;
    let txid = Txid::from_str(&unsigned_txid).map_err(|_| "invalid unsigned txid".to_string())?;

    let mut resolver = build_resolver(&indexer_url)?;
    resolver.add_consignment_txes(&terminal_only_consignment(&consignment, txid)?);
    let config = ValidationConfig {
        chain_net,
        trusted_typesystem,
        ..Default::default()
    };
    consignment
        .clone()
        .validate(&resolver, &config)
        .map_err(|e| format!("consignment validation failed: {e}"))?;

    let contract_id = consignment.contract_id();
    let (bundle, transition) = select_transfer_transition(&consignment, txid)?;
    let witness_tx = bundle
        .pub_witness
        .tx()
        .ok_or_else(|| "anchored bundle does not embed its witness transaction".to_string())?;
    verify_anchor(bundle, contract_id, witness_tx)?;

    let store = FsBinStore::new(std::path::PathBuf::from(&stock_dir))
        .map_err(|e| format!("failed to open stock dir: {e}"))?;
    let stock = Stock::<MemStash, MemState, MemIndex>::load(store, false)
        .map_err(|e| format!("failed to load stock: {e}"))?;
    let scan = scan_inputs(
        &stock,
        contract_id,
        transition,
        &input_map_of(bundle),
        &witness_prevouts(witness_tx),
    )?;

    let transfer = ValidatedTransfer {
        contract_id: contract_id.to_string(),
        chain_net: consignment.genesis.chain_net.prefix().to_string(),
        witness_txid: witness_tx.compute_txid().to_string(),
        prevouts: extract_prevouts(witness_tx),
        legs: extract_legs(transition)?,
        inputs_accounted: scan.inputs_accounted,
        inputs: scan.inputs,
    };

    serde_json::to_string(&transfer).map_err(|e| e.to_string())
}

pub(crate) fn build_resolver(indexer_url: &str) -> Result<AnyResolver, String> {
    if indexer_url.starts_with("http://") || indexer_url.starts_with("https://") {
        let authority = indexer_url
            .split_once("://")
            .map(|(_, rest)| rest)
            .unwrap_or("");
        if authority.is_empty() || authority.starts_with('/') {
            return Err(format!("malformed esplora indexer url: {indexer_url}"));
        }
        let builder = rgbstd::indexers::esplora_blocking::esplora_client::Builder::new(indexer_url);
        return AnyResolver::esplora_blocking(builder)
            .map_err(|e| format!("failed to build esplora resolver: {e}"));
    }
    AnyResolver::electrum_blocking(indexer_url, None)
        .map_err(|e| format!("failed to build electrum resolver: {e}"))
}

pub(crate) fn terminal_only_consignment(
    consignment: &Transfer,
    txid: Txid,
) -> Result<Transfer, String> {
    let terminal = select_anchored_bundle(consignment, txid)?.clone();
    let mut trimmed = consignment.clone();
    trimmed.bundles = Confined::try_from_iter([terminal])
        .map_err(|_| "failed to build terminal witness set".to_string())?;
    Ok(trimmed)
}

pub(crate) fn trusted_types_for(schema_id: rgbcore::SchemaId) -> Result<TypeSystem, String> {
    if schema_id == NIA_SCHEMA_ID {
        Ok(NonInflatableAsset::types())
    } else if schema_id == CFA_SCHEMA_ID {
        Ok(CollectibleFungibleAsset::types())
    } else {
        Err(format!(
            "schema {schema_id} is not one of the supported NIA/CFA schemas"
        ))
    }
}

pub(crate) fn select_anchored_bundle(
    consignment: &Transfer,
    txid: Txid,
) -> Result<&WitnessBundle, String> {
    let mut anchored = consignment
        .bundles
        .iter()
        .filter(|bundle| bundle.pub_witness.txid() == txid);
    let bundle = anchored
        .next()
        .ok_or_else(|| format!("no bundle commits to the signed txid {txid}"))?;
    if anchored.next().is_some() {
        return Err(format!("multiple bundles commit to the signed txid {txid}"));
    }
    Ok(bundle)
}

pub(crate) fn enforce_transition_rules(bundle: &WitnessBundle) -> Result<&Transition, String> {
    let transition_bundle = &bundle.bundle;
    if !transition_bundle
        .input_map_opids()
        .is_subset(&transition_bundle.known_transitions_opids())
    {
        return Err("input map references transitions absent from the bundle".to_string());
    }
    if transition_bundle.known_transitions.len() != 1 {
        return Err(format!(
            "expected exactly one transition, found {}",
            transition_bundle.known_transitions.len()
        ));
    }
    let transition = &transition_bundle
        .known_transitions
        .iter()
        .next()
        .unwrap()
        .transition;
    if transition.transition_type != TS_TRANSFER {
        return Err(format!(
            "transition type {} is not a transfer",
            transition.transition_type
        ));
    }
    Ok(transition)
}

fn select_transfer_transition(
    consignment: &Transfer,
    txid: Txid,
) -> Result<(&WitnessBundle, &Transition), String> {
    let bundle = select_anchored_bundle(consignment, txid)?;
    let transition = enforce_transition_rules(bundle)?;
    Ok((bundle, transition))
}

pub(crate) fn input_map_of(bundle: &WitnessBundle) -> BTreeMap<Opout, OpId> {
    bundle
        .bundle
        .input_map
        .iter()
        .map(|(opout, opid)| (*opout, *opid))
        .collect()
}

pub(crate) fn witness_prevouts(witness_tx: &Transaction) -> Vec<OutPoint> {
    witness_tx
        .input
        .iter()
        .map(|input| input.previous_output)
        .collect()
}

pub(crate) fn verify_anchor(
    bundle: &WitnessBundle,
    contract_id: ContractId,
    witness_tx: &Transaction,
) -> Result<(), String> {
    if bundle.anchor.dbc_proof.method() != Method::OpretFirst {
        return Err("anchor does not use an opret commitment".to_string());
    }
    bundle
        .anchor
        .verify(contract_id, bundle.bundle.bundle_id(), witness_tx)
        .map_err(|e| format!("anchor verification failed: {e}"))?;
    Ok(())
}

pub(crate) fn extract_prevouts(witness_tx: &Transaction) -> Vec<String> {
    witness_tx
        .input
        .iter()
        .map(|input| {
            format!(
                "{}:{}",
                input.previous_output.txid, input.previous_output.vout
            )
        })
        .collect()
}

pub(crate) fn extract_legs(transition: &Transition) -> Result<Vec<Leg>, String> {
    let mut legs = Vec::new();
    for (assignment_type, typed) in transition.assignments.iter() {
        if !typed.is_fungible() {
            return Err(format!(
                "assignment type {assignment_type} carries non-fungible state"
            ));
        }
        let assignment_type = assignment_type.to_inner();
        for assign in typed.as_fungible() {
            let leg = match assign {
                Assign::Revealed { seal, state } => match seal.txid {
                    TxPtr::WitnessTx => Leg {
                        assignment_type,
                        seal_kind: "revealedWitnessVout".to_string(),
                        seal_bytes: None,
                        witness_vout: Some(seal.vout.into_u32()),
                        outpoint: None,
                        derivation_path: None,
                        amount: state.as_u64(),
                    },
                    TxPtr::Txid(txid) => Leg {
                        assignment_type,
                        seal_kind: "revealedConcreteOutpoint".to_string(),
                        seal_bytes: None,
                        witness_vout: None,
                        outpoint: Some(format!("{txid}:{}", seal.vout.into_u32())),
                        derivation_path: None,
                        amount: state.as_u64(),
                    },
                },
                Assign::ConfidentialSeal { seal, state } => Leg {
                    assignment_type,
                    seal_kind: "confidentialSeal".to_string(),
                    seal_bytes: Some(hex::encode(seal.to_byte_array())),
                    witness_vout: None,
                    outpoint: None,
                    derivation_path: None,
                    amount: state.as_u64(),
                },
            };
            legs.push(leg);
        }
    }
    Ok(legs)
}

#[cfg(test)]
mod tests {
    use super::*;

    use rgbcore::bitcoin::key::UntweakedPublicKey;
    use rgbcore::dbc::tapret::{TapretPathProof, TapretProof};
    use rgbcore::validation::DbcProof;
    use rgbcore::{AssignmentType, OpId, Opout, TransitionType};
    use schemata::UniqueDigitalAsset;

    fn fixture() -> Transfer {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/consignment_out"
        );
        Transfer::load_file(path).unwrap()
    }

    fn terminal_txid(consignment: &Transfer) -> Txid {
        consignment
            .bundles
            .iter()
            .next_back()
            .unwrap()
            .pub_witness
            .txid()
    }

    fn terminal_bundle(consignment: &Transfer) -> WitnessBundle {
        let txid = terminal_txid(consignment);
        select_anchored_bundle(consignment, txid).unwrap().clone()
    }

    #[test]
    fn rejects_unsupported_schema() {
        let mut consignment = fixture();
        consignment.schema = UniqueDigitalAsset::schema();
        let err = trusted_types_for(consignment.schema_id()).unwrap_err();
        assert!(err.contains("supported NIA/CFA schemas"), "{err}");
    }

    #[test]
    fn rejects_multiple_bundles_same_txid() {
        let consignment = fixture();
        let txid = terminal_txid(&consignment);
        let terminal = select_anchored_bundle(&consignment, txid).unwrap().clone();
        let mut tampered = consignment.clone();
        tampered.bundles = Confined::try_from_iter([terminal.clone(), terminal]).unwrap();
        let err = select_anchored_bundle(&tampered, txid).unwrap_err();
        assert!(err.contains("multiple bundles commit"), "{err}");
    }

    #[test]
    fn rejects_input_map_referencing_unknown_transition() {
        let consignment = fixture();
        let mut bundle = terminal_bundle(&consignment);
        let bogus_opid = OpId::from([0x77u8; 32]);
        let bogus_opout = Opout::new(bogus_opid, AssignmentType::with(4000), 0);
        bundle
            .bundle
            .input_map
            .insert(bogus_opout, bogus_opid)
            .unwrap();
        let err = enforce_transition_rules(&bundle).unwrap_err();
        assert!(
            err.contains("input map references transitions absent"),
            "{err}"
        );
    }

    #[test]
    fn rejects_multiple_known_transitions() {
        let consignment = fixture();
        let mut bundle = terminal_bundle(&consignment);
        let extra = bundle
            .bundle
            .known_transitions
            .iter()
            .next()
            .unwrap()
            .clone();
        let mut transitions = bundle.bundle.known_transitions.to_unconfined();
        transitions.push(extra);
        bundle.bundle.known_transitions = Confined::from_checked(transitions);
        let err = enforce_transition_rules(&bundle).unwrap_err();
        assert!(err.contains("expected exactly one transition"), "{err}");
    }

    #[test]
    fn rejects_non_transfer_transition_type() {
        let consignment = fixture();
        let mut bundle = terminal_bundle(&consignment);
        let mut known = bundle
            .bundle
            .known_transitions
            .iter()
            .next()
            .unwrap()
            .clone();
        known.transition.transition_type = TransitionType::with(9999);
        bundle.bundle.known_transitions = Confined::from_checked(vec![known]);
        let err = enforce_transition_rules(&bundle).unwrap_err();
        assert!(err.contains("is not a transfer"), "{err}");
    }

    #[test]
    fn rejects_non_opret_anchor() {
        let consignment = fixture();
        let mut bundle = terminal_bundle(&consignment);
        let witness_tx = bundle.pub_witness.tx().unwrap().clone();
        let internal_pk = UntweakedPublicKey::from_str(
            "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798",
        )
        .unwrap();
        bundle.anchor.dbc_proof = DbcProof::Tapret(TapretProof {
            path_proof: TapretPathProof::root(0),
            internal_pk,
        });
        let err = verify_anchor(&bundle, consignment.contract_id(), &witness_tx).unwrap_err();
        assert!(err.contains("does not use an opret commitment"), "{err}");
    }

    #[test]
    fn rejects_anchor_with_wrong_contract_id() {
        let consignment = fixture();
        let txid = terminal_txid(&consignment);
        let bundle = select_anchored_bundle(&consignment, txid).unwrap();
        let witness_tx = bundle.pub_witness.tx().unwrap();
        let wrong = ContractId::from([0x99u8; 32]);
        let err = verify_anchor(bundle, wrong, witness_tx).unwrap_err();
        assert!(err.contains("anchor verification failed"), "{err}");
    }

    #[test]
    fn fixture_uses_nia_schema() {
        trusted_types_for(fixture().schema_id()).unwrap();
    }

    #[test]
    fn selects_bundle_by_witness_txid() {
        let consignment = fixture();
        let txid = terminal_txid(&consignment);
        let bundle = select_anchored_bundle(&consignment, txid).unwrap();
        assert_eq!(bundle.pub_witness.txid(), txid);
    }

    #[test]
    fn terminal_only_keeps_single_terminal_bundle() {
        let consignment = fixture();
        assert!(consignment.bundles.len() > 1);
        let txid = terminal_txid(&consignment);
        let trimmed = terminal_only_consignment(&consignment, txid).unwrap();
        assert_eq!(trimmed.bundles.len(), 1);
        assert_eq!(
            trimmed.bundles.iter().next().unwrap().pub_witness.txid(),
            txid
        );
    }

    #[test]
    fn rejects_txid_absent_from_consignment() {
        let consignment = fixture();
        let txid =
            Txid::from_str("0000000000000000000000000000000000000000000000000000000000000000")
                .unwrap();
        assert!(select_anchored_bundle(&consignment, txid).is_err());
    }

    #[test]
    fn anchored_bundle_has_single_transfer_transition() {
        let consignment = fixture();
        let txid = terminal_txid(&consignment);
        let bundle = select_anchored_bundle(&consignment, txid).unwrap();
        let transition = enforce_transition_rules(bundle).unwrap();
        assert_eq!(transition.transition_type, TS_TRANSFER);
    }

    #[test]
    fn anchor_verification_succeeds_offline() {
        let consignment = fixture();
        let txid = terminal_txid(&consignment);
        let bundle = select_anchored_bundle(&consignment, txid).unwrap();
        let witness_tx = bundle.pub_witness.tx().unwrap();
        verify_anchor(bundle, consignment.contract_id(), witness_tx).unwrap();
    }

    #[test]
    fn extracts_conserving_fungible_legs() {
        let consignment = fixture();
        let txid = terminal_txid(&consignment);
        let bundle = select_anchored_bundle(&consignment, txid).unwrap();
        let transition = enforce_transition_rules(bundle).unwrap();
        let legs = extract_legs(transition).unwrap();
        assert!(!legs.is_empty());
        for leg in &legs {
            assert!(matches!(
                leg.seal_kind.as_str(),
                "confidentialSeal" | "revealedWitnessVout" | "revealedConcreteOutpoint"
            ));
            if leg.seal_kind == "confidentialSeal" {
                assert!(leg.seal_bytes.is_some());
            }
        }
    }

    #[test]
    fn prevouts_are_outpoint_formatted() {
        let consignment = fixture();
        let txid = terminal_txid(&consignment);
        let bundle = select_anchored_bundle(&consignment, txid).unwrap();
        let witness_tx = bundle.pub_witness.tx().unwrap();
        let prevouts = extract_prevouts(witness_tx);
        assert!(!prevouts.is_empty());
        for prevout in prevouts {
            let (txid, vout) = prevout.split_once(':').unwrap();
            assert_eq!(txid.len(), 64);
            vout.parse::<u32>().unwrap();
        }
    }

    #[test]
    fn esplora_indexer_is_accepted() {
        assert!(build_resolver("https://blockstream.info/api").is_ok());
        assert!(build_resolver("http://127.0.0.1:3000").is_ok());
    }

    #[test]
    fn malformed_esplora_url_is_rejected() {
        assert!(build_resolver("https://").is_err());
        assert!(build_resolver("https:///no-host").is_err());
    }

    #[test]
    #[ignore = "network: queries the live utexo esplora indexer"]
    fn utexo_esplora_indexer_is_reachable() {
        let url = "https://esplora-api.utexo.com";
        assert!(build_resolver(url).is_ok());
        let client =
            rgbstd::indexers::esplora_blocking::esplora_client::Builder::new(url).build_blocking();
        let height = client
            .get_height()
            .expect("live utexo esplora query failed");
        assert!(height > 0);
    }
}
