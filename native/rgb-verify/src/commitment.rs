use std::collections::BTreeMap;
use std::fs;
use std::str::FromStr;

use amplify::confinement::MediumOrdMap;
use serde::Serialize;

use rgbcore::commit_verify::mpc::{Commitment, MerkleTree, Message, MultiSource, ProtocolId};
use rgbcore::commit_verify::{CommitId, TryCommitVerify};
use rgbcore::Txid;
use rgbstd::containers::Fascia;

#[derive(Serialize)]
struct CommitmentCheck {
    matches: bool,
    #[serde(rename = "witnessIdMatches")]
    witness_id_matches: bool,
    #[serde(rename = "committedContractIds")]
    committed_contract_ids: Vec<String>,
}

pub(crate) fn commitment_check(
    fascia_path: String,
    unsigned_txid: String,
    opret_commitment_bytes: String,
    entropy: u64,
) -> Result<String, String> {
    let fascia_str =
        fs::read_to_string(&fascia_path).map_err(|e| format!("failed to read fascia: {e}"))?;
    let fascia: Fascia =
        serde_json::from_str(&fascia_str).map_err(|e| format!("failed to parse fascia: {e}"))?;

    let signed_txid =
        Txid::from_str(&unsigned_txid).map_err(|_| "invalid unsigned txid".to_string())?;
    let witness_id_matches = fascia.witness_id() == signed_txid;

    let expected_bytes = hex::decode(opret_commitment_bytes.trim())
        .map_err(|_| "opret commitment is not valid hex".to_string())?;
    let expected = Commitment::copy_from_slice(&expected_bytes)
        .map_err(|_| "opret commitment must be 32 bytes".to_string())?;

    let mut messages = BTreeMap::new();
    let mut committed_contract_ids = Vec::new();
    for (contract_id, bundle) in fascia.into_bundles() {
        messages.insert(
            ProtocolId::from(contract_id),
            Message::from(bundle.bundle_id()),
        );
        committed_contract_ids.push(contract_id.to_string());
    }
    committed_contract_ids.sort();
    committed_contract_ids.dedup();

    let commitment = recompute_commitment(&messages, entropy)?;

    let check = CommitmentCheck {
        matches: commitment == expected,
        witness_id_matches,
        committed_contract_ids,
    };

    serde_json::to_string(&check).map_err(|e| e.to_string())
}

pub(crate) fn recompute_commitment(
    messages: &BTreeMap<ProtocolId, Message>,
    entropy: u64,
) -> Result<Commitment, String> {
    let mut source = MultiSource::with_static_entropy(entropy);
    source.messages = MediumOrdMap::from_checked(messages.clone());
    let merkle_tree =
        MerkleTree::try_commit(&source).map_err(|e| format!("mpc commitment failed: {e}"))?;
    Ok(merkle_tree.commit_id())
}

#[cfg(test)]
mod tests {
    use super::*;

    use amplify::confinement::NonEmptyOrdMap;
    use amplify::ByteArray;
    use rgbcore::commit_verify::mpc::MerkleBlock;
    use rgbcore::ContractId;
    use rgbstd::containers::{ConsignmentExt, FileContent, SealWitness, Transfer};

