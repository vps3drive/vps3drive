use std::env;
use std::path::PathBuf;
use std::process::Command;

fn main() {
    println!("cargo:rerun-if-changed=app.rc");
    println!("cargo:rerun-if-changed=assets/vps3drive.ico");

    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }

    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let res_path = out_dir.join("app.res");

    let windres = env::var("WINDRES").unwrap_or_else(|_| "windres".to_string());
    let status = Command::new(&windres)
        .args([
            "app.rc",
            "-O",
            "coff",
            "-o",
            res_path.to_str().unwrap(),
        ])
        .status();

    if let Ok(s) = status {
        if s.success() {
            println!("cargo:rustc-link-arg={}", res_path.display());
        }
    }
}
