use std::collections::{BTreeMap, BTreeSet};

use amplify::Wrapper;
use serde::Serialize;

use rgbcore::bitcoin::OutPoint;
use rgbcore::seals::txout::TxPtr;
use rgbcore::{Assign, ContractId, OpId, Operation, Opout, Transition, TransitionBundle};
use rgbstd::contract::AllocatedState;
use rgbstd::persistence::{ContractAssignments, MemIndex, MemStash, MemState, Stock};
use schemata::{CFA_SCHEMA_ID, NIA_SCHEMA_ID};

#[derive(Serialize, Debug)]
pub(crate) struct ObservedAllocation {
    #[serde(rename = "contractId")]
    contract_id: String,
    kind: String,
    amount: Option<u64>,
    accounted: bool,
    reason: String,
}

#[derive(Serialize, Debug)]
pub(crate) struct ObservedInput {
    outpoint: String,
    observed: Vec<ObservedAllocation>,
}

#[derive(Debug)]
pub(crate) struct InputScan {
    pub inputs_accounted: bool,
    pub inputs: Vec<ObservedInput>,
}

#[derive(Serialize, Debug, Clone)]
pub(crate) struct CarryForwardProof {
    #[serde(rename = "contractId")]
    pub contract_id: String,
    pub opout: String,
    #[serde(rename = "transitionId")]
    pub transition_id: String,
    #[serde(rename = "inputOutpoint")]
    pub input_outpoint: String,
    #[serde(rename = "assignmentType")]
    pub assignment_type: u16,
    #[serde(rename = "stateKind")]
    pub state_kind: String,
    pub amount: Option<u64>,
    #[serde(rename = "successorKind")]
    pub successor_kind: String,
    #[serde(rename = "witnessVout")]
    pub witness_vout: Option<u32>,
    #[serde(rename = "successorOutpoint")]
    pub successor_outpoint: Option<String>,
    #[serde(rename = "derivationPath")]
    pub derivation_path: Option<String>,
}

#[derive(Debug)]
pub(crate) struct ExhaustiveInputScan {
    pub inputs: Vec<ObservedInput>,
    pub carry_forwards: Vec<CarryForwardProof>,
    pub verified_contract_ids: Vec<String>,
    pub verified_transition_ids: Vec<String>,
}

fn classify(
    is_x: bool,
    is_fungible: bool,
    in_inputs: bool,
    map_matches: bool,
) -> (bool, &'static str) {
    if is_x && is_fungible && in_inputs && map_matches {
        (true, "accountedTransferInput")
    } else if !is_x {
        (false, "foreignContract")
    } else if !is_fungible {
        (false, "nonFungibleOnInput")
    } else if !in_inputs {
        (false, "notInTransitionInputs")
    } else {
        (false, "inputMapMismatch")
    }
}

pub(crate) fn scan_inputs(
    stock: &Stock<MemStash, MemState, MemIndex>,
    contract_x: ContractId,
    transition: &Transition,
    input_map: &BTreeMap<Opout, OpId>,
    prevouts: &[OutPoint],
) -> Result<InputScan, String> {
    let genesis_contracts: BTreeSet<ContractId> = stock
        .contracts()
        .map_err(|e| format!("failed to enumerate stock contracts: {e}"))?
        .map(|info| info.id)
        .collect();

    for cid in stock.as_state_provider().debug_contracts().keys() {
        if !genesis_contracts.contains(cid) {
            return Err(format!(
                "stock inconsistency: contract {cid} has state without a genesis"
            ));
        }
    }

    let transition_id = transition.id();
    let transition_inputs: BTreeSet<Opout> = transition.inputs().into_iter().collect();
    let prevout_set: BTreeSet<OutPoint> = prevouts.iter().copied().collect();

    let mut per_outpoint: BTreeMap<OutPoint, Vec<ObservedAllocation>> = BTreeMap::new();
    let mut inputs_accounted = true;

    for cid in &genesis_contracts {
        let assignments: ContractAssignments = stock
            .contract_assignments_for(*cid, prevout_set.iter().copied())
            .map_err(|e| format!("failed to enumerate allocations for contract {cid}: {e}"))?;

        for (seal, opout_map) in assignments {
            let outpoint = OutPoint::from(seal);
            for (opout, state) in opout_map {
                let (kind, amount, is_fungible) = match &state {
                    AllocatedState::Amount(value) => ("amount", Some(value.as_u64()), true),
                    AllocatedState::Data(_) => ("data", None, false),
                    AllocatedState::Void => ("void", None, false),
                };
                let is_x = *cid == contract_x;
                let in_inputs = transition_inputs.contains(&opout);
                let map_matches = input_map.get(&opout) == Some(&transition_id);
                let (accounted, reason) = classify(is_x, is_fungible, in_inputs, map_matches);

                if !accounted {
                    inputs_accounted = false;
                }

                per_outpoint
                    .entry(outpoint)
                    .or_default()
                    .push(ObservedAllocation {
                        contract_id: cid.to_string(),
                        kind: kind.to_string(),
                        amount,
                        accounted,
                        reason: reason.to_string(),
                    });
            }
        }
    }

    let mut inputs = Vec::with_capacity(prevouts.len());
    let mut seen: BTreeSet<OutPoint> = BTreeSet::new();
    for out in prevouts {
        if !seen.insert(*out) {
            continue;
        }
        let mut observed = per_outpoint.remove(out).unwrap_or_default();
        observed.sort_by(|a, b| {
            a.contract_id
                .cmp(&b.contract_id)
                .then(a.reason.cmp(&b.reason))
                .then(a.amount.cmp(&b.amount))
        });
        inputs.push(ObservedInput {
            outpoint: format!("{}:{}", out.txid, out.vout),
            observed,
        });
    }

    Ok(InputScan {
        inputs_accounted,
        inputs,
    })
}