    fn real_ids() -> (ProtocolId, Message) {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/consignment_out"
        );
        let consignment = Transfer::load_file(path).unwrap();
        let contract_id = consignment.contract_id();
        let bundle_id = consignment
            .bundles
            .iter()
            .next_back()
            .unwrap()
            .bundle
            .bundle_id();
        (ProtocolId::from(contract_id), Message::from(bundle_id))
    }

    fn single_message() -> BTreeMap<ProtocolId, Message> {
        let (protocol_id, message) = real_ids();
        let mut messages = BTreeMap::new();
        messages.insert(protocol_id, message);
        messages
    }

    #[test]
    fn recompute_is_deterministic() {
        let messages = single_message();
        let a = recompute_commitment(&messages, 42).unwrap();
        let b = recompute_commitment(&messages, 42).unwrap();
        assert_eq!(a, b);
    }

    #[test]
    fn recompute_varies_with_entropy() {
        let messages = single_message();
        let a = recompute_commitment(&messages, 1).unwrap();
        let b = recompute_commitment(&messages, 2).unwrap();
        assert_ne!(a, b);
    }

    #[test]
    fn recompute_varies_with_committed_message() {
        let (protocol_id, message) = real_ids();
        let mut base = BTreeMap::new();
        base.insert(protocol_id, message);
        let mut tampered = BTreeMap::new();
        tampered.insert(protocol_id, Message::from([0xABu8; 32]));
        assert_ne!(
            recompute_commitment(&base, 7).unwrap(),
            recompute_commitment(&tampered, 7).unwrap()
        );
    }

    #[test]
    fn commitment_bytes_roundtrip_matches() {
        let messages = single_message();
        let commitment = recompute_commitment(&messages, 99).unwrap();
        let recovered = Commitment::copy_from_slice(&commitment.to_byte_array()).unwrap();
        assert_eq!(commitment, recovered);
    }

    #[test]
    fn cospend_changes_commitment() {
        let (protocol_id, message) = real_ids();
        let mut single = BTreeMap::new();
        single.insert(protocol_id, message);

        let mut cospend = single.clone();
        cospend.insert(ProtocolId::from([0x11u8; 32]), Message::from([0x22u8; 32]));
        assert_ne!(
            recompute_commitment(&single, 5).unwrap(),
            recompute_commitment(&cospend, 5).unwrap()
        );
    }

    const FASCIA_ENTROPY: u64 = 0x0102_0304_0506_0708;

    struct FasciaFixture {
        path: String,
        witness_txid: String,
        opret_hex: String,
        contract_id: String,
    }

    impl Drop for FasciaFixture {
        fn drop(&mut self) {
            let _ = fs::remove_file(&self.path);
        }
    }

    fn write_fascia_fixture(name: &str) -> FasciaFixture {
        let consignment_path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/consignment_out"
        );
        let consignment = Transfer::load_file(consignment_path).unwrap();
        let contract_id = consignment.contract_id();
        let witness_bundle = consignment.bundles.iter().next_back().unwrap();
        let bundle = witness_bundle.bundle.clone();

        let mut messages = BTreeMap::new();
        messages.insert(
            ProtocolId::from(contract_id),
            Message::from(bundle.bundle_id()),
        );
        let commitment = recompute_commitment(&messages, FASCIA_ENTROPY).unwrap();

        let mut source = MultiSource::with_static_entropy(FASCIA_ENTROPY);
        source.messages = MediumOrdMap::from_checked(messages);
        let tree = MerkleTree::try_commit(&source).unwrap();

        let seal_witness = SealWitness::new(
            witness_bundle.pub_witness.clone(),
            MerkleBlock::from(&tree),
            witness_bundle.anchor.dbc_proof.clone(),
        );
        let bundles = NonEmptyOrdMap::with_key_value(contract_id, bundle);
        let fascia = Fascia::new(seal_witness, bundles);
        let witness_txid = fascia.witness_id().to_string();

        let path = std::env::temp_dir()
            .join(format!(
                "rgbverify_fascia_{}_{name}.json",
                std::process::id()
            ))
            .to_string_lossy()
            .into_owned();
        fs::write(&path, serde_json::to_string(&fascia).unwrap()).unwrap();

        FasciaFixture {
            path,
            witness_txid,
            opret_hex: hex::encode(commitment.to_byte_array()),
            contract_id: contract_id.to_string(),
        }
    }

    fn run_check(path: &str, txid: &str, opret_hex: &str) -> serde_json::Value {
        let json = commitment_check(
            path.to_string(),
            txid.to_string(),
            opret_hex.to_string(),
            FASCIA_ENTROPY,
        )
        .unwrap();
        serde_json::from_str(&json).unwrap()
    }

    fn write_two_contract_fascia_fixture(name: &str) -> FasciaFixture {
        let consignment_path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/consignment_out"
        );
        let consignment = Transfer::load_file(consignment_path).unwrap();
        let contract_id = consignment.contract_id();
        let witness_bundle = consignment.bundles.iter().next_back().unwrap();
        let bundle = witness_bundle.bundle.clone();
        let foreign_id = ContractId::from([0x11u8; 32]);

        let mut single = BTreeMap::new();
        single.insert(
            ProtocolId::from(contract_id),
            Message::from(bundle.bundle_id()),
        );
        let single_commitment = recompute_commitment(&single, FASCIA_ENTROPY).unwrap();

        let mut source = MultiSource::with_static_entropy(FASCIA_ENTROPY);
        source.messages = MediumOrdMap::from_checked(single);
        let tree = MerkleTree::try_commit(&source).unwrap();

        let seal_witness = SealWitness::new(
            witness_bundle.pub_witness.clone(),
            MerkleBlock::from(&tree),
            witness_bundle.anchor.dbc_proof.clone(),
        );
        let mut bundles = NonEmptyOrdMap::with_key_value(contract_id, bundle.clone());
        bundles.insert(foreign_id, bundle).unwrap();
        let fascia = Fascia::new(seal_witness, bundles);
        let witness_txid = fascia.witness_id().to_string();

        let path = std::env::temp_dir()
            .join(format!(
                "rgbverify_fascia_{}_{name}.json",
                std::process::id()
            ))
            .to_string_lossy()
            .into_owned();
        fs::write(&path, serde_json::to_string(&fascia).unwrap()).unwrap();

        FasciaFixture {
            path,
            witness_txid,
            opret_hex: hex::encode(single_commitment.to_byte_array()),
            contract_id: contract_id.to_string(),
        }
    }

    #[test]
    fn detects_two_committed_contracts() {
        let fixture = write_two_contract_fascia_fixture("cospend");
        let value = run_check(&fixture.path, &fixture.witness_txid, &fixture.opret_hex);
        let committed = value["committedContractIds"].as_array().unwrap();
        assert_eq!(committed.len(), 2);
        assert_eq!(value["matches"], false);
    }

    #[test]
    fn matches_and_binds_txid_and_contract() {
        let fixture = write_fascia_fixture("match");
        let value = run_check(&fixture.path, &fixture.witness_txid, &fixture.opret_hex);
        assert_eq!(value["matches"], true);
        assert_eq!(value["witnessIdMatches"], true);
        let committed = value["committedContractIds"].as_array().unwrap();
        assert_eq!(
            committed,
            &[serde_json::Value::from(fixture.contract_id.clone())]
        );
    }

    #[test]
    fn detects_txid_mismatch() {
        let fixture = write_fascia_fixture("txid");
        let wrong_txid = "0000000000000000000000000000000000000000000000000000000000000000";
        let value = run_check(&fixture.path, wrong_txid, &fixture.opret_hex);
        assert_eq!(value["witnessIdMatches"], false);
        assert_eq!(value["matches"], true);
    }

    #[test]
    fn detects_opret_mismatch() {
        let fixture = write_fascia_fixture("opret");
        let wrong_opret = "1111111111111111111111111111111111111111111111111111111111111111";
        let value = run_check(&fixture.path, &fixture.witness_txid, wrong_opret);
        assert_eq!(value["matches"], false);
    }

    #[test]
    fn rejects_non_hex_opret() {
        let fixture = write_fascia_fixture("badhex");
        assert!(commitment_check(
            fixture.path.clone(),
            fixture.witness_txid.clone(),
            "zz".to_string(),
            FASCIA_ENTROPY
        )
        .is_err());
    }

    #[test]
    fn rejects_wrong_length_opret() {
        let fixture = write_fascia_fixture("badlen");
        assert!(commitment_check(
            fixture.path.clone(),
            fixture.witness_txid.clone(),
            "aabb".to_string(),
            FASCIA_ENTROPY
        )
        .is_err());
    }
}
