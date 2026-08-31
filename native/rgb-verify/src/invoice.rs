use std::str::FromStr;

use amplify::ByteArray;
use rgbinvoice::{Beneficiary, InvoiceState, RgbInvoice};
use serde::Serialize;

use schemata::{CFA_SCHEMA_ID, NIA_SCHEMA_ID};

#[derive(Serialize)]
struct DecodedInvoice {
    #[serde(rename = "contractId")]
    contract_id: String,
    #[serde(rename = "amountKind")]
    amount_kind: String,
    amount: Option<u64>,
    #[serde(rename = "recipientSeal")]
    recipient_seal: String,
    #[serde(rename = "recipientChainNet")]
    recipient_chain_net: String,
    expiry: Option<i64>,
    transports: Vec<String>,
}

pub(crate) fn decode_invoice(invoice_str: String) -> Result<String, String> {
    let invoice = RgbInvoice::from_str(&invoice_str).map_err(|e| e.to_string())?;

    let contract = invoice
        .contract
        .ok_or_else(|| "invoice omits the contract id".to_string())?;

    if let Some(schema) = invoice.schema {
        if schema != NIA_SCHEMA_ID && schema != CFA_SCHEMA_ID {
            return Err("invoice schema hint is not a supported NIA/CFA schema".to_string());
        }
    }

    let (amount_kind, amount) = match &invoice.assignment_state {
        None => ("absent", None),
        Some(InvoiceState::Void) => ("absent", None),
        Some(InvoiceState::Amount(value)) => ("amount", Some(value.value())),
        Some(InvoiceState::Data(_)) => {
            return Err("invoice carries non-fungible (Data) state".to_string())
        }
    };

    let beneficiary = invoice.beneficiary;
    let recipient_chain_net = beneficiary.chain_network().prefix().to_string();
    let recipient_seal = match beneficiary.into_inner() {
        Beneficiary::BlindedSeal(seal) => hex::encode(seal.to_byte_array()),
        Beneficiary::WitnessVout(..) => {
            return Err("invoice uses a witness-mode (non-blinded) beneficiary".to_string())
        }
    };

    let transports = invoice
        .transports
        .iter()
        .map(|transport| transport.to_string())
        .filter(|value| !value.is_empty())
        .collect();

    let decoded = DecodedInvoice {
        contract_id: contract.to_string(),
        amount_kind: amount_kind.to_string(),
        amount,
        recipient_seal,
        recipient_chain_net,
        expiry: invoice.expiry,
        transports,
    };

    serde_json::to_string(&decoded).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    use rgbcore::{ChainNet, ContractId, SchemaId, SecretSeal};
    use rgbinvoice::{RgbInvoiceBuilder, XChainNet};
    use serde_json::Value;

    const CONTRACT: &str = "eIbQx5Am-XRDjj01-RM~5eo7-rv2nluD-OnBJRAy-S9~Yfts";
    const SEAL: &str = "utxob:4vm1CX2Z-K8hMo59-e7dgGBS-Jka7mYn-Xe~yP85-yUiHHxr-aVlYa";
    const NON_NIA_SCHEMA: &str = "XvmU3d4_nQQ8S7oagbXi07x5vjMm7P~ERukQNX6SC4M";

    fn contract_id() -> ContractId {
        ContractId::from_str(CONTRACT).unwrap()
    }

    fn seal() -> SecretSeal {
        SecretSeal::from_str(SEAL).unwrap()
    }

    fn blinded(chain_net: ChainNet) -> XChainNet<Beneficiary> {
        XChainNet::with(chain_net, Beneficiary::BlindedSeal(seal()))
    }

    fn decode_value(invoice: &RgbInvoice) -> Value {
        let json = decode_invoice(invoice.to_string()).unwrap();
        serde_json::from_str(&json).unwrap()
    }

    #[test]
    fn blinded_amount_roundtrip() {
        let invoice = RgbInvoiceBuilder::with(contract_id(), blinded(ChainNet::BitcoinRegtest))
            .set_schema(NIA_SCHEMA_ID)
            .set_amount_raw(100u64)
            .finish();

        let value = decode_value(&invoice);
        assert_eq!(value["contractId"], contract_id().to_string());
        assert_eq!(value["amountKind"], "amount");
        assert_eq!(value["amount"], 100u64);
        assert_eq!(value["recipientChainNet"], "bcrt");
        assert_eq!(value["recipientSeal"], hex::encode(seal().to_byte_array()));
        assert!(value["expiry"].is_null());
    }

    #[test]
    fn recipient_seal_matches_independent_parse() {
        let invoice = RgbInvoiceBuilder::with(contract_id(), blinded(ChainNet::BitcoinTestnet3))
            .set_schema(NIA_SCHEMA_ID)
            .set_amount_raw(1u64)
            .finish();

        let value = decode_value(&invoice);
        let expected = hex::encode(SecretSeal::from_str(SEAL).unwrap().to_byte_array());
        assert_eq!(value["recipientSeal"], expected);
        assert_eq!(value["recipientChainNet"], "tb3");
    }

    #[test]
    fn absent_amount_is_explicit() {
        let invoice = RgbInvoiceBuilder::with(contract_id(), blinded(ChainNet::BitcoinRegtest))
            .set_schema(NIA_SCHEMA_ID)
            .finish();

        let value = decode_value(&invoice);
        assert_eq!(value["amountKind"], "absent");
        assert!(value["amount"].is_null());
    }

    #[test]
    fn expiry_and_transports_populated() {
        let invoice = RgbInvoiceBuilder::with(contract_id(), blinded(ChainNet::BitcoinRegtest))
            .set_schema(NIA_SCHEMA_ID)
            .set_amount_raw(5u64)
            .set_expiry_timestamp(1_900_000_000)
            .add_transport("rpcs://proxy.iriswallet.com/0.2/json-rpc")
            .unwrap()
            .finish();

        let value = decode_value(&invoice);
        assert_eq!(value["expiry"], 1_900_000_000i64);
        let transports = value["transports"].as_array().unwrap();
        assert_eq!(transports.len(), 1);
        assert!(transports[0]
            .as_str()
            .unwrap()
            .contains("proxy.iriswallet.com"));
    }

    #[test]
    fn rejects_witness_mode_beneficiary() {
        let invoice_str = format!(
            "rgb:{CONTRACT}/~/~/bc:wvout:\
             A8cJ7Ww3-NIzADo3-Tzp_5aD-7CTBWmA-AAAAAAA-AAAAAAA-ALSQkcw"
        );
        let err = decode_invoice(invoice_str).unwrap_err();
        assert!(err.contains("witness"), "unexpected error: {err}");
    }

    #[test]
    fn rejects_contract_omitted() {
        let invoice = RgbInvoiceBuilder::new(blinded(ChainNet::BitcoinRegtest))
            .set_schema(NIA_SCHEMA_ID)
            .set_amount_raw(100u64)
            .finish();

        let err = decode_invoice(invoice.to_string()).unwrap_err();
        assert!(err.contains("contract"), "unexpected error: {err}");
    }

    #[test]
    fn accepts_cfa_schema() {
        let invoice = RgbInvoiceBuilder::with(contract_id(), blinded(ChainNet::BitcoinRegtest))
            .set_schema(CFA_SCHEMA_ID)
            .set_amount_raw(100u64)
            .finish();

        let value = decode_value(&invoice);
        assert_eq!(value["amount"], 100u64);
    }

    #[test]
    fn rejects_unsupported_schema() {
        let schema = SchemaId::from_str(NON_NIA_SCHEMA).unwrap();
        let invoice = RgbInvoiceBuilder::with(contract_id(), blinded(ChainNet::BitcoinRegtest))
            .set_schema(schema)
            .set_amount_raw(100u64)
            .finish();

        let err = decode_invoice(invoice.to_string()).unwrap_err();
        assert!(err.contains("NIA/CFA"), "unexpected error: {err}");
    }

    #[test]
    fn rejects_non_fungible_data_state() {
        let invoice = RgbInvoiceBuilder::with(contract_id(), blinded(ChainNet::BitcoinRegtest))
            .set_schema(NIA_SCHEMA_ID)
            .set_allocation(0, 1)
            .unwrap()
            .finish();

        let err = decode_invoice(invoice.to_string()).unwrap_err();
        assert!(err.contains("Data"), "unexpected error: {err}");
    }

    #[test]
    fn schema_omitted_is_accepted() {
        let invoice = RgbInvoiceBuilder::with(contract_id(), blinded(ChainNet::BitcoinRegtest))
            .set_amount_raw(7u64)
            .finish();

        let value = decode_value(&invoice);
        assert_eq!(value["amountKind"], "amount");
        assert_eq!(value["amount"], 7u64);
    }

    #[test]
    fn network_qualifier_reflects_mainnet() {
        let invoice = RgbInvoiceBuilder::with(contract_id(), blinded(ChainNet::BitcoinMainnet))
            .set_schema(NIA_SCHEMA_ID)
            .set_amount_raw(1u64)
            .finish();

        let value = decode_value(&invoice);
        assert_eq!(value["recipientChainNet"], "bc");
    }
}
