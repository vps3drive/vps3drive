use std::ffi::OsStr;
use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::os::windows::ffi::OsStrExt;
use std::path::PathBuf;
use std::time::Duration;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{HANDLE, HGLOBAL};
use windows::Win32::System::DataExchange::{
    CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData,
};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
use windows::Win32::System::Registry::{
    RegCloseKey, RegCreateKeyExW, RegDeleteTreeW, RegOpenKeyExW, RegSetValueExW, HKEY,
    HKEY_CURRENT_USER, KEY_READ, KEY_WRITE, REG_OPTION_NON_VOLATILE, REG_SZ,
};
use windows::Win32::UI::Shell::{SHChangeNotify, SHCNE_UPDATEDIR, SHCNF_PATHW};

const CF_UNICODETEXT: u32 = 13;

fn to_wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

pub fn ipc_port_path() -> PathBuf {
    let base = std::env::var("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|_| std::env::temp_dir());
    base.join("VPS3Drive").join("ipc.port")
}

pub fn write_ipc_port(port: u16) {
    let p = ipc_port_path();
    if let Some(parent) = p.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let _ = std::fs::write(p, port.to_string());
}

pub fn remove_ipc_port() {
    let _ = std::fs::remove_file(ipc_port_path());
}

pub fn copy_to_clipboard(text: &str) -> Result<(), String> {
    let wide = to_wide(text);
    let byte_len = wide.len() * 2;
    unsafe {
        if OpenClipboard(None).is_err() {
            return Err("Failed to open clipboard".to_string());
        }
        let _ = EmptyClipboard();
        let hmem: HGLOBAL = GlobalAlloc(GMEM_MOVEABLE, byte_len)
            .map_err(|e| {
                let _ = CloseClipboard();
                format!("GlobalAlloc failed: {e}")
            })?;
        let ptr = GlobalLock(hmem) as *mut u16;
        if ptr.is_null() {
            let _ = CloseClipboard();
            return Err("GlobalLock failed".to_string());
        }
        std::ptr::copy_nonoverlapping(wide.as_ptr(), ptr, wide.len());
        let _ = GlobalUnlock(hmem);
        let res = SetClipboardData(CF_UNICODETEXT, Some(HANDLE(hmem.0)));
        let _ = CloseClipboard();
        if res.is_err() {
            return Err("SetClipboardData failed".to_string());
        }
    }
    Ok(())
}

pub fn notify_explorer_refresh(path: &str) {
    let wide = to_wide(path);
    unsafe {
        SHChangeNotify(
            SHCNE_UPDATEDIR,
            SHCNF_PATHW,
            Some(wide.as_ptr() as *const _),
            None,
        );
    }
}

fn reg_set_str(subkey: &str, name: Option<&str>, val: &str) -> Result<(), String> {
    let subkey_w = to_wide(subkey);
    let mut hkey = HKEY::default();
    let status = unsafe {
        RegCreateKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(subkey_w.as_ptr()),
            Some(0),
            None,
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            None,
            &mut hkey,
            None,
        )
    };
    if status.is_err() {
        return Err(format!("RegCreateKeyExW({subkey}) failed: {status:?}"));
    }
    let val_w = to_wide(val);
    let bytes = unsafe {
        std::slice::from_raw_parts(val_w.as_ptr() as *const u8, val_w.len() * 2)
    };
    let name_w = name.map(to_wide);
    let name_ptr = name_w
        .as_ref()
        .map(|w| PCWSTR(w.as_ptr()))
        .unwrap_or(PCWSTR::null());
    let set_status = unsafe { RegSetValueExW(hkey, name_ptr, Some(0), REG_SZ, Some(bytes)) };
    unsafe {
        let _ = RegCloseKey(hkey);
    }
    if set_status.is_err() {
        return Err(format!("RegSetValueExW({subkey}) failed: {set_status:?}"));
    }
    Ok(())
}

