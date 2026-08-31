use std::ffi::{c_char, CStr, CString};

mod commitment;
mod inputs;
mod invoice;
mod validate;
mod validate_v2;

use commitment::commitment_check;
use invoice::decode_invoice;
use validate::validate;
use validate_v2::validate_v2;

#[repr(C)]
pub enum CResultValue {
    Ok,
    Err,
}

#[repr(C)]
pub struct CResultString {
    pub result: CResultValue,
    pub inner: *mut c_char,
}

fn string_to_ptr(value: String) -> *mut c_char {
    match CString::new(value) {
        Ok(cstr) => cstr.into_raw(),
        Err(_) => CString::new("error: string contains a null-char")
            .unwrap()
            .into_raw(),
    }
}

impl From<Result<String, String>> for CResultString {
    fn from(result: Result<String, String>) -> Self {
        match result {
            Ok(payload) => CResultString {
                result: CResultValue::Ok,
                inner: string_to_ptr(payload),
            },
            Err(message) => CResultString {
                result: CResultValue::Err,
                inner: string_to_ptr(message),
            },
        }
    }
}

fn cstr_to_string(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::new();
    }
    unsafe { CStr::from_ptr(ptr).to_string_lossy().into_owned() }
}

fn guard<F: FnOnce() -> Result<String, String>>(f: F) -> CResultString {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)) {
        Ok(result) => result.into(),
        Err(_) => Err::<String, String>("verification aborted".to_string()).into(),
    }
}

#[no_mangle]
pub extern "C" fn rgbverify_decode_invoice(invoice: *const c_char) -> CResultString {
    guard(|| decode_invoice(cstr_to_string(invoice)))
}

#[no_mangle]
pub extern "C" fn rgbverify_validate(
    consignment_path: *const c_char,
    unsigned_txid: *const c_char,
    indexer_url: *const c_char,
    network: *const c_char,
    stock_dir: *const c_char,
) -> CResultString {
    guard(|| {
        validate(
            cstr_to_string(consignment_path),
            cstr_to_string(unsigned_txid),
            cstr_to_string(indexer_url),
            cstr_to_string(network),
            cstr_to_string(stock_dir),
        )
    })
}

#[no_mangle]
pub extern "C" fn rgbverify_commitment_check(
    fascia_path: *const c_char,
    unsigned_txid: *const c_char,
    opret_commitment_bytes: *const c_char,
    entropy: u64,
) -> CResultString {
    guard(|| {
        commitment_check(
            cstr_to_string(fascia_path),
            cstr_to_string(unsigned_txid),
            cstr_to_string(opret_commitment_bytes),
            entropy,
        )
    })
}

#[no_mangle]
pub extern "C" fn rgbverify_validate_v2(request_json: *const c_char) -> CResultString {
    guard(|| validate_v2(cstr_to_string(request_json)))
}

#[no_mangle]
pub extern "C" fn rgbverify_string_free(ptr: *mut c_char) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        let _ = CString::from_raw(ptr);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr::null_mut;

    #[test]
    fn string_free_reclaims_ok_err_and_null() {
        for i in 0..10_000 {
            let ok: CResultString = Ok::<String, String>(format!("payload-{i}")).into();
            assert!(matches!(ok.result, CResultValue::Ok));
            assert!(!ok.inner.is_null());
            rgbverify_string_free(ok.inner);

            let err: CResultString = Err::<String, String>(format!("error-{i}")).into();
            assert!(matches!(err.result, CResultValue::Err));
            assert!(!err.inner.is_null());
            rgbverify_string_free(err.inner);
        }

        rgbverify_string_free(null_mut());
    }

    #[test]
    fn guard_converts_panic_to_err() {
        let result = guard(|| panic!("boom"));
        assert!(matches!(result.result, CResultValue::Err));
        assert!(!result.inner.is_null());
        rgbverify_string_free(result.inner);
    }

    #[test]
    fn guard_passes_through_ok() {
        let result = guard(|| Ok("payload".to_string()));
        assert!(matches!(result.result, CResultValue::Ok));
        rgbverify_string_free(result.inner);
    }
}