/// Exhaustively accounts the complete fascia against the pre-send Stock snapshot.
///
/// The intended transition is the only transition allowed to implement operator intent. Every
/// other disclosed transition must be beta.30's deliberately narrow one-input/one-output exact
/// carry-forward form. Bundle input maps, transition inputs and Stock allocations must be the same
/// set; no one of those three views is trusted to fill a gap in another.
pub(crate) fn scan_inputs_exhaustive(
    stock: &Stock<MemStash, MemState, MemIndex>,
    intended_contract: ContractId,
    main_transition_id: OpId,
    bundles: &BTreeMap<ContractId, TransitionBundle>,
    prevouts: &[OutPoint],
) -> Result<ExhaustiveInputScan, String> {
    if prevouts.is_empty() {
        return Err("witness transaction has no prevouts".to_string());
    }
    let prevout_set: BTreeSet<OutPoint> = prevouts.iter().copied().collect();
    if prevout_set.len() != prevouts.len() {
        return Err("witness transaction contains duplicate prevouts".to_string());
    }

    let genesis_contracts: BTreeSet<ContractId> = stock
        .contracts()
        .map_err(|e| format!("failed to enumerate stock contracts: {e}"))?
        .map(|info| info.id)
        .collect();
    for cid in stock.as_state_provider().debug_contracts().keys() {
        if !genesis_contracts.contains(cid) {
            return Err(format!(
                "stock inconsistency: contract {cid} has state without a genesis"
            ));
        }
    }

    let mut transitions = BTreeMap::<(ContractId, OpId), &Transition>::new();
    let mut mapped = BTreeMap::<(ContractId, Opout), OpId>::new();
    for (cid, bundle) in bundles {
        if !genesis_contracts.contains(cid) {
            return Err(format!("fascia commits unknown Stock contract {cid}"));
        }
        let info = stock
            .contract_info(*cid)
            .map_err(|e| format!("failed to load contract {cid} info: {e}"))?;
        if info.schema_id != NIA_SCHEMA_ID && info.schema_id != CFA_SCHEMA_ID {
            return Err(format!(
                "contract {cid} uses unsupported schema {}",
                info.schema_id
            ));
        }

        for known in &bundle.known_transitions {
            let calculated = known.transition.id();
            if known.opid != calculated {
                return Err(format!(
                    "contract {cid} transition key {} does not match transition id {calculated}",
                    known.opid
                ));
            }
            if known.transition.contract_id != *cid {
                return Err(format!(
                    "transition {} declares contract {} but is bundled under {cid}",
                    known.opid, known.transition.contract_id
                ));
            }
            if transitions
                .insert((*cid, known.opid), &known.transition)
                .is_some()
            {
                return Err(format!(
                    "duplicate transition {} under contract {cid}",
                    known.opid
                ));
            }
        }

        for (opout, transition_id) in &bundle.input_map {
            let transition = transitions.get(&(*cid, *transition_id)).ok_or_else(|| {
                format!(
                    "contract {cid} input map references undisclosed transition {transition_id}"
                )
            })?;
            if !transition.inputs().contains(opout) {
                return Err(format!(
                    "contract {cid} input map sends {opout} to transition {transition_id}, which does not consume it"
                ));
            }
            if mapped.insert((*cid, *opout), *transition_id).is_some() {
                return Err(format!(
                    "duplicate input-map entry for contract {cid} opout {opout}"
                ));
            }
        }

        for known in &bundle.known_transitions {
            for opout in known.transition.inputs() {
                match mapped.get(&(*cid, opout)) {
                    Some(mapped_id) if *mapped_id == known.opid => {}
                    Some(mapped_id) => {
                        return Err(format!(
                            "contract {cid} transition {} consumes {opout}, but the input map assigns it to {mapped_id}",
                            known.opid
                        ));
                    }
                    None => {
                        return Err(format!(
                            "contract {cid} transition {} consumes {opout} without an input-map entry",
                            known.opid
                        ));
                    }
                }
            }
        }
    }

    let main = transitions
        .get(&(intended_contract, main_transition_id))
        .ok_or_else(|| {
            format!(
                "intended transition {main_transition_id} is absent from the intended fascia bundle"
            )
        })?;
    if main.inputs().is_empty() {
        return Err("intended transition has no inputs".to_string());
    }

    let mut stock_allocations = BTreeMap::<(ContractId, Opout), (OutPoint, AllocatedState)>::new();
    let mut per_outpoint: BTreeMap<OutPoint, Vec<ObservedAllocation>> = BTreeMap::new();
    for cid in &genesis_contracts {
        let assignments: ContractAssignments = stock
            .contract_assignments_for(*cid, prevout_set.iter().copied())
            .map_err(|e| format!("failed to enumerate allocations for contract {cid}: {e}"))?;
        for (seal, opout_map) in assignments {
            let outpoint = OutPoint::from(seal);
            for (opout, state) in opout_map {
                if stock_allocations
                    .insert((*cid, opout), (outpoint, state.clone()))
                    .is_some()
                {
                    return Err(format!(
                        "duplicate Stock allocation for contract {cid} opout {opout}"
                    ));
                }
            }
        }
    }

    if stock_allocations.is_empty() {
        return Err("no RGB Stock allocations were found on the witness prevouts".to_string());
    }

    for key in mapped.keys() {
        if !stock_allocations.contains_key(key) {
            return Err(format!(
                "fascia input map references {}:{} which is not allocated on a witness prevout",
                key.0, key.1
            ));
        }
    }

    let mut used_transitions = BTreeSet::<(ContractId, OpId)>::new();
    let mut carry_forwards = Vec::new();
    for ((cid, opout), (outpoint, state)) in &stock_allocations {
        let transition_id = mapped.get(&(*cid, *opout)).ok_or_else(|| {
            format!("Stock allocation {cid}:{opout} on {outpoint} has no fascia consumer")
        })?;
        let transition = transitions.get(&(*cid, *transition_id)).ok_or_else(|| {
            format!("missing disclosed transition {transition_id} for {cid}:{opout}")
        })?;
        used_transitions.insert((*cid, *transition_id));

        let (kind, amount, reason): (String, Option<u64>, &'static str) =
            if *cid == intended_contract && *transition_id == main_transition_id {
                match state {
                    AllocatedState::Amount(value) => (
                        "amount".to_string(),
                        Some(value.as_u64()),
                        "accountedTransferInput",
                    ),
                    AllocatedState::Data(_) => {
                        return Err("intended transfer consumes unsupported data state".to_string());
                    }
                    AllocatedState::Void => {
                        return Err("intended transfer consumes unsupported void state".to_string());
                    }
                }
            } else {
                let proof =
                    verify_carry_forward(stock, *cid, *opout, *outpoint, state, transition)?;
                let kind = proof.state_kind.clone();
                let amount = proof.amount;
                carry_forwards.push(proof);
                (kind, amount, "accountedCarryForward")
            };

        per_outpoint
            .entry(*outpoint)
            .or_default()
            .push(ObservedAllocation {
                contract_id: cid.to_string(),
                kind,
                amount,
                accounted: true,
                reason: reason.to_string(),
            });
    }

    for key in transitions.keys() {
        if !used_transitions.contains(key) {
            return Err(format!(
                "fascia discloses unrelated transition {} under contract {}",
                key.1, key.0
            ));
        }
    }

    let mut inputs = Vec::with_capacity(prevouts.len());
    for outpoint in prevouts {
        let mut observed = per_outpoint.remove(outpoint).unwrap_or_default();
        observed.sort_by(|a, b| {
            a.contract_id
                .cmp(&b.contract_id)
                .then(a.reason.cmp(&b.reason))
                .then(a.amount.cmp(&b.amount))
        });
        inputs.push(ObservedInput {
            outpoint: format!("{}:{}", outpoint.txid, outpoint.vout),
            observed,
        });
    }

    carry_forwards.sort_by(|a, b| {
        a.contract_id
            .cmp(&b.contract_id)
            .then(a.transition_id.cmp(&b.transition_id))
    });
    let mut verified_contract_ids = bundles.keys().map(ToString::to_string).collect::<Vec<_>>();
    verified_contract_ids.sort();
    let mut verified_transition_ids = transitions
        .keys()
        .map(|(_, transition_id)| transition_id.to_string())
        .collect::<Vec<_>>();
    verified_transition_ids.sort();

    Ok(ExhaustiveInputScan {
        inputs,
        carry_forwards,
        verified_contract_ids,
        verified_transition_ids,
    })
}

fn verify_carry_forward(
    stock: &Stock<MemStash, MemState, MemIndex>,
    contract_id: ContractId,
    opout: Opout,
    input_outpoint: OutPoint,
    input_state: &AllocatedState,
    transition: &Transition,
) -> Result<CarryForwardProof, String> {
    if transition.inputs().len() != 1 || !transition.inputs().contains(&opout) {
        return Err(format!(
            "carry-forward transition {} is not an exact one-input transition for {opout}",
            transition.id()
        ));
    }
    if !transition.globals.is_empty() || !transition.metadata.is_empty() {
        return Err(format!(
            "carry-forward transition {} contains globals or metadata",
            transition.id()
        ));
    }
    if transition.signature.is_some() {
        return Err(format!(
            "carry-forward transition {} contains an unsupported signature",
            transition.id()
        ));
    }

    let info = stock
        .contract_info(contract_id)
        .map_err(|e| format!("failed to load contract {contract_id} info: {e}"))?;
    let schema = stock
        .schema(info.schema_id)
        .map_err(|e| format!("failed to load contract {contract_id} schema: {e}"))?;
    if !schema.owned_types.contains_key(&opout.ty) {
        return Err(format!(
            "contract {contract_id} schema does not define assignment type {}",
            opout.ty
        ));
    }
    let expected_transition_type = schema.default_transition_for_assignment(&opout.ty);
    if transition.transition_type != expected_transition_type {
        return Err(format!(
            "carry-forward transition {} uses type {}, expected schema default {}",
            transition.id(),
            transition.transition_type,
            expected_transition_type
        ));
    }
    if transition.assignments.len() != 1 {
        return Err(format!(
            "carry-forward transition {} has {} assignment types, expected one",
            transition.id(),
            transition.assignments.len()
        ));
    }
    let typed = transition.assignments.get(&opout.ty).ok_or_else(|| {
        format!(
            "carry-forward transition {} does not output assignment type {}",
            transition.id(),
            opout.ty
        )
    })?;
    if typed.len_u16() != 1 {
        return Err(format!(
            "carry-forward transition {} has {} outputs, expected one",
            transition.id(),
            typed.len_u16()
        ));
    }

    let (seal, state_kind, amount) = match input_state {
        AllocatedState::Amount(input_value) => {
            if !typed.is_fungible() {
                return Err(format!(
                    "carry-forward transition {} changes the allocated state kind",
                    transition.id()
                ));
            }
            let assign = typed
                .as_fungible()
                .first()
                .ok_or_else(|| "missing fungible carry-forward output".to_string())?;
            match assign {
                Assign::Revealed { seal, state } if state == input_value => {
                    (*seal, "amount", Some(state.as_u64()))
                }
                Assign::Revealed { .. } => {
                    return Err(format!(
                        "carry-forward transition {} changes the allocated state bytes",
                        transition.id()
                    ));
                }
                Assign::ConfidentialSeal { .. } => {
                    return Err(format!(
                        "carry-forward transition {} conceals its successor",
                        transition.id()
                    ));
                }
            }
        }
        AllocatedState::Data(_) => {
            return Err("carry-forward of data state is unsupported".to_string());
        }
        AllocatedState::Void => {
            return Err("carry-forward of void state is unsupported".to_string());
        }
    };

    let (successor_kind, witness_vout, successor_outpoint) = match seal.txid {
        TxPtr::WitnessTx => (
            "revealedWitnessVout".to_string(),
            Some(seal.vout.into_u32()),
            None,
        ),
        TxPtr::Txid(txid) => (
            "revealedConcreteOutpoint".to_string(),
            None,
            Some(format!("{txid}:{}", seal.vout.into_u32())),
        ),
    };

    Ok(CarryForwardProof {
        contract_id: contract_id.to_string(),
        opout: opout.to_string(),
        transition_id: transition.id().to_string(),
        input_outpoint: format!("{}:{}", input_outpoint.txid, input_outpoint.vout),
        assignment_type: opout.ty.to_inner(),
        state_kind: state_kind.to_string(),
        amount,
        successor_kind,
        witness_vout,
        successor_outpoint,
        derivation_path: None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    use std::fs;
    use std::path::{Path, PathBuf};
    use std::str::FromStr;

    use amplify::confinement::{NonEmptyOrdSet, U16};
    use rgbcore::{AssignmentType, Inputs, KnownTransition, RevealedValue};
    use rgbstd::containers::{FileContent, Transfer};
    use rgbstd::persistence::fs::FsBinStore;

    const CID_A: &str = "rgb:Cfn6bJvN-r_xEQET-1DslmTr-rCbxbgR-S0kAu2_-8XkSq~A";
    const CID_B: &str = "rgb:Q3BzNdGX-EbHQ65U-AN4Px9g-6tlkViw-Lzn9uLo-yRriVco";
    const CID_C: &str = "rgb:jbWkxjFq-ZTzP50O-uLTADZi-RFUMLXk-aKzH2UI-t7K4RyI";
    const OUT_CLEAN: &str = "a68be11f883a9423bd7a3ba729e2dfd417e0db054d8d8e2a7ddc371401fd79fc:0";
    const OUT_MULTI: &str = "a1306969b0972548d686657e8c9f93ab9a7a9df2dba116fcae5586adf4ac81b6:1";
    const ZERO_OUT: &str = "0000000000000000000000000000000000000000000000000000000000000000:0";

    fn fixture_dir() -> PathBuf {
        PathBuf::from(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/stock_multi"
        ))
    }

    fn load_multi() -> Stock<MemStash, MemState, MemIndex> {
        let store = FsBinStore::new(fixture_dir()).unwrap();
        Stock::<MemStash, MemState, MemIndex>::load(store, false).unwrap()
    }

    fn cid(s: &str) -> ContractId {
        ContractId::from_str(s).unwrap()
    }

    fn out(s: &str) -> OutPoint {
        OutPoint::from_str(s).unwrap()
    }

    fn opout_at(
        stock: &Stock<MemStash, MemState, MemIndex>,
        contract: ContractId,
        outpoint: OutPoint,
    ) -> Opout {
        let assignments = stock
            .contract_assignments_for(contract, [outpoint])
            .unwrap();
        let (_seal, opout_map) = assignments.into_iter().next().unwrap();
        opout_map.into_iter().next().unwrap().0
    }

    fn base_transition() -> Transition {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/consignment_out"
        );
        let consignment = Transfer::load_file(path).unwrap();
        let bundle = consignment.bundles.iter().next_back().unwrap();
        bundle
            .bundle
            .known_transitions
            .iter()
            .next()
            .unwrap()
            .transition
            .clone()
    }

    fn transition_with_inputs(opouts: &[Opout]) -> Transition {
        let mut transition = base_transition();
        let mut iter = opouts.iter();
        let mut set = NonEmptyOrdSet::<Opout, U16>::with(*iter.next().unwrap());
        for opout in iter {
            set.push(*opout).unwrap();
        }
        transition.inputs = Inputs::from(set);
        transition
    }

    fn accounting_map(transition: &Transition, opouts: &[Opout]) -> BTreeMap<Opout, OpId> {
        let id = transition.id();
        opouts.iter().map(|opout| (*opout, id)).collect()
    }

    fn bundle_for(transition: Transition, opouts: &[Opout]) -> TransitionBundle {
        let id = transition.id();
        let mut bundle = terminal_transition_bundle();
        bundle.known_transitions =
            amplify::confinement::Confined::from_checked(vec![KnownTransition {
                opid: id,
                transition,
            }]);
        bundle.input_map =
            amplify::confinement::Confined::try_from_iter(opouts.iter().map(|opout| (*opout, id)))
                .unwrap();
        bundle
    }

    fn terminal_transition_bundle() -> TransitionBundle {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/consignment_out"
        );
        let consignment = Transfer::load_file(path).unwrap();
        consignment
            .bundles
            .iter()
            .next_back()
            .unwrap()
            .bundle
            .clone()
    }

    fn write_empty_store(dir: &Path) {
        let store = FsBinStore::new(dir.to_path_buf()).unwrap();
        let mut stock = Stock::in_memory();
        stock.make_persistent(store, true).unwrap();
        stock.store().unwrap();
    }

    fn unique_tmp(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "rgbverify_stock_{}_{}_{tag}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ))
    }

    #[test]
    fn accounts_clean_single_asset_send() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let opout = opout_at(&stock, x, outpoint);
        let transition = transition_with_inputs(&[opout]);
        let map = accounting_map(&transition, &[opout]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(scan.inputs_accounted);
        assert_eq!(scan.inputs.len(), 1);
        let observed = &scan.inputs[0].observed;
        assert_eq!(observed.len(), 1);
        assert!(observed[0].accounted);
        assert_eq!(observed[0].reason, "accountedTransferInput");
        assert_eq!(observed[0].kind, "amount");
        assert_eq!(observed[0].amount, Some(5000));
        assert_eq!(observed[0].contract_id, CID_A);
    }

    #[test]
    fn rejects_foreign_contract_on_input() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_MULTI);
        let opout = opout_at(&stock, x, outpoint);
        let transition = transition_with_inputs(&[opout]);
        let map = accounting_map(&transition, &[opout]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(!scan.inputs_accounted);
        let observed = &scan.inputs[0].observed;
        assert_eq!(observed.len(), 3);
        let foreign: Vec<_> = observed
            .iter()
            .filter(|o| o.reason == "foreignContract")
            .map(|o| o.contract_id.as_str())
            .collect();
        assert_eq!(foreign.len(), 2);
        assert!(foreign.contains(&CID_B));
        assert!(foreign.contains(&CID_C));
        let accounted: Vec<_> = observed.iter().filter(|o| o.accounted).collect();
        assert_eq!(accounted.len(), 1);
        assert_eq!(accounted[0].contract_id, CID_A);
    }

    #[test]
    fn rejects_unaccounted_x_allocation() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let real_opout = opout_at(&stock, x, outpoint);
        let decoy = Opout::new(real_opout.op, AssignmentType::with(4000), 9);
        let transition = transition_with_inputs(&[decoy]);
        let map = accounting_map(&transition, &[decoy]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(!scan.inputs_accounted);
        let observed = &scan.inputs[0].observed;
        assert_eq!(observed.len(), 1);
        assert!(!observed[0].accounted);
        assert_eq!(observed[0].reason, "notInTransitionInputs");
    }

    #[test]
    fn rejects_x_input_not_in_input_map() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let opout = opout_at(&stock, x, outpoint);
        let transition = transition_with_inputs(&[opout]);
        let wrong_id = OpId::from([0x42u8; 32]);
        let map: BTreeMap<Opout, OpId> = [(opout, wrong_id)].into_iter().collect();

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(!scan.inputs_accounted);
        let observed = &scan.inputs[0].observed;
        assert_eq!(observed[0].reason, "inputMapMismatch");
        assert!(!observed[0].accounted);
    }

    #[test]
    fn same_seal_output_leg_is_not_carry_forward() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let real_opout = opout_at(&stock, x, outpoint);
        let base = base_transition();
        let output_leg = Opout::new(base.id(), real_opout.ty, real_opout.no);
        let transition = transition_with_inputs(&[output_leg]);
        let map = accounting_map(&transition, &[output_leg]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(!scan.inputs_accounted);
        assert_eq!(scan.inputs[0].observed[0].reason, "notInTransitionInputs");
    }

    #[test]
    fn rejects_non_fungible_allocation_on_input() {
        assert_eq!(
            classify(true, false, true, true),
            (false, "nonFungibleOnInput")
        );
        assert_eq!(
            classify(true, true, true, true),
            (true, "accountedTransferInput")
        );
    }

    #[test]
    fn uncolored_input_is_accounted() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(ZERO_OUT);
        let transition = transition_with_inputs(&[Opout::new(
            x_opid(&stock, x, out(OUT_CLEAN)),
            AssignmentType::with(4000),
            0,
        )]);
        let map = accounting_map(&transition, &[]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(scan.inputs_accounted);
        assert_eq!(scan.inputs.len(), 1);
        assert!(scan.inputs[0].observed.is_empty());
    }

    fn x_opid(
        stock: &Stock<MemStash, MemState, MemIndex>,
        contract: ContractId,
        outpoint: OutPoint,
    ) -> OpId {
        opout_at(stock, contract, outpoint).op
    }

    #[test]
    fn input_scan_uses_anchor_verified_transition() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let opout = opout_at(&stock, x, outpoint);

        let good = transition_with_inputs(&[opout]);
        let map = accounting_map(&good, &[opout]);
        let scan_good = scan_inputs(&stock, x, &good, &map, &[outpoint]).unwrap();
        assert!(scan_good.inputs_accounted);

        let other = transition_with_inputs(&[opout, Opout::new(opout.op, opout.ty, 7)]);
        assert_ne!(good.id(), other.id());
        let scan_other = scan_inputs(&stock, x, &other, &map, &[outpoint]).unwrap();
        assert!(!scan_other.inputs_accounted);
        assert_eq!(scan_other.inputs[0].observed[0].reason, "inputMapMismatch");
    }

    #[test]
    fn native_derives_full_input_set_from_witness_tx() {
        let stock = load_multi();
        let x = cid(CID_A);
        let clean = out(OUT_CLEAN);
        let opout = opout_at(&stock, x, clean);
        let transition = transition_with_inputs(&[opout]);
        let map = accounting_map(&transition, &[opout]);

        let prevouts = [clean, out(ZERO_OUT), out(OUT_MULTI)];
        let scan = scan_inputs(&stock, x, &transition, &map, &prevouts).unwrap();

        assert_eq!(scan.inputs.len(), 3);
        let outpoints: Vec<&str> = scan.inputs.iter().map(|i| i.outpoint.as_str()).collect();
        assert!(outpoints.contains(&OUT_CLEAN));
        assert!(outpoints.contains(&ZERO_OUT));
        assert!(outpoints.contains(&OUT_MULTI));
    }

    #[test]
    fn per_contract_enumeration_error_is_fatal() {
        let dir = unique_tmp("genesis_no_state");
        write_empty_store(&dir);
        let src = fixture_dir();
        fs::copy(src.join("stash.dat"), dir.join("stash.dat")).unwrap();
        fs::copy(src.join("index.dat"), dir.join("index.dat")).unwrap();

        let store = FsBinStore::new(dir.clone()).unwrap();
        let stock = Stock::<MemStash, MemState, MemIndex>::load(store, false).unwrap();
        let x = cid(CID_A);
        let transition = transition_with_inputs(&[Opout::new(
            OpId::from([0x01u8; 32]),
            AssignmentType::with(4000),
            0,
        )]);
        let map = accounting_map(&transition, &[]);

        let result = scan_inputs(&stock, x, &transition, &map, &[out(OUT_CLEAN)]);
        assert!(result.is_err());
        assert!(result
            .unwrap_err()
            .contains("failed to enumerate allocations"));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn observed_outpoints_are_canonical() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_MULTI);
        let opout = opout_at(&stock, x, outpoint);
        let transition = transition_with_inputs(&[opout]);
        let map = accounting_map(&transition, &[opout]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        let key = &scan.inputs[0].outpoint;
        let (txid, vout) = key.split_once(':').unwrap();
        assert_eq!(txid.len(), 64);
        assert_eq!(txid, txid.to_lowercase());
        assert!(txid.chars().all(|c| c.is_ascii_hexdigit()));
        vout.parse::<u32>().unwrap();
        assert_eq!(key, OUT_MULTI);
    }

    fn exact_carry_transition(
        stock: &Stock<MemStash, MemState, MemIndex>,
        contract: ContractId,
        opout: Opout,
        value: RevealedValue,
    ) -> Transition {
        let mut transition = base_transition();
        transition.contract_id = contract;
        transition.inputs = Inputs::from(NonEmptyOrdSet::with(opout));
        let schema_id = stock.contract_info(contract).unwrap().schema_id;
        transition.transition_type = stock
            .schema(schema_id)
            .unwrap()
            .default_transition_for_assignment(&opout.ty);
        transition.globals = Default::default();
        transition.metadata = Default::default();
        transition.signature = None;

        let revealed_seal = base_transition()
            .assignments
            .values()
            .flat_map(|typed| typed.as_fungible().iter())
            .find_map(|assign| match assign {
                Assign::Revealed { seal, .. } => Some(*seal),
                Assign::ConfidentialSeal { .. } => None,
            })
            .unwrap();
        let assign = Assign::Revealed {
            seal: revealed_seal,
            state: value,
        };
        let typed = transition.assignments.get_mut(&opout.ty).unwrap();
        *typed.as_fungible_mut().unwrap() = amplify::confinement::NonEmptyVec::with(assign);
        transition
    }

    #[test]
    fn accepts_exact_foreign_fungible_carry_normal_form() {
        let stock = load_multi();
        let contract = cid(CID_B);
        let outpoint = out(OUT_MULTI);
        let assignments = stock
            .contract_assignments_for(contract, [outpoint])
            .unwrap();
        let (_, opouts) = assignments.into_iter().next().unwrap();
        let (opout, state) = opouts.into_iter().next().unwrap();
        let AllocatedState::Amount(value) = state else {
            panic!("expected amount")
        };
        let transition = exact_carry_transition(&stock, contract, opout, value);

        let proof = verify_carry_forward(
            &stock,
            contract,
            opout,
            outpoint,
            &AllocatedState::Amount(value),
            &transition,
        )
        .unwrap();

        assert_eq!(proof.contract_id, CID_B);
        assert_eq!(proof.amount, Some(value.as_u64()));
        assert_eq!(proof.successor_kind, "revealedWitnessVout");
        assert!(proof.witness_vout.is_some());
    }

    #[test]
    fn exhaustive_scan_accepts_clean_main_transition() {
        let stock = load_multi();
        let contract = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let opout = opout_at(&stock, contract, outpoint);
        let mut transition = transition_with_inputs(&[opout]);
        transition.contract_id = contract;
        let transition_id = transition.id();
        let bundles = [(contract, bundle_for(transition, &[opout]))]
            .into_iter()
            .collect();

        let scan =
            scan_inputs_exhaustive(&stock, contract, transition_id, &bundles, &[outpoint]).unwrap();

        assert!(scan.carry_forwards.is_empty());
        assert_eq!(scan.verified_contract_ids, vec![CID_A]);
        assert_eq!(
            scan.verified_transition_ids,
            vec![transition_id.to_string()]
        );
    }

    #[test]
    fn exhaustive_scan_rejects_unmapped_colocated_allocations() {
        let stock = load_multi();
        let contract = cid(CID_A);
        let outpoint = out(OUT_MULTI);
        let opout = opout_at(&stock, contract, outpoint);
        let mut transition = transition_with_inputs(&[opout]);
        transition.contract_id = contract;
        let transition_id = transition.id();
        let bundles = [(contract, bundle_for(transition, &[opout]))]
            .into_iter()
            .collect();

        let err = scan_inputs_exhaustive(&stock, contract, transition_id, &bundles, &[outpoint])
            .unwrap_err();

        assert!(err.contains("has no fascia consumer"), "{err}");
    }

    #[test]
    fn rejects_foreign_carry_that_changes_amount() {
        let stock = load_multi();
        let contract = cid(CID_B);
        let outpoint = out(OUT_MULTI);
        let assignments = stock
            .contract_assignments_for(contract, [outpoint])
            .unwrap();
        let (_, opouts) = assignments.into_iter().next().unwrap();
        let (opout, state) = opouts.into_iter().next().unwrap();
        let AllocatedState::Amount(value) = state else {
            panic!("expected amount")
        };
        let changed = RevealedValue::new(value.as_u64() + 1);
        let transition = exact_carry_transition(&stock, contract, opout, changed);

        let err = verify_carry_forward(
            &stock,
            contract,
            opout,
            outpoint,
            &AllocatedState::Amount(value),
            &transition,
        )
        .unwrap_err();
        assert!(err.contains("changes the allocated state bytes"), "{err}");
    }

    fn hostile_two_output_carry_transition(
        stock: &Stock<MemStash, MemState, MemIndex>,
        contract: ContractId,
        opout: Opout,
        value: RevealedValue,
        first_output: u64,
        second_output: u64,
    ) -> Transition {
        let mut transition = exact_carry_transition(stock, contract, opout, value);
        let seal = match transition
            .assignments
            .get(&opout.ty)
            .unwrap()
            .as_fungible()
            .first()
            .unwrap()
        {
            Assign::Revealed { seal, .. } => *seal,
            Assign::ConfidentialSeal { .. } => panic!("expected revealed seal"),
        };
        let mut split = amplify::confinement::NonEmptyVec::with(Assign::Revealed {
            seal,
            state: RevealedValue::new(first_output),
        });
        split
            .push(Assign::Revealed {
                seal,
                state: RevealedValue::new(second_output),
            })
            .unwrap();
        *transition
            .assignments
            .get_mut(&opout.ty)
            .unwrap()
            .as_fungible_mut()
            .unwrap() = split;
        transition
    }

    #[test]
    fn rejects_foreign_carry_that_splits_the_sum_across_two_outputs() {
        let stock = load_multi();
        let contract = cid(CID_B);
        let outpoint = out(OUT_MULTI);
        let assignments = stock
            .contract_assignments_for(contract, [outpoint])
            .unwrap();
        let (_, opouts) = assignments.into_iter().next().unwrap();
        let (opout, state) = opouts.into_iter().next().unwrap();
        let AllocatedState::Amount(value) = state else {
            panic!("expected amount")
        };
        let whole = value.as_u64();
        let first = whole / 2;
        let transition = hostile_two_output_carry_transition(
            &stock,
            contract,
            opout,
            value,
            first,
            whole - first,
        );

        let err = verify_carry_forward(
            &stock,
            contract,
            opout,
            outpoint,
            &AllocatedState::Amount(value),
            &transition,
        )
        .unwrap_err();
        assert!(
            err.contains("has 2 outputs, expected one"),
            "the one-output carry-forward normal form must reject a sum-preserving two-output split; got: {err}"
        );
    }

    #[test]
    fn rejects_foreign_carry_that_duplicates_the_whole_amount_into_a_second_output() {
        let stock = load_multi();
        let contract = cid(CID_B);
        let outpoint = out(OUT_MULTI);
        let assignments = stock
            .contract_assignments_for(contract, [outpoint])
            .unwrap();
        let (_, opouts) = assignments.into_iter().next().unwrap();
        let (opout, state) = opouts.into_iter().next().unwrap();
        let AllocatedState::Amount(value) = state else {
            panic!("expected amount")
        };
        let whole = value.as_u64();
        let transition =
            hostile_two_output_carry_transition(&stock, contract, opout, value, whole, whole);

        let err = verify_carry_forward(
            &stock,
            contract,
            opout,
            outpoint,
            &AllocatedState::Amount(value),
            &transition,
        )
        .unwrap_err();
        assert!(
            err.contains("has 2 outputs, expected one"),
            "only the one-output clause stands between a carry-forward and inflation: a first output equal to the input value satisfies every downstream state-bytes check, so a duplicated second output would otherwise be accepted; got: {err}"
        );
    }

    #[test]
    fn rejects_foreign_carry_that_uses_a_non_schema_default_transition_type() {
        let stock = load_multi();
        let contract = cid(CID_B);
        let outpoint = out(OUT_MULTI);
        let assignments = stock
            .contract_assignments_for(contract, [outpoint])
            .unwrap();
        let (_, opouts) = assignments.into_iter().next().unwrap();
        let (opout, state) = opouts.into_iter().next().unwrap();
        let AllocatedState::Amount(value) = state else {
            panic!("expected amount")
        };
        let mut transition = exact_carry_transition(&stock, contract, opout, value);
        let expected = stock
            .schema(stock.contract_info(contract).unwrap().schema_id)
            .unwrap()
            .default_transition_for_assignment(&opout.ty);
        transition.transition_type =
            rgbcore::TransitionType::with(expected.to_inner().wrapping_add(1));

        let err = verify_carry_forward(
            &stock,
            contract,
            opout,
            outpoint,
            &AllocatedState::Amount(value),
            &transition,
        )
        .unwrap_err();
        assert!(
            err.contains("expected schema default"),
            "the carry-forward normal form must reject a transition type other than the schema default; got: {err}"
        );
    }

    #[test]
    fn missing_or_corrupt_stock_errors() {
        let missing = unique_tmp("missing");
        let store = FsBinStore::new(missing.clone()).unwrap();
        assert!(Stock::<MemStash, MemState, MemIndex>::load(store, false).is_err());
        let _ = fs::remove_dir_all(&missing);

        let corrupt = unique_tmp("corrupt");
        fs::create_dir_all(&corrupt).unwrap();
        for name in ["stash.dat", "state.dat", "index.dat"] {
            fs::write(corrupt.join(name), b"not a valid strict-encoded blob").unwrap();
        }
        let store = FsBinStore::new(corrupt.clone()).unwrap();
        assert!(Stock::<MemStash, MemState, MemIndex>::load(store, false).is_err());
        let _ = fs::remove_dir_all(&corrupt);
    }

    #[test]
    fn rejects_cross_file_inconsistent_stock() {
        let dir = unique_tmp("state_no_genesis");
        write_empty_store(&dir);
        let src = fixture_dir();
        fs::copy(src.join("state.dat"), dir.join("state.dat")).unwrap();

        let store = FsBinStore::new(dir.clone()).unwrap();
        let stock = Stock::<MemStash, MemState, MemIndex>::load(store, false).unwrap();
        let x = cid(CID_A);
        let transition = transition_with_inputs(&[Opout::new(
            OpId::from([0x01u8; 32]),
            AssignmentType::with(4000),
            0,
        )]);
        let map = accounting_map(&transition, &[]);

        let result = scan_inputs(&stock, x, &transition, &map, &[out(OUT_CLEAN)]);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("stock inconsistency"));
        let _ = fs::remove_dir_all(&dir);
    }
}
