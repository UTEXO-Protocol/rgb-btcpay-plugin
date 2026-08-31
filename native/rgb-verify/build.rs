use std::fs;

const PINNED: &[(&str, &str)] = &[
    ("rgb-ops", "0.11.1-rc.10"),
    ("rgb-consensus", "0.11.1-rc.10"),
    ("rgb-schemas", "0.11.1-rc.10"),
    ("rgb-invoicing", "0.11.1-rc.10"),
    ("rgb-strict-types", "1.0.1"),
    ("bdk_wallet", "3.0.0"),
    ("bdk_file_store", "0.22.0"),
    ("amplify", "4.8.1"),
];

fn main() {
    let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
    assert_pinned_versions(&manifest_dir);
    generate_header(&manifest_dir);
    set_macos_install_name();
}

fn assert_pinned_versions(manifest_dir: &str) {
    let lock_path = find_cargo_lock(manifest_dir);
    println!("cargo:rerun-if-changed={lock_path}");

    let lock = fs::read_to_string(&lock_path)
        .unwrap_or_else(|e| panic!("rgb-verify pin check cannot read {lock_path}: {e}"));

    for (name, expected) in PINNED {
        let resolved = resolved_version(&lock, name);
        match resolved {
            Some(version) if version == *expected => {}
            Some(version) => panic!(
                "rgb-verify pin violation: {name} resolved to {version}, expected {expected}"
            ),
            None => panic!("rgb-verify pin violation: {name} not found in Cargo.lock"),
        }
    }
}

fn find_cargo_lock(manifest_dir: &str) -> String {
    let mut dir = std::path::Path::new(manifest_dir);
    loop {
        let candidate = dir.join("Cargo.lock");
        if candidate.is_file() {
            return candidate.to_string_lossy().into_owned();
        }
        match dir.parent() {
            Some(parent) => dir = parent,
            None => {
                panic!("rgb-verify pin check could not locate Cargo.lock from {manifest_dir}")
            }
        }
    }
}

fn generate_header(manifest_dir: &str) {
    println!("cargo:rerun-if-changed={manifest_dir}/src/lib.rs");
    match cbindgen::generate(manifest_dir) {
        Ok(bindings) => {
            bindings.write_to_file(format!("{manifest_dir}/rgbverify.h"));
        }
        Err(e) => println!("cargo:warning=rgbverify header generation failed: {e}"),
    }
}

fn set_macos_install_name() {
    if std::env::var("TARGET")
        .map(|target| target.contains("apple-darwin"))
        .unwrap_or(false)
    {
        println!("cargo:rustc-link-arg=-Wl,-install_name,@rpath/librgbverifycffi.dylib");
    }
}

fn resolved_version(lock: &str, package: &str) -> Option<String> {
    let needle = format!("name = \"{package}\"");
    for block in lock.split("[[package]]") {
        if block.lines().any(|line| line.trim() == needle) {
            for line in block.lines() {
                let line = line.trim();
                if let Some(rest) = line.strip_prefix("version = \"") {
                    if let Some(version) = rest.strip_suffix('"') {
                        return Some(version.to_string());
                    }
                }
            }
        }
    }
    None
}