fn reg_delete_tree(subkey: &str) {
    let subkey_w = to_wide(subkey);
    unsafe {
        let _ = RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(subkey_w.as_ptr()));
    }
}

const CONTEXT_ITEMS: &[(&str, &str, &str)] = &[
    ("01_presign_1h", "Copy Presigned URL (1 hour)", "presign_1h"),
    ("02_presign_24h", "Copy Presigned URL (24 hours)", "presign_24h"),
    ("03_presign_7d", "Copy Presigned URL (7 days)", "presign_7d"),
    ("04_s3_uri", "Copy S3 URI (s3://...)", "copy_s3_uri"),
    ("05_https_url", "Copy HTTPS URL", "copy_https_url"),
    ("06_purge_cache", "Purge Cache / Force Refresh", "purge_cache"),
    ("07_versions", "Versions & Trash Recovery...", "open_versions"),
];

pub fn is_context_menu_registered() -> bool {
    let subkey_w = to_wide(r"Software\Classes\*\shell\VPS3Drive");
    let mut hkey = HKEY::default();
    let status = unsafe {
        RegOpenKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(subkey_w.as_ptr()),
            Some(0),
            KEY_READ,
            &mut hkey,
        )
    };
    if status.is_ok() {
        unsafe {
            let _ = RegCloseKey(hkey);
        }
        true
    } else {
        false
    }
}

pub fn register_context_menu(exe_path: &str) -> Result<(), String> {
    for target in ["*", "Directory"] {
        let root = format!(r"Software\Classes\{target}\shell\VPS3Drive");
        reg_set_str(&root, Some("MUIVerb"), "VPS3Drive")?;
        reg_set_str(&root, Some("SubCommands"), "")?;
        reg_set_str(&root, Some("Icon"), &format!("\"{exe_path}\",0"))?;

        for (item_key, label, action) in CONTEXT_ITEMS {
            // Directory doesn't need presigned URL for a folder, but purge_cache, copy_s3_uri, open_versions are useful.
            if target == "Directory" && action.starts_with("presign_") {
                continue;
            }
            let item_root = format!(r"{root}\shell\{item_key}");
            reg_set_str(&item_root, Some("MUIVerb"), label)?;
            let cmd = format!("\"{exe_path}\" --ctx {action} \"%1\"");
            reg_set_str(&format!(r"{item_root}\command"), None, &cmd)?;
        }
    }
    Ok(())
}

pub fn unregister_context_menu() -> Result<(), String> {
    reg_delete_tree(r"Software\Classes\*\shell\VPS3Drive");
    reg_delete_tree(r"Software\Classes\Directory\shell\VPS3Drive");
    Ok(())
}

pub fn send_ctx_command(action: &str, path: &str) -> Result<(), String> {
    let port_str = std::fs::read_to_string(ipc_port_path())
        .map_err(|_| "VPS3Drive is not running".to_string())?;
    let port: u16 = port_str
        .trim()
        .parse()
        .map_err(|_| "Invalid IPC port".to_string())?;
    let mut stream = TcpStream::connect(("127.0.0.1", port))
        .map_err(|e| format!("Connect failed: {e}"))?;
    let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
    let _ = stream.set_write_timeout(Some(Duration::from_secs(5)));

    let payload = serde_json::json!({
        "action": action,
        "path": path,
    });
    writeln!(stream, "{payload}").map_err(|e| format!("Write IPC failed: {e}"))?;
    let _ = stream.flush();

    let mut reader = BufReader::new(stream);
    let mut line = String::new();
    if reader.read_line(&mut line).is_ok() && !line.trim().is_empty() {
        if let Ok(val) = serde_json::from_str::<serde_json::Value>(&line) {
            if let Some(clip) = val.get("clipboard").and_then(|v| v.as_str()) {
                let _ = copy_to_clipboard(clip);
            }
        }
    }
    Ok(())
}
