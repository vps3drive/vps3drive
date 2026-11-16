#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod cache;
mod fs;
mod s3;
mod shell;

use crate::cache::{DiskCache, TransferItem, TransferTracker, WriteBackQueue};
use crate::fs::S3FileSystem;
use crate::s3::{now_filetime, S3Client, S3Config, S3Entry, S3VersionEntry};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;
use std::process::Command;
use std::sync::mpsc::{self, Sender};
use std::sync::Arc;
use std::thread::JoinHandle;
use tao::event::{Event, WindowEvent};
use tao::event_loop::{ControlFlow, EventLoopBuilder};
use tao::window::WindowBuilder;
use tray_icon::menu::{Menu, MenuEvent, MenuItem};
use tray_icon::{Icon, MouseButton, TrayIconBuilder, TrayIconEvent};
use winfsp::host::{FileSystemHost, VolumeParams};
use wry::http::Response;
use wry::{WebContext, WebViewBuilder};

const HTML_UI: &str = include_str!("ui.html");
const LOGO_PNG: &[u8] = include_bytes!("../assets/vps3drive-logo.png");

fn default_true() -> bool {
    true
}
fn default_mount_type() -> String {
    "network".to_string()
}
fn default_cache_ttl() -> u64 {
    30
}
fn default_part_mb() -> usize {
    8
}

#[derive(Clone, Serialize, Deserialize)]
struct StorageAccount {
    id: String,
    name: String,
    #[serde(default)]
    account_type: String,
    endpoint: String,
    region: String,
    access_key: String,
    secret_key: String,
    path_style: bool,
    #[serde(default = "default_true")]
    use_ssl: bool,
}

fn default_trash_prefix() -> String {
    ".trash".to_string()
}

#[derive(Clone, Serialize, Deserialize)]
struct MappedDrive {
    id: String,
    account_id: String,
    bucket: String,
    #[serde(default)]
    prefix: String,
    drive_letter: String,
    volume_label: String,
    #[serde(default = "default_mount_type")]
    mount_type: String,
    #[serde(default = "default_cache_ttl")]
    cache_ttl_secs: u64,
    read_only: bool,
    auto_mount: bool,
    #[serde(default)]
    soft_delete: bool,
    #[serde(default = "default_trash_prefix")]
    trash_prefix: String,
}

#[derive(Clone, Serialize, Deserialize)]
struct CustomHeader {
    name: String,
    value: String,
}

fn default_proxy_mode() -> String { "SystemProxy".to_string() }
fn default_proxy_port() -> u16 { 8080 }
fn default_1024() -> u32 { 1024 }
fn default_2048() -> u32 { 2048 }
fn default_4() -> u32 { 4 }
fn default_3() -> u32 { 3 }
fn default_log_level() -> String { "Info".to_string() }
fn default_14() -> u32 { 14 }
fn default_60() -> u64 { 60 }
fn default_256() -> u32 { 256 }

fn default_2048_u64() -> u64 { 2048 }
fn default_2_u64() -> u64 { 2 }

#[derive(Clone, Serialize, Deserialize)]
struct PersistedState {
    accounts: Vec<StorageAccount>,
    drives: Vec<MappedDrive>,
    #[serde(default = "default_true")]
    start_minimized_on_windows_startup: bool,
    auto_mount_on_startup: bool,
    #[serde(default)]
    minimize_to_tray: bool,
    #[serde(default = "default_true")]
    check_updates_on_startup: bool,
    #[serde(default = "default_part_mb")]
    multipart_part_size_mb: usize,
    #[serde(default)]
    custom_headers: Vec<CustomHeader>,
    // Performance & I/O
    #[serde(default = "default_true")]
    disk_cache_enabled: bool,
    #[serde(default = "default_2048_u64")]
    disk_cache_mb: u64,
    #[serde(default = "default_true")]
    write_back_enabled: bool,
    #[serde(default = "default_2_u64")]
    prefetch_chunks: u64,
    // Explorer integration
    #[serde(default = "default_true")]
    context_menu_enabled: bool,
    // Proxy
    #[serde(default = "default_proxy_mode")]
    proxy_mode: String,
    #[serde(default)]
    proxy_server: String,
    #[serde(default = "default_proxy_port")]
    proxy_port: u16,
    #[serde(default)]
    proxy_requires_auth: bool,
    #[serde(default)]
    proxy_username: String,
    // Bandwidth
    #[serde(default)]
    throttle_uploads: bool,
    #[serde(default = "default_1024")]
    max_upload_kbps: u32,
    #[serde(default)]
    throttle_downloads: bool,
    #[serde(default = "default_2048")]
    max_download_kbps: u32,
    // Data Integrity
    #[serde(default = "default_true")]
    verify_upload_integrity: bool,
    #[serde(default = "default_true")]
    verify_download_integrity: bool,
    // Queueing
    #[serde(default = "default_4")]
    max_concurrent_uploads: u32,
    #[serde(default = "default_4")]
    max_concurrent_downloads: u32,
    #[serde(default = "default_3")]
    retry_count: u32,
    // Logging & Advanced
    #[serde(default = "default_log_level")]
    log_level: String,
    #[serde(default = "default_14")]
    log_retention_days: u32,
    #[serde(default = "default_60")]
    request_timeout_secs: u64,
    #[serde(default = "default_256")]
    read_buffer_kb: u32,
}

impl Default for PersistedState {
    fn default() -> Self {
        Self {
            accounts: Vec::new(),
            drives: Vec::new(),
            start_minimized_on_windows_startup: true,
            auto_mount_on_startup: true,
            minimize_to_tray: false,
            check_updates_on_startup: true,
            multipart_part_size_mb: 8,
            custom_headers: Vec::new(),
            disk_cache_enabled: true,
            disk_cache_mb: 2048,
            write_back_enabled: true,
            prefetch_chunks: 2,
            context_menu_enabled: true,
            proxy_mode: default_proxy_mode(),
            proxy_server: String::new(),
            proxy_port: 8080,
            proxy_requires_auth: false,
            proxy_username: String::new(),
            throttle_uploads: false,
            max_upload_kbps: 1024,
            throttle_downloads: false,
            max_download_kbps: 2048,
            verify_upload_integrity: true,
            verify_download_integrity: true,
            max_concurrent_uploads: 4,
            max_concurrent_downloads: 4,
            retry_count: 3,
            log_level: default_log_level(),
            log_retention_days: 14,
            request_timeout_secs: 60,
            read_buffer_kb: 256,
        }
    }
}

#[derive(Serialize)]
struct DriveView {
    id: String,
    account_id: String,
    bucket: String,
    prefix: String,
    drive_letter: String,
    volume_label: String,
    mount_type: String,
    cache_ttl_secs: u64,
    read_only: bool,
    auto_mount: bool,
    soft_delete: bool,
    trash_prefix: String,
    status: String,
}

#[derive(Serialize)]
struct UiStatePayload<'a> {
    accounts: &'a [StorageAccount],
    drives: Vec<DriveView>,
    start_with_windows: bool,
    start_minimized_on_windows_startup: bool,
    auto_mount_on_startup: bool,
    minimize_to_tray: bool,
    check_updates_on_startup: bool,
    multipart_part_size_mb: usize,
    disk_cache_enabled: bool,
    disk_cache_mb: u64,
    write_back_enabled: bool,
    prefetch_chunks: u64,
    disk_cache_bytes: u64,
    disk_cache_chunks: usize,
    context_menu_enabled: bool,
    context_menu_installed: bool,
    transfers: Vec<TransferItem>,
    proxy_mode: &'a str,
    proxy_server: &'a str,
    proxy_port: u16,
    proxy_requires_auth: bool,
    proxy_username: &'a str,
    throttle_uploads: bool,
    max_upload_kbps: u32,
    throttle_downloads: bool,
    max_download_kbps: u32,
    verify_upload_integrity: bool,
    verify_download_integrity: bool,
    max_concurrent_uploads: u32,
    max_concurrent_downloads: u32,
    retry_count: u32,
    log_level: &'a str,
    log_retention_days: u32,
    request_timeout_secs: u64,
    read_buffer_kb: u32,
    custom_headers: &'a [CustomHeader],
    logs: &'a [String],
}

#[derive(Serialize)]
struct UpdateInfo {
    update_available: bool,
    current_version: String,
    latest_version: String,
    download_url: String,
}

struct ActiveMount {
    stop_tx: Sender<()>,
    handle: JoinHandle<()>,
    fs: Arc<S3FileSystem>,
    status: String,
}

#[derive(Serialize)]
struct TrashEntryView {
    key: String,
    display_name: String,
    original_key: String,
    size: u64,
    last_modified_filetime: u64,
}

enum UserEvent {
    IpcMessage(String),
    BucketsLoaded(Result<Vec<String>, String>),
    AccountTested(Result<String, String>),
    PrefixBrowsed(Result<Vec<String>, String>),
    UpdateChecked(bool, Result<UpdateInfo, String>),
    VersionsLoaded(Result<Vec<S3VersionEntry>, String>),
    VersionActionDone(Result<String, String>),
    TrashLoaded(Result<Vec<TrashEntryView>, String>),
    TrashActionDone(Result<String, String>),
    ShellContextCommand {
        action: String,
        path: String,
        reply_tx: Sender<serde_json::Value>,
    },
    MountStarted(String, String),
    MountFailed(String, String),
    MountStopped(String),
    MountAll,
    UnmountAll,
    ShowWindow,
    ExitApp,
}

struct AppController {
    persisted: PersistedState,
    mounts: HashMap<String, ActiveMount>,
    logs: Vec<String>,
    start_with_windows: bool,
    disk_cache: Arc<DiskCache>,
    transfer_tracker: Arc<TransferTracker>,
    write_back_queue: Arc<WriteBackQueue>,
}

impl AppController {
    fn new() -> Self {
        let persisted = load_state();
        let cache_dir = app_data_dir().join("cache");
        let disk_cache = Arc::new(DiskCache::new(
            cache_dir,
            persisted.disk_cache_mb,
            persisted.disk_cache_enabled,
        ));
        let transfer_tracker = Arc::new(TransferTracker::new());
        let write_back_queue = Arc::new(WriteBackQueue::new(
            persisted.max_concurrent_uploads as usize,
            Arc::clone(&transfer_tracker),
        ));
        Self {
            persisted,
            mounts: HashMap::new(),
            logs: vec!["VPS3Drive Rust Engine initialized (WebView2 + Hybrid Cache + Write-Back).".to_string()],
            start_with_windows: is_startup_enabled(),
            disk_cache,
            transfer_tracker,
            write_back_queue,
        }
    }

    fn log(&mut self, msg: impl Into<String>) {
        let now = chrono::Local::now().format("%H:%M:%S");
        self.logs.push(format!("[{now}] {}", msg.into()));
        if self.logs.len() > 200 {
            self.logs.remove(0);
        }
    }

    fn save(&self) {
        let _ = save_state(&self.persisted);
    }

    fn state_script(&self) -> String {
        let drives = self
            .persisted
            .drives
            .iter()
            .map(|d| {
                let status = self
                    .mounts
                    .get(&d.id)
                    .map(|m| m.status.clone())
                    .unwrap_or_else(|| "Stopped".to_string());
                DriveView {
                    id: d.id.clone(),
                    account_id: d.account_id.clone(),
                    bucket: d.bucket.clone(),
                    prefix: d.prefix.clone(),
                    drive_letter: d.drive_letter.clone(),
                    volume_label: d.volume_label.clone(),
                    mount_type: d.mount_type.clone(),
                    cache_ttl_secs: d.cache_ttl_secs,
                    read_only: d.read_only,
                    auto_mount: d.auto_mount,
                    soft_delete: d.soft_delete,
                    trash_prefix: d.trash_prefix.clone(),
                    status,
                }
            })
            .collect();

        let (disk_cache_bytes, disk_cache_chunks) = self.disk_cache.stats();
        let payload = UiStatePayload {
            accounts: &self.persisted.accounts,
            drives,
            start_with_windows: self.start_with_windows,
            start_minimized_on_windows_startup: self.persisted.start_minimized_on_windows_startup,
            auto_mount_on_startup: self.persisted.auto_mount_on_startup,
            minimize_to_tray: self.persisted.minimize_to_tray,
            check_updates_on_startup: self.persisted.check_updates_on_startup,
            multipart_part_size_mb: self.persisted.multipart_part_size_mb,
            disk_cache_enabled: self.persisted.disk_cache_enabled,
            disk_cache_mb: self.persisted.disk_cache_mb,
            write_back_enabled: self.persisted.write_back_enabled,
            prefetch_chunks: self.persisted.prefetch_chunks,
            disk_cache_bytes,
            disk_cache_chunks,
            context_menu_enabled: self.persisted.context_menu_enabled,
            context_menu_installed: shell::is_context_menu_registered(),
            transfers: self.transfer_tracker.snapshot(),
            proxy_mode: &self.persisted.proxy_mode,
            proxy_server: &self.persisted.proxy_server,
            proxy_port: self.persisted.proxy_port,
            proxy_requires_auth: self.persisted.proxy_requires_auth,
            proxy_username: &self.persisted.proxy_username,
            throttle_uploads: self.persisted.throttle_uploads,
            max_upload_kbps: self.persisted.max_upload_kbps,
            throttle_downloads: self.persisted.throttle_downloads,
            max_download_kbps: self.persisted.max_download_kbps,
            verify_upload_integrity: self.persisted.verify_upload_integrity,
            verify_download_integrity: self.persisted.verify_download_integrity,
            max_concurrent_uploads: self.persisted.max_concurrent_uploads,
            max_concurrent_downloads: self.persisted.max_concurrent_downloads,
            retry_count: self.persisted.retry_count,
            log_level: &self.persisted.log_level,
            log_retention_days: self.persisted.log_retention_days,
            request_timeout_secs: self.persisted.request_timeout_secs,
            read_buffer_kb: self.persisted.read_buffer_kb,
            custom_headers: &self.persisted.custom_headers,
            logs: &self.logs,
        };
        let json = serde_json::to_string(&payload).unwrap_or_else(|_| "{}".to_string());
        format!("window.onAppState({json});")
    }

    fn build_s3_config(&self, acct: &StorageAccount) -> S3Config {
        let custom_headers = self
            .persisted
            .custom_headers
            .iter()
            .map(|h| (h.name.clone(), h.value.clone()))
            .collect();
        let no_proxy = self.persisted.proxy_mode == "NoProxy";
        let proxy_url = if self.persisted.proxy_mode == "ManualProxy" && !self.persisted.proxy_server.trim().is_empty() {
            let host = self.persisted.proxy_server.trim();
            if host.contains("://") {
                Some(format!("{host}:{}", self.persisted.proxy_port))
            } else {
                Some(format!("http://{host}:{}", self.persisted.proxy_port))
            }
        } else {
            None
        };
        S3Config {
            endpoint: acct.endpoint.clone(),
            region: acct.region.clone(),
            access_key: acct.access_key.clone(),
            secret_key: acct.secret_key.clone(),
            path_style: acct.path_style,
            custom_headers,
            timeout_secs: self.persisted.request_timeout_secs,
            no_proxy,
            proxy_url,
        }
    }

    fn mount_drive(&mut self, id: &str, proxy: tao::event_loop::EventLoopProxy<UserEvent>) {
        if self.mounts.contains_key(id) {
            return;
        }
        let Some(drive) = self.persisted.drives.iter().find(|d| d.id == id).cloned() else {
            return;
        };
        let Some(acct) = self
            .persisted
            .accounts
            .iter()
            .find(|a| a.id == drive.account_id)
            .cloned()
        else {
            self.log("Cannot mount: account not found.");
            return;
        };

        let drive_letter = if drive.drive_letter.ends_with(':') {
            drive.drive_letter.clone()
        } else {
            format!("{}:", drive.drive_letter)
        };
        let label = if drive.volume_label.trim().is_empty() {
            format!("S3:{}", drive.bucket)
        } else {
            drive.volume_label.clone()
        };

        let cfg = self.build_s3_config(&acct);
        let client = match S3Client::new(cfg) {
            Ok(c) => Arc::new(c),
            Err(e) => {
                self.log(format!("Mount failed: {e}"));
                return;
            }
        };

        let wbq = if self.persisted.write_back_enabled {
            Some(Arc::clone(&self.write_back_queue))
        } else {
            None
        };
        let fs_arc = Arc::new(S3FileSystem::new(
            client,
            drive.bucket.clone(),
            drive.prefix.clone(),
            label,
            drive.read_only,
            drive.cache_ttl_secs,
            self.persisted.multipart_part_size_mb,
            self.persisted.max_concurrent_uploads as usize,
            self.persisted.prefetch_chunks,
            drive.soft_delete,
            drive.trash_prefix.clone(),
            Arc::clone(&self.disk_cache),
            wbq,
        ));
        let fs_ref = Arc::clone(&fs_arc);
        let (stop_tx, stop_rx) = mpsc::channel::<()>();
        let drive_id = drive.id.clone();
        let dl_clone = drive_letter.clone();
        let bucket_clone = drive.bucket.clone();

        let handle = std::thread::spawn(move || {
            let mut params = VolumeParams::new();
            params
                .sector_size(4096)
                .sectors_per_allocation_unit(1)
                .volume_creation_time(now_filetime())
                .volume_serial_number(0x5650_5333)
                .filesystem_name("VPS3Drive")
                .file_info_timeout(5000)
                .case_sensitive_search(false)
                .case_preserved_names(true)
                .unicode_on_disk(true)
                .persistent_acls(false)
                .read_only_volume(drive.read_only);

            let mut host = match FileSystemHost::new(params, SharedFs(fs_ref)) {
                Ok(h) => h,
                Err(e) => {
                    let _ = proxy.send_event(UserEvent::MountFailed(
                        drive_id,
                        format!("WinFsp init failed: {e:?}"),
                    ));
                    return;
                }
            };

            if let Err(e) = host.mount(dl_clone.as_str()) {
                let _ = proxy.send_event(UserEvent::MountFailed(
                    drive_id,
                    format!("Mount {dl_clone} failed: {e:?}"),
                ));
                return;
            }
            if let Err(e) = host.start() {
                let _ = proxy.send_event(UserEvent::MountFailed(
                    drive_id,
                    format!("Dispatcher start failed: {e:?}"),
                ));
                return;
            }

            let _ = proxy.send_event(UserEvent::MountStarted(
                drive_id.clone(),
                format!("Mounted {bucket_clone} on {dl_clone}"),
            ));

            let _ = stop_rx.recv();
            host.unmount();
            host.stop();
            drop(host);
            let _ = proxy.send_event(UserEvent::MountStopped(format!("Unmounted {dl_clone}")));
        });

        self.mounts.insert(
            drive.id,
            ActiveMount {
                stop_tx,
                handle,
                fs: fs_arc,
                status: "Mounting".to_string(),
            },
        );
        self.log(format!("Mounting {} on {}...", drive.bucket, drive_letter));
    }

    fn unmount_drive(&mut self, id: &str) {
        if let Some(m) = self.mounts.remove(id) {
            let _ = m.stop_tx.send(());
            let _ = m.handle.join();
        }
    }

    fn unmount_all(&mut self) {
        let ids: Vec<String> = self.mounts.keys().cloned().collect();
        for id in ids {
            self.unmount_drive(&id);
        }
    }

    fn resolve_mount_and_key(&self, win_path: &str) -> Option<(String, Arc<S3FileSystem>, String)> {
        let norm = win_path.trim().replace('/', "\\");
        let drive_prefix = norm.get(0..2)?;
        if !drive_prefix.ends_with(':') {
            return None;
        }
        let drive = self.persisted.drives.iter().find(|d| {
            let dl = d.drive_letter.trim_end_matches(':');
            format!("{dl}:").eq_ignore_ascii_case(drive_prefix)
        })?;
        let mount = self.mounts.get(&drive.id)?;
        let rel = norm.get(2..).unwrap_or("");
        let s3_key = mount.fs.resolve_s3_key_from_relative(rel);
        Some((drive.id.clone(), Arc::clone(&mount.fs), s3_key))
    }

    fn handle_shell_context(
        &mut self,
        action: &str,
        path: &str,
        webview: &wry::WebView,
        window: &tao::window::Window,
    ) -> serde_json::Value {
        let Some((drive_id, fs, s3_key)) = self.resolve_mount_and_key(path) else {
            self.log(format!("Context action '{action}': path is not on a mounted VPS3Drive ({path})."));
            return serde_json::json!({ "ok": false, "error": "Not a mounted VPS3Drive path" });
        };

        let bucket = fs.bucket().to_string();
        match action {
            "presign_1h" | "presign_24h" | "presign_7d" => {
                let secs = match action {
                    "presign_1h" => 3600,
                    "presign_24h" => 86_400,
                    _ => 604_800,
                };
                match fs.client().generate_presigned_url(&bucket, &s3_key, secs) {
                    Ok(url) => {
                        let _ = shell::copy_to_clipboard(&url);
                        self.log(format!("Copied Presigned URL ({action}) for s3://{bucket}/{s3_key}"));
                        let _ = webview.evaluate_script(&self.state_script());
                        serde_json::json!({ "ok": true, "clipboard": url })
                    }
                    Err(e) => {
                        self.log(format!("Presign failed: {e}"));
                        serde_json::json!({ "ok": false, "error": e })
                    }
                }
            }
            "copy_s3_uri" => {
                let uri = format!("s3://{bucket}/{s3_key}");
                let _ = shell::copy_to_clipboard(&uri);
                self.log(format!("Copied S3 URI: {uri}"));
                let _ = webview.evaluate_script(&self.state_script());
                serde_json::json!({ "ok": true, "clipboard": uri })
            }
            "copy_https_url" => {
                let url = fs.client().public_https_url(&bucket, &s3_key);
                let _ = shell::copy_to_clipboard(&url);
                self.log(format!("Copied HTTPS URL: {url}"));
                let _ = webview.evaluate_script(&self.state_script());
                serde_json::json!({ "ok": true, "clipboard": url })
            }
            "purge_cache" => {
                fs.purge_path_cache(&s3_key);
                shell::notify_explorer_refresh(path);
                self.log(format!("Purged cache & refreshed Explorer for {s3_key}"));
                let _ = webview.evaluate_script(&self.state_script());
                serde_json::json!({ "ok": true })
            }
            "open_versions" => {
                window.set_visible(true);
                window.set_focus();
                let d_json = serde_json::to_string(&drive_id).unwrap_or_else(|_| "\"\"".to_string());
                let k_json = serde_json::to_string(&s3_key).unwrap_or_else(|_| "\"\"".to_string());
                let _ = webview.evaluate_script(&format!(
                    "window.openVersionsForKey && window.openVersionsForKey({d_json}, {k_json});"
                ));
                serde_json::json!({ "ok": true })
            }
            _ => serde_json::json!({ "ok": false, "error": "Unknown action" }),
        }
    }
}

struct SharedFs(Arc<S3FileSystem>);

impl winfsp::filesystem::FileSystemContext for SharedFs {
    type FileContext = <S3FileSystem as winfsp::filesystem::FileSystemContext>::FileContext;

    fn get_volume_info(&self, out_volume_info: &mut winfsp::filesystem::VolumeInfo) -> winfsp::Result<()> {
        self.0.get_volume_info(out_volume_info)
    }

    fn get_security_by_name(
        &self,
        file_name: &winfsp::U16CStr,
        security_descriptor: Option<&mut [std::ffi::c_void]>,
        reparse_point_resolver: impl FnOnce(&winfsp::U16CStr) -> Option<winfsp::filesystem::FileSecurity>,
    ) -> winfsp::Result<winfsp::filesystem::FileSecurity> {
        self.0
            .get_security_by_name(file_name, security_descriptor, reparse_point_resolver)
    }

    fn open(
        &self,
        file_name: &winfsp::U16CStr,
        create_options: u32,
        granted_access: winfsp_sys::FILE_ACCESS_RIGHTS,
        file_info: &mut winfsp::filesystem::OpenFileInfo,
    ) -> winfsp::Result<Self::FileContext> {
        self.0.open(file_name, create_options, granted_access, file_info)
    }

    fn close(&self, context: Self::FileContext) {
        self.0.close(context)
    }

    fn create(
        &self,
        file_name: &winfsp::U16CStr,
        create_options: u32,
        granted_access: winfsp_sys::FILE_ACCESS_RIGHTS,
        file_attributes: winfsp_sys::FILE_FLAGS_AND_ATTRIBUTES,
        security_descriptor: Option<&[std::ffi::c_void]>,
        allocation_size: u64,
        extra_buffer: Option<&[u8]>,
        extra_buffer_is_reparse_point: bool,
        file_info: &mut winfsp::filesystem::OpenFileInfo,
    ) -> winfsp::Result<Self::FileContext> {
        self.0.create(
            file_name,
            create_options,
            granted_access,
            file_attributes,
            security_descriptor,
            allocation_size,
            extra_buffer,
            extra_buffer_is_reparse_point,
            file_info,
        )
    }

    fn cleanup(&self, context: &Self::FileContext, file_name: Option<&winfsp::U16CStr>, flags: u32) {
        self.0.cleanup(context, file_name, flags)
    }

    fn flush(
        &self,
        context: Option<&Self::FileContext>,
        file_info: &mut winfsp::filesystem::FileInfo,
    ) -> winfsp::Result<()> {
        self.0.flush(context, file_info)
    }

    fn get_file_info(
        &self,
        context: &Self::FileContext,
        file_info: &mut winfsp::filesystem::FileInfo,
    ) -> winfsp::Result<()> {
        self.0.get_file_info(context, file_info)
    }

    fn get_security(
        &self,
        context: &Self::FileContext,
        security_descriptor: Option<&mut [std::ffi::c_void]>,
    ) -> winfsp::Result<u64> {
        self.0.get_security(context, security_descriptor)
    }

    fn set_security(
        &self,
        context: &Self::FileContext,
        security_information: u32,
        modification_descriptor: winfsp::filesystem::ModificationDescriptor,
    ) -> winfsp::Result<()> {
        self.0
            .set_security(context, security_information, modification_descriptor)
    }

    fn set_basic_info(
        &self,
        context: &Self::FileContext,
        file_attributes: u32,
        creation_time: u64,
        last_access_time: u64,
        last_write_time: u64,
        last_change_time: u64,
        file_info: &mut winfsp::filesystem::FileInfo,
    ) -> winfsp::Result<()> {
        self.0.set_basic_info(
            context,
            file_attributes,
            creation_time,
            last_access_time,
            last_write_time,
            last_change_time,
            file_info,
        )
    }

    fn overwrite(
        &self,
        context: &Self::FileContext,
        file_attributes: winfsp_sys::FILE_FLAGS_AND_ATTRIBUTES,
        replace_file_attributes: bool,
        allocation_size: u64,
        extra_buffer: Option<&[u8]>,
        file_info: &mut winfsp::filesystem::FileInfo,
    ) -> winfsp::Result<()> {
        self.0.overwrite(
            context,
            file_attributes,
            replace_file_attributes,
            allocation_size,
            extra_buffer,
            file_info,
        )
    }

    fn read_directory(
        &self,
        context: &Self::FileContext,
        pattern: Option<&winfsp::U16CStr>,
        marker: winfsp::filesystem::DirMarker,
        buffer: &mut [u8],
    ) -> winfsp::Result<u32> {
        self.0.read_directory(context, pattern, marker, buffer)
    }

    fn rename(
        &self,
        context: &Self::FileContext,
        file_name: &winfsp::U16CStr,
        new_file_name: &winfsp::U16CStr,
        replace_if_exists: bool,
    ) -> winfsp::Result<()> {
        self.0.rename(context, file_name, new_file_name, replace_if_exists)
    }

    fn set_delete(
        &self,
        context: &Self::FileContext,
        file_name: &winfsp::U16CStr,
        delete_file: bool,
    ) -> winfsp::Result<()> {
        self.0.set_delete(context, file_name, delete_file)
    }

    fn set_file_size(
        &self,
        context: &Self::FileContext,
        new_size: u64,
        set_allocation_size: bool,
        file_info: &mut winfsp::filesystem::FileInfo,
    ) -> winfsp::Result<()> {
        self.0
            .set_file_size(context, new_size, set_allocation_size, file_info)
    }

    fn read(
        &self,
        context: &Self::FileContext,
        buffer: &mut [u8],
        offset: u64,
    ) -> winfsp::Result<u32> {
        self.0.read(context, buffer, offset)
    }

    fn write(
        &self,
        context: &Self::FileContext,
        buffer: &[u8],
        offset: u64,
        write_to_eof: bool,
        constrained_io: bool,
        file_info: &mut winfsp::filesystem::FileInfo,
    ) -> winfsp::Result<u32> {
        self.0
            .write(context, buffer, offset, write_to_eof, constrained_io, file_info)
    }
}

fn new_id() -> String {
    format!("{:x}", now_filetime())
}

fn is_startup_enabled() -> bool {
    Command::new("reg")
        .args([
            "query",
            r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            "/v",
            "VPS3DriveRust",
        ])
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
}

fn set_startup_enabled(enable: bool, minimized: bool) {
    if enable {
        if let Ok(exe) = std::env::current_exe() {
            let val = if minimized {
                format!("\"{}\" --minimized", exe.display())
            } else {
                format!("\"{}\"", exe.display())
            };
            let _ = Command::new("reg")
                .args([
                    "add",
                    r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
                    "/v",
                    "VPS3DriveRust",
                    "/t",
                    "REG_SZ",
                    "/d",
                    &val,
                    "/f",
                ])
                .output();
        }
    } else {
        let _ = Command::new("reg")
            .args([
                "delete",
                r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
                "/v",
                "VPS3DriveRust",
                "/f",
            ])
            .output();
    }
}

fn app_data_dir() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join("VPS3Drive-Rust")
}

fn log_debug(msg: &str) {
    use std::io::Write;
    let dir = app_data_dir();
    let _ = std::fs::create_dir_all(&dir);
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(dir.join("app.log"))
    {
        let _ = writeln!(f, "[{}] {}", chrono::Local::now().format("%H:%M:%S"), msg);
    }
}

fn state_path() -> PathBuf {
    app_data_dir().join("state.json")
}

fn load_state() -> PersistedState {
    let path = state_path();
    std::fs::read_to_string(path)
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

fn save_state(state: &PersistedState) -> Result<(), String> {
    let path = state_path();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let json = serde_json::to_string_pretty(state).map_err(|e| e.to_string())?;
    std::fs::write(path, json).map_err(|e| e.to_string())
}

fn load_logo_rgba() -> (Vec<u8>, u32, u32) {
    let img = image::load_from_memory(LOGO_PNG)
        .expect("decode logo png")
        .resize_exact(64, 64, image::imageops::FilterType::Lanczos3)
        .into_rgba8();
    let (w, h) = img.dimensions();
    (img.into_raw(), w, h)
}

fn parse_semver3(s: &str) -> (u32, u32, u32) {
    let clean = s.trim().trim_start_matches(['v', 'V']);
    let mut parts = clean.split('.').map(|p| p.parse::<u32>().unwrap_or(0));
    (
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
        parts.next().unwrap_or(0),
    )
}

fn check_github_updates() -> Result<UpdateInfo, String> {
    let current = env!("CARGO_PKG_VERSION").to_string();
    let client = reqwest::blocking::Client::builder()
        .timeout(std::time::Duration::from_secs(10))
        .build()
        .map_err(|e| e.to_string())?;
    let resp = client
        .get("https://api.github.com/repos/vps3drive/vps3drive/releases/latest")
        .header("User-Agent", format!("VPS3Drive-Rust/{current}"))
        .header("Accept", "application/vnd.github+json")
        .send()
        .map_err(|e| format!("Update check failed: {e}"))?;
    if !resp.status().is_success() {
        return Err(format!("GitHub API returned {}", resp.status()));
    }
    let body = resp.text().map_err(|e| e.to_string())?;
    let json: serde_json::Value = serde_json::from_str(&body).map_err(|e| e.to_string())?;
    let tag = json
        .get("tag_name")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    let html_url = json
        .get("html_url")
        .and_then(|v| v.as_str())
        .unwrap_or("https://github.com/vps3drive/vps3drive/releases")
        .to_string();
    let mut dl_url = None;
    if let Some(assets) = json.get("assets").and_then(|v| v.as_array()) {
        for a in assets {
            if let Some(u) = a.get("browser_download_url").and_then(|v| v.as_str()) {
                if u.to_ascii_lowercase().contains("rust") && u.to_ascii_lowercase().ends_with(".exe") {
                    dl_url = Some(u.to_string());
                    break;
                }
            }
        }
    }
    let update_available = !tag.is_empty() && parse_semver3(&tag) > parse_semver3(&current);
    Ok(UpdateInfo {
        update_available,
        current_version: current,
        latest_version: tag,
        download_url: dl_url.unwrap_or(html_url),
    })
}

fn create_tray_icon() -> Icon {
    let (rgba, w, h) = load_logo_rgba();
    Icon::from_rgba(rgba, w, h).expect("tray icon")
}

fn create_window_icon() -> Option<tao::window::Icon> {
    let (rgba, w, h) = load_logo_rgba();
    tao::window::Icon::from_rgba(rgba, w, h).ok()
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() >= 4 && args[1] == "--ctx" {
        let action = &args[2];
        let path = &args[3];
        let _ = shell::send_ctx_command(action, path);
        return;
    }

    log_debug("Starting VPS3Drive (Rust)");
    let event_loop = EventLoopBuilder::<UserEvent>::with_user_event().build();
    let proxy = event_loop.create_proxy();

    // Start local TCP IPC server for Explorer Context Menu commands
    {
        let ipc_proxy = proxy.clone();
        if let Ok(listener) = std::net::TcpListener::bind("127.0.0.1:0") {
            if let Ok(addr) = listener.local_addr() {
                shell::write_ipc_port(addr.port());
            }
            std::thread::spawn(move || {
                use std::io::{BufRead, BufReader, Write};
                for stream in listener.incoming().flatten() {
                    let p = ipc_proxy.clone();
                    std::thread::spawn(move || {
                        let mut reader = BufReader::new(stream);
                        let mut line = String::new();
                        if reader.read_line(&mut line).is_ok() {
                            if let Ok(val) = serde_json::from_str::<serde_json::Value>(&line) {
                                let action = val.get("action").and_then(|v| v.as_str()).unwrap_or("").to_string();
                                let path = val.get("path").and_then(|v| v.as_str()).unwrap_or("").to_string();
                                let (tx, rx) = mpsc::channel();
                                if p.send_event(UserEvent::ShellContextCommand { action, path, reply_tx: tx }).is_ok() {
                                    if let Ok(reply) = rx.recv_timeout(std::time::Duration::from_secs(5)) {
                                        let mut s = reader.into_inner();
                                        let _ = writeln!(s, "{reply}");
                                        let _ = s.flush();
                                    }
                                }
                            }
                        }
                    });
                }
            });
        }
    }

    let start_minimized = std::env::args().any(|a| a.eq_ignore_ascii_case("--minimized"));
    let mut wb = WindowBuilder::new()
        .with_title("VPS3Drive")
        .with_visible(!start_minimized)
        .with_inner_size(tao::dpi::LogicalSize::new(1020.0, 680.0))
        .with_min_inner_size(tao::dpi::LogicalSize::new(780.0, 520.0));
    if let Some(icon) = create_window_icon() {
        wb = wb.with_window_icon(Some(icon));
    }
    let window = wb.build(&event_loop).expect("Failed to create window");
    log_debug("Window created");

    let webview_dir = app_data_dir().join("webview2");
    let _ = std::fs::create_dir_all(&webview_dir);
    let mut web_context = WebContext::new(Some(webview_dir));

    let ipc_proxy = proxy.clone();
    let webview = WebViewBuilder::with_web_context(&mut web_context)
        .with_custom_protocol("app".into(), |_id, req| {
            if req.uri().path() == "/logo.png" {
                Response::builder()
                    .header("Content-Type", "image/png")
                    .body(LOGO_PNG.to_vec().into())
                    .unwrap()
            } else {
                Response::builder()
                    .header("Content-Type", "text/html; charset=utf-8")
                    .body(HTML_UI.as_bytes().to_vec().into())
                    .unwrap()
            }
        })
        .with_url("http://app.localhost/")
        .with_background_color((243, 243, 243, 255))
        .with_ipc_handler(move |req| {
            let body = req.body().to_string();
            log_debug(&format!("IPC recv: {body}"));
            let _ = ipc_proxy.send_event(UserEvent::IpcMessage(body));
        })
        .build(&window)
        .expect("Failed to create WebView2");
    log_debug("WebView2 created");

    let menu = Menu::new();
    let open_item = MenuItem::new("Open VPS3Drive", true, None);
    let mount_all_item = MenuItem::new("Mount All Drives", true, None);
    let unmount_all_item = MenuItem::new("Unmount All Drives", true, None);
    let exit_item = MenuItem::new("Exit", true, None);
    let open_id = open_item.id().clone();
    let mount_all_id = mount_all_item.id().clone();
    let unmount_all_id = unmount_all_item.id().clone();
    let exit_id = exit_item.id().clone();
    let _ = menu.append(&open_item);
    let _ = menu.append(&mount_all_item);
    let _ = menu.append(&unmount_all_item);
    let _ = menu.append(&exit_item);

    let _tray = TrayIconBuilder::new()
        .with_menu(Box::new(menu))
        .with_tooltip("VPS3Drive (Rust)")
        .with_icon(create_tray_icon())
        .build()
        .ok();

    let menu_proxy = proxy.clone();
    std::thread::spawn(move || {
        while let Ok(ev) = MenuEvent::receiver().recv() {
            if ev.id == open_id {
                let _ = menu_proxy.send_event(UserEvent::ShowWindow);
            } else if ev.id == mount_all_id {
                let _ = menu_proxy.send_event(UserEvent::MountAll);
            } else if ev.id == unmount_all_id {
                let _ = menu_proxy.send_event(UserEvent::UnmountAll);
            } else if ev.id == exit_id {
                let _ = menu_proxy.send_event(UserEvent::ExitApp);
                break;
            }
        }
    });

    let tray_proxy = proxy.clone();
    std::thread::spawn(move || {
        while let Ok(ev) = TrayIconEvent::receiver().recv() {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                ..
            }
            | TrayIconEvent::DoubleClick {
                button: MouseButton::Left,
                ..
            } = ev
            {
                let _ = tray_proxy.send_event(UserEvent::ShowWindow);
            }
        }
    });

    let mut ctrl = AppController::new();
    if ctrl.persisted.context_menu_enabled {
        if let Ok(exe) = std::env::current_exe() {
            let _ = shell::register_context_menu(&exe.to_string_lossy());
        }
    }
    if ctrl.persisted.auto_mount_on_startup {
        let auto_ids: Vec<String> = ctrl
            .persisted
            .drives
            .iter()
            .filter(|d| d.auto_mount)
            .map(|d| d.id.clone())
            .collect();
        for id in auto_ids {
            ctrl.mount_drive(&id, proxy.clone());
        }
    }

    event_loop.run(move |event, _, control_flow| {
        *control_flow = ControlFlow::Wait;

        match event {
            Event::WindowEvent {
                event: WindowEvent::CloseRequested,
                ..
            } => {
                if ctrl.persisted.minimize_to_tray && !ctrl.mounts.is_empty() {
                    log_debug("CloseRequested -> hiding to tray");
                    window.set_visible(false);
                } else {
                    log_debug("CloseRequested -> exiting");
                    ctrl.unmount_all();
                    *control_flow = ControlFlow::Exit;
                }
            }
            Event::UserEvent(ue) => match ue {
                UserEvent::ShowWindow => {
                    window.set_visible(true);
                    window.set_focus();
                }
                UserEvent::MountAll => {
                    let ids: Vec<String> = ctrl.persisted.drives.iter().map(|d| d.id.clone()).collect();
                    for id in ids {
                        ctrl.mount_drive(&id, proxy.clone());
                    }
                    let _ = webview.evaluate_script(&ctrl.state_script());
                }
                UserEvent::UnmountAll => {
                    ctrl.unmount_all();
                    let _ = webview.evaluate_script(&ctrl.state_script());
                }
                UserEvent::ExitApp => {
                    ctrl.unmount_all();
                    *control_flow = ControlFlow::Exit;
                }
                UserEvent::MountStarted(id, msg) => {
                    if let Some(m) = ctrl.mounts.get_mut(&id) {
                        m.status = "Mounted".to_string();
                    }
                    ctrl.log(msg);
                    let _ = webview.evaluate_script(&ctrl.state_script());
                }
                UserEvent::MountFailed(id, err) => {
                    ctrl.mounts.remove(&id);
                    ctrl.log(format!("Error: {err}"));
                    let _ = webview.evaluate_script(&ctrl.state_script());
                }
                UserEvent::MountStopped(msg) => {
                    ctrl.log(msg);
                    let _ = webview.evaluate_script(&ctrl.state_script());
                }
                UserEvent::BucketsLoaded(res) => {
                    let js = match res {
                        Ok(buckets) => {
                            let b_json = serde_json::to_string(&buckets).unwrap_or_else(|_| "[]".to_string());
                            format!("window.onBucketsLoaded({{ok:true,buckets:{b_json}}});")
                        }
                        Err(e) => {
                            let e_json = serde_json::to_string(&e).unwrap_or_else(|_| "\"error\"".to_string());
                            format!("window.onBucketsLoaded({{ok:false,error:{e_json}}});")
                        }
                    };
                    let _ = webview.evaluate_script(&js);
                }
                UserEvent::AccountTested(res) => {
                    let js = match res {
                        Ok(msg) => {
                            let m_json = serde_json::to_string(&msg).unwrap_or_else(|_| "\"OK\"".to_string());
                            format!("window.onAccountTested({{ok:true,message:{m_json}}});")
                        }
                        Err(e) => {
                            let e_json = serde_json::to_string(&e).unwrap_or_else(|_| "\"error\"".to_string());
                            format!("window.onAccountTested({{ok:false,error:{e_json}}});")
                        }
                    };
                    let _ = webview.evaluate_script(&js);
                }
                UserEvent::PrefixBrowsed(res) => {
                    let js = match res {
                        Ok(folders) => {
                            let f_json = serde_json::to_string(&folders).unwrap_or_else(|_| "[]".to_string());
                            format!("window.onPrefixBrowsed({{ok:true,folders:{f_json}}});")
                        }
                        Err(e) => {
                            let e_json = serde_json::to_string(&e).unwrap_or_else(|_| "\"error\"".to_string());
                            format!("window.onPrefixBrowsed({{ok:false,error:{e_json}}});")
                        }
                    };
                    let _ = webview.evaluate_script(&js);
                }
                UserEvent::UpdateChecked(manual, res) => {
                    let js = match res {
                        Ok(info) => {
                            let i_json = serde_json::to_string(&info).unwrap_or_else(|_| "{}".to_string());
                            format!("window.onUpdateChecked({manual}, true, {i_json});")
                        }
                        Err(e) => {
                            let e_json = serde_json::to_string(&e).unwrap_or_else(|_| "\"error\"".to_string());
                            format!("window.onUpdateChecked({manual}, false, {e_json});")
                        }
                    };
                    let _ = webview.evaluate_script(&js);
                }
                UserEvent::ShellContextCommand { action, path, reply_tx } => {
                    let reply = ctrl.handle_shell_context(&action, &path, &webview, &window);
                    let _ = reply_tx.send(reply);
                }
                UserEvent::VersionsLoaded(res) => {
                    let js = match res {
                        Ok(versions) => {
                            let v_json = serde_json::to_string(&versions).unwrap_or_else(|_| "[]".to_string());
                            format!("window.onVersionsLoaded({{ok:true,versions:{v_json}}});")
                        }
                        Err(e) => {
                            let e_json = serde_json::to_string(&e).unwrap_or_else(|_| "\"error\"".to_string());
                            format!("window.onVersionsLoaded({{ok:false,error:{e_json}}});")
                        }
                    };
                    let _ = webview.evaluate_script(&js);
                }
                UserEvent::VersionActionDone(res) => {
                    let js = match res {
                        Ok(msg) => {
                            ctrl.log(&msg);
                            let m_json = serde_json::to_string(&msg).unwrap_or_else(|_| "\"OK\"".to_string());
                            format!("window.onVersionActionDone({{ok:true,message:{m_json}}});")
                        }
                        Err(e) => {
                            ctrl.log(format!("Version action failed: {e}"));
                            let e_json = serde_json::to_string(&e).unwrap_or_else(|_| "\"error\"".to_string());
                            format!("window.onVersionActionDone({{ok:false,error:{e_json}}});")
                        }
                    };
                    let _ = webview.evaluate_script(&js);
                    let _ = webview.evaluate_script(&ctrl.state_script());
                }
                UserEvent::TrashLoaded(res) => {
                    let js = match res {
                        Ok(items) => {
                            let t_json = serde_json::to_string(&items).unwrap_or_else(|_| "[]".to_string());
                            format!("window.onTrashLoaded({{ok:true,items:{t_json}}});")
                        }
                        Err(e) => {
                            let e_json = serde_json::to_string(&e).unwrap_or_else(|_| "\"error\"".to_string());
                            format!("window.onTrashLoaded({{ok:false,error:{e_json}}});")
                        }
                    };
                    let _ = webview.evaluate_script(&js);
                }
                UserEvent::TrashActionDone(res) => {
                    let js = match res {
                        Ok(msg) => {
                            ctrl.log(&msg);
                            let m_json = serde_json::to_string(&msg).unwrap_or_else(|_| "\"OK\"".to_string());
                            format!("window.onTrashActionDone({{ok:true,message:{m_json}}});")
                        }
                        Err(e) => {
                            ctrl.log(format!("Trash action failed: {e}"));
                            let e_json = serde_json::to_string(&e).unwrap_or_else(|_| "\"error\"".to_string());
                            format!("window.onTrashActionDone({{ok:false,error:{e_json}}});")
                        }
                    };
                    let _ = webview.evaluate_script(&js);
                    let _ = webview.evaluate_script(&ctrl.state_script());
                }
                UserEvent::IpcMessage(raw) => {
                    if let Ok(val) = serde_json::from_str::<serde_json::Value>(&raw) {
                        let ty = val.get("type").and_then(|v| v.as_str()).unwrap_or("");
                        match ty {
                            "ready" => {
                                let _ = webview.evaluate_script(&ctrl.state_script());
                                if ctrl.persisted.check_updates_on_startup {
                                    let p = proxy.clone();
                                    std::thread::spawn(move || {
                                        let _ = p.send_event(UserEvent::UpdateChecked(false, check_github_updates()));
                                    });
                                }
                            }
                            "check_updates" => {
                                let p = proxy.clone();
                                std::thread::spawn(move || {
                                    let _ = p.send_event(UserEvent::UpdateChecked(true, check_github_updates()));
                                });
                            }
                            "open_url" => {
                                if let Some(u) = val.get("url").and_then(|v| v.as_str()) {
                                    let _ = Command::new("cmd").args(["/c", "start", "", u]).spawn();
                                }
                            }
                            "hide_to_tray" => {
                                window.set_visible(false);
                            }
                            "clear_logs" => {
                                ctrl.logs.clear();
                                let _ = webview.evaluate_script(&ctrl.state_script());
                            }
                            "mount_drive" => {
                                if let Some(id) = val.get("id").and_then(|v| v.as_str()) {
                                    ctrl.mount_drive(id, proxy.clone());
                                    let _ = webview.evaluate_script(&ctrl.state_script());
                                }
                            }
                            "unmount_drive" => {
                                if let Some(id) = val.get("id").and_then(|v| v.as_str()) {
                                    ctrl.unmount_drive(id);
                                    let _ = webview.evaluate_script(&ctrl.state_script());
                                }
                            }
                            "open_drive" => {
                                if let Some(id) = val.get("id").and_then(|v| v.as_str()) {
                                    if let Some(d) = ctrl.persisted.drives.iter().find(|x| x.id == id) {
                                        let path = format!("{}\\", d.drive_letter.trim_end_matches('\\'));
                                        let _ = Command::new("explorer.exe").arg(path).spawn();
                                    }
                                }
                            }
                            "refresh_cache" => {
                                if let Some(id) = val.get("id").and_then(|v| v.as_str()) {
                                    if let Some(m) = ctrl.mounts.get(id) {
                                        m.fs.clear_cache();
                                        ctrl.log("Directory & metadata cache cleared.");
                                        let _ = webview.evaluate_script(&ctrl.state_script());
                                    }
                                }
                            }
                            "delete_drive" => {
                                if let Some(id) = val.get("id").and_then(|v| v.as_str()) {
                                    ctrl.unmount_drive(id);
                                    ctrl.persisted.drives.retain(|d| d.id != id);
                                    ctrl.save();
                                    let _ = webview.evaluate_script(&ctrl.state_script());
                                }
                            }
                            "save_drive" => {
                                if let Ok(mut d) = serde_json::from_value::<MappedDrive>(val["drive"].clone()) {
                                    if d.id.is_empty() {
                                        d.id = new_id();
                                        ctrl.persisted.drives.push(d);
                                    } else if let Some(existing) =
                                        ctrl.persisted.drives.iter_mut().find(|x| x.id == d.id)
                                    {
                                        *existing = d;
                                    }
                                    ctrl.save();
                                    let _ = webview.evaluate_script(&ctrl.state_script());
                                }
                            }
                            "save_account" => {
                                if let Ok(mut a) = serde_json::from_value::<StorageAccount>(val["account"].clone()) {
                                    if a.id.is_empty() {
                                        a.id = new_id();
                                        ctrl.persisted.accounts.push(a);
                                    } else if let Some(existing) =
                                        ctrl.persisted.accounts.iter_mut().find(|x| x.id == a.id)
                                    {
                                        *existing = a;
                                    }
                                    ctrl.save();
                                    let _ = webview.evaluate_script(&ctrl.state_script());
                                }
                            }
                            "delete_account" => {
                                if let Some(id) = val.get("id").and_then(|v| v.as_str()) {
                                    ctrl.persisted.accounts.retain(|a| a.id != id);
                                    ctrl.save();
                                    let _ = webview.evaluate_script(&ctrl.state_script());
                                }
                            }
                            "move_account" => {
                                if let (Some(id), Some(dir)) = (
                                    val.get("id").and_then(|v| v.as_str()),
                                    val.get("dir").and_then(|v| v.as_i64()),
                                ) {
                                    if let Some(idx) = ctrl.persisted.accounts.iter().position(|a| a.id == id) {
                                        let len = ctrl.persisted.accounts.len();
                                        if dir < 0 && idx > 0 {
                                            ctrl.persisted.accounts.swap(idx, idx - 1);
                                            ctrl.save();
                                        } else if dir > 0 && idx + 1 < len {
                                            ctrl.persisted.accounts.swap(idx, idx + 1);
                                            ctrl.save();
                                        }
                                        let _ = webview.evaluate_script(&ctrl.state_script());
                                    }
                                }
                            }
                            "mount_all" => {
                                let ids: Vec<String> = ctrl.persisted.drives.iter().map(|d| d.id.clone()).collect();
                                for id in ids {
                                    ctrl.mount_drive(&id, proxy.clone());
                                }
                                let _ = webview.evaluate_script(&ctrl.state_script());
                            }
                            "unmount_all" => {
                                ctrl.unmount_all();
                                let _ = webview.evaluate_script(&ctrl.state_script());
                            }
                            "exit_app" => {
                                shell::remove_ipc_port();
                                ctrl.unmount_all();
                                *control_flow = ControlFlow::Exit;
                            }
                            "save_headers" => {
                                if let Ok(hdrs) = serde_json::from_value::<Vec<CustomHeader>>(val["headers"].clone()) {
                                    ctrl.persisted.custom_headers = hdrs;
                                    ctrl.save();
                                    ctrl.log("Default HTTP headers saved.");
                                    let _ = webview.evaluate_script(&ctrl.state_script());
                                }
                            }
                            "save_options" => {
                                let sw = val["start_with_windows"].as_bool().unwrap_or(false);
                                let sm = val["start_minimized_on_windows_startup"].as_bool().unwrap_or(true);
                                ctrl.start_with_windows = sw;
                                ctrl.persisted.start_minimized_on_windows_startup = sm;
                                set_startup_enabled(sw, sm);
                                ctrl.persisted.auto_mount_on_startup = val["auto_mount_on_startup"].as_bool().unwrap_or(true);
                                ctrl.persisted.minimize_to_tray = val["minimize_to_tray"].as_bool().unwrap_or(false);
                                ctrl.persisted.check_updates_on_startup = val["check_updates_on_startup"].as_bool().unwrap_or(true);
                                ctrl.persisted.multipart_part_size_mb = (val["multipart_part_size_mb"].as_u64().unwrap_or(8) as usize).clamp(5, 128);
                                if let Some(v) = val["proxy_mode"].as_str() { ctrl.persisted.proxy_mode = v.to_string(); }
                                if let Some(v) = val["proxy_server"].as_str() { ctrl.persisted.proxy_server = v.to_string(); }
                                ctrl.persisted.proxy_port = val["proxy_port"].as_u64().unwrap_or(8080) as u16;
                                ctrl.persisted.proxy_requires_auth = val["proxy_requires_auth"].as_bool().unwrap_or(false);
                                if let Some(v) = val["proxy_username"].as_str() { ctrl.persisted.proxy_username = v.to_string(); }
                                ctrl.persisted.throttle_uploads = val["throttle_uploads"].as_bool().unwrap_or(false);
                                ctrl.persisted.max_upload_kbps = val["max_upload_kbps"].as_u64().unwrap_or(1024) as u32;
                                ctrl.persisted.throttle_downloads = val["throttle_downloads"].as_bool().unwrap_or(false);
                                ctrl.persisted.max_download_kbps = val["max_download_kbps"].as_u64().unwrap_or(2048) as u32;
                                ctrl.persisted.verify_upload_integrity = val["verify_upload_integrity"].as_bool().unwrap_or(true);
                                ctrl.persisted.verify_download_integrity = val["verify_download_integrity"].as_bool().unwrap_or(true);
                                ctrl.persisted.max_concurrent_uploads = val["max_concurrent_uploads"].as_u64().unwrap_or(4) as u32;
                                ctrl.persisted.max_concurrent_downloads = val["max_concurrent_downloads"].as_u64().unwrap_or(4) as u32;
                                ctrl.persisted.retry_count = val["retry_count"].as_u64().unwrap_or(3) as u32;
                                if let Some(v) = val["log_level"].as_str() { ctrl.persisted.log_level = v.to_string(); }
                                ctrl.persisted.log_retention_days = val["log_retention_days"].as_u64().unwrap_or(14) as u32;
                                ctrl.persisted.request_timeout_secs = val["request_timeout_secs"].as_u64().unwrap_or(60).clamp(5, 600);
                                ctrl.persisted.read_buffer_kb = val["read_buffer_kb"].as_u64().unwrap_or(256) as u32;
                                ctrl.persisted.disk_cache_enabled = val["disk_cache_enabled"].as_bool().unwrap_or(true);
                                ctrl.persisted.disk_cache_mb = val["disk_cache_mb"].as_u64().unwrap_or(2048).clamp(64, 65536);
                                ctrl.persisted.write_back_enabled = val["write_back_enabled"].as_bool().unwrap_or(true);
                                ctrl.persisted.prefetch_chunks = val["prefetch_chunks"].as_u64().unwrap_or(2).min(8);
                                let ctx_en = val["context_menu_enabled"].as_bool().unwrap_or(true);
                                if ctx_en != ctrl.persisted.context_menu_enabled {
                                    ctrl.persisted.context_menu_enabled = ctx_en;
                                    if ctx_en {
                                        if let Ok(exe) = std::env::current_exe() {
                                            let _ = shell::register_context_menu(&exe.to_string_lossy());
                                        }
                                    } else {
                                        let _ = shell::unregister_context_menu();
                                    }
                                }
                                ctrl.disk_cache.set_limits(ctrl.persisted.disk_cache_mb, ctrl.persisted.disk_cache_enabled);
                                ctrl.save();
                                ctrl.log("Options saved.");
                                let _ = webview.evaluate_script(&ctrl.state_script());
                            }
                            "get_transfers" => {
                                let snap = ctrl.transfer_tracker.snapshot();
                                let (bytes, chunks) = ctrl.disk_cache.stats();
                                let t_json = serde_json::to_string(&snap).unwrap_or_else(|_| "[]".to_string());
                                let _ = webview.evaluate_script(&format!(
                                    "window.onTransfersUpdate && window.onTransfersUpdate({t_json}, {bytes}, {chunks});"
                                ));
                            }
                            "clear_disk_cache" => {
                                ctrl.disk_cache.clear();
                                ctrl.log("Disk LRU read cache cleared.");
                                let _ = webview.evaluate_script(&ctrl.state_script());
                            }
                            "toggle_context_menu" => {
                                let enable = val["enable"].as_bool().unwrap_or(false);
                                let res = if enable {
                                    let exe = std::env::current_exe()
                                        .map(|p| p.to_string_lossy().into_owned())
                                        .unwrap_or_default();
                                    shell::register_context_menu(&exe)
                                } else {
                                    shell::unregister_context_menu()
                                };
                                match res {
                                    Ok(()) => {
                                        ctrl.persisted.context_menu_enabled = enable;
                                        ctrl.save();
                                        ctrl.log(if enable {
                                            "Windows Explorer context menu registered."
                                        } else {
                                            "Windows Explorer context menu removed."
                                        });
                                    }
                                    Err(e) => ctrl.log(format!("Context menu error: {e}")),
                                }
                                let _ = webview.evaluate_script(&ctrl.state_script());
                            }
                            "list_versions" => {
                                if let Some(drive_id) = val.get("drive_id").and_then(|v| v.as_str()) {
                                    let prefix = val.get("prefix").and_then(|v| v.as_str()).unwrap_or("").to_string();
                                    if let Some(drive) = ctrl.persisted.drives.iter().find(|d| d.id == drive_id).cloned() {
                                        if let Some(acct) = ctrl.persisted.accounts.iter().find(|a| a.id == drive.account_id).cloned() {
                                            let cfg = ctrl.build_s3_config(&acct);
                                            let bucket = drive.bucket.clone();
                                            let full_prefix = if prefix.is_empty() {
                                                drive.prefix.trim().trim_matches('/').to_string()
                                            } else {
                                                prefix
                                            };
                                            let p = proxy.clone();
                                            std::thread::spawn(move || {
                                                let res = S3Client::new(cfg)
                                                    .and_then(|c| c.list_object_versions(&bucket, &full_prefix));
                                                let _ = p.send_event(UserEvent::VersionsLoaded(res));
                                            });
                                        }
                                    }
                                }
                            }
                            "restore_version" => {
                                if let (Some(drive_id), Some(key), Some(vid)) = (
                                    val.get("drive_id").and_then(|v| v.as_str()),
                                    val.get("key").and_then(|v| v.as_str()),
                                    val.get("version_id").and_then(|v| v.as_str()),
                                ) {
                                    let is_dm = val.get("is_delete_marker").and_then(|v| v.as_bool()).unwrap_or(false);
                                    if let Some(drive) = ctrl.persisted.drives.iter().find(|d| d.id == drive_id).cloned() {
                                        if let Some(acct) = ctrl.persisted.accounts.iter().find(|a| a.id == drive.account_id).cloned() {
                                            if let Some(m) = ctrl.mounts.get(drive_id) {
                                                m.fs.purge_path_cache(key);
                                            }
                                            let cfg = ctrl.build_s3_config(&acct);
                                            let bucket = drive.bucket.clone();
                                            let k = key.to_string();
                                            let v = vid.to_string();
                                            let p = proxy.clone();
                                            std::thread::spawn(move || {
                                                let res = S3Client::new(cfg).and_then(|c| {
                                                    if is_dm {
                                                        c.delete_object_version(&bucket, &k, &v)?;
                                                        Ok(format!("Removed delete marker on {k}"))
                                                    } else {
                                                        c.restore_object_version(&bucket, &k, &v)?;
                                                        Ok(format!("Restored {k} to version {v}"))
                                                    }
                                                });
                                                let _ = p.send_event(UserEvent::VersionActionDone(res));
                                            });
                                        }
                                    }
                                }
                            }
                            "delete_version" => {
                                if let (Some(drive_id), Some(key), Some(vid)) = (
                                    val.get("drive_id").and_then(|v| v.as_str()),
                                    val.get("key").and_then(|v| v.as_str()),
                                    val.get("version_id").and_then(|v| v.as_str()),
                                ) {
                                    if let Some(drive) = ctrl.persisted.drives.iter().find(|d| d.id == drive_id).cloned() {
                                        if let Some(acct) = ctrl.persisted.accounts.iter().find(|a| a.id == drive.account_id).cloned() {
                                            if let Some(m) = ctrl.mounts.get(drive_id) {
                                                m.fs.purge_path_cache(key);
                                            }
                                            let cfg = ctrl.build_s3_config(&acct);
                                            let bucket = drive.bucket.clone();
                                            let k = key.to_string();
                                            let v = vid.to_string();
                                            let p = proxy.clone();
                                            std::thread::spawn(move || {
                                                let res = S3Client::new(cfg).and_then(|c| {
                                                    c.delete_object_version(&bucket, &k, &v)?;
                                                    Ok(format!("Deleted version {v} of {k}"))
                                                });
                                                let _ = p.send_event(UserEvent::VersionActionDone(res));
                                            });
                                        }
                                    }
                                }
                            }
                            "list_trash" => {
                                if let Some(drive_id) = val.get("drive_id").and_then(|v| v.as_str()) {
                                    if let Some(drive) = ctrl.persisted.drives.iter().find(|d| d.id == drive_id).cloned() {
                                        if let Some(acct) = ctrl.persisted.accounts.iter().find(|a| a.id == drive.account_id).cloned() {
                                            let cfg = ctrl.build_s3_config(&acct);
                                            let bucket = drive.bucket.clone();
                                            let clean_prefix = drive.prefix.trim().trim_matches('/').to_string();
                                            let trash_name = if drive.trash_prefix.trim().is_empty() {
                                                ".trash".to_string()
                                            } else {
                                                drive.trash_prefix.trim().trim_matches('/').to_string()
                                            };
                                            let trash_prefix = if clean_prefix.is_empty() {
                                                format!("{trash_name}/")
                                            } else {
                                                format!("{clean_prefix}/{trash_name}/")
                                            };
                                            let p = proxy.clone();
                                            std::thread::spawn(move || {
                                                let res = S3Client::new(cfg).and_then(|c| {
                                                    let mut all: Vec<S3Entry> = Vec::new();
                                                    let mut stack = vec![trash_prefix.clone()];
                                                    while let Some(pfx) = stack.pop() {
                                                        let (objs, folders) = c.list_objects(&bucket, &pfx)?;
                                                        all.extend(objs.into_iter().filter(|o| !o.is_dir));
                                                        stack.extend(folders);
                                                    }
                                                    let views = all
                                                        .into_iter()
                                                        .map(|o| {
                                                            let rel = o.key.strip_prefix(&trash_prefix).unwrap_or(&o.key);
                                                            // Format: <YYYYMMDDTHHMMSSZ>_<original_rel>
                                                            let orig_rel = if let Some(idx) = rel.find('_') {
                                                                &rel[idx + 1..]
                                                            } else {
                                                                rel
                                                            };
                                                            let original_key = if clean_prefix.is_empty() {
                                                                orig_rel.to_string()
                                                            } else {
                                                                format!("{clean_prefix}/{orig_rel}")
                                                            };
                                                            TrashEntryView {
                                                                key: o.key.clone(),
                                                                display_name: rel.to_string(),
                                                                original_key,
                                                                size: o.size,
                                                                last_modified_filetime: o.last_modified_filetime,
                                                            }
                                                        })
                                                        .collect::<Vec<_>>();
                                                    Ok(views)
                                                });
                                                let _ = p.send_event(UserEvent::TrashLoaded(res));
                                            });
                                        }
                                    }
                                }
                            }
                            "restore_trash_item" => {
                                if let (Some(drive_id), Some(trash_key), Some(orig_key)) = (
                                    val.get("drive_id").and_then(|v| v.as_str()),
                                    val.get("key").and_then(|v| v.as_str()),
                                    val.get("original_key").and_then(|v| v.as_str()),
                                ) {
                                    if let Some(drive) = ctrl.persisted.drives.iter().find(|d| d.id == drive_id).cloned() {
                                        if let Some(acct) = ctrl.persisted.accounts.iter().find(|a| a.id == drive.account_id).cloned() {
                                            if let Some(m) = ctrl.mounts.get(drive_id) {
                                                m.fs.purge_path_cache(orig_key);
                                                m.fs.purge_path_cache(trash_key);
                                            }
                                            let cfg = ctrl.build_s3_config(&acct);
                                            let bucket = drive.bucket.clone();
                                            let tk = trash_key.to_string();
                                            let ok = orig_key.to_string();
                                            let p = proxy.clone();
                                            std::thread::spawn(move || {
                                                let res = S3Client::new(cfg).and_then(|c| {
                                                    if c.copy_object(&bucket, &tk, &ok).is_err() {
                                                        let data = c.get_object(&bucket, &tk)?;
                                                        c.put_object(&bucket, &ok, &data)?;
                                                    }
                                                    c.delete_object(&bucket, &tk)?;
                                                    Ok(format!("Restored {ok} from Trash"))
                                                });
                                                let _ = p.send_event(UserEvent::TrashActionDone(res));
                                            });
                                        }
                                    }
                                }
                            }
                            "delete_trash_item" => {
                                if let (Some(drive_id), Some(trash_key)) = (
                                    val.get("drive_id").and_then(|v| v.as_str()),
                                    val.get("key").and_then(|v| v.as_str()),
                                ) {
                                    if let Some(drive) = ctrl.persisted.drives.iter().find(|d| d.id == drive_id).cloned() {
                                        if let Some(acct) = ctrl.persisted.accounts.iter().find(|a| a.id == drive.account_id).cloned() {
                                            if let Some(m) = ctrl.mounts.get(drive_id) {
                                                m.fs.purge_path_cache(trash_key);
                                            }
                                            let cfg = ctrl.build_s3_config(&acct);
                                            let bucket = drive.bucket.clone();
                                            let tk = trash_key.to_string();
                                            let p = proxy.clone();
                                            std::thread::spawn(move || {
                                                let res = S3Client::new(cfg).and_then(|c| {
                                                    c.delete_object(&bucket, &tk)?;
                                                    Ok(format!("Permanently deleted {tk}"))
                                                });
                                                let _ = p.send_event(UserEvent::TrashActionDone(res));
                                            });
                                        }
                                    }
                                }
                            }
                            "empty_trash" => {
                                if let Some(drive_id) = val.get("drive_id").and_then(|v| v.as_str()) {
                                    if let Some(drive) = ctrl.persisted.drives.iter().find(|d| d.id == drive_id).cloned() {
                                        if let Some(acct) = ctrl.persisted.accounts.iter().find(|a| a.id == drive.account_id).cloned() {
                                            let cfg = ctrl.build_s3_config(&acct);
                                            let bucket = drive.bucket.clone();
                                            let clean_prefix = drive.prefix.trim().trim_matches('/').to_string();
                                            let trash_name = if drive.trash_prefix.trim().is_empty() {
                                                ".trash".to_string()
                                            } else {
                                                drive.trash_prefix.trim().trim_matches('/').to_string()
                                            };
                                            let trash_prefix = if clean_prefix.is_empty() {
                                                format!("{trash_name}/")
                                            } else {
                                                format!("{clean_prefix}/{trash_name}/")
                                            };
                                            if let Some(m) = ctrl.mounts.get(drive_id) {
                                                m.fs.purge_path_cache(&trash_prefix);
                                            }
                                            let p = proxy.clone();
                                            std::thread::spawn(move || {
                                                let res = S3Client::new(cfg).and_then(|c| {
                                                    let mut stack = vec![trash_prefix];
                                                    let mut count = 0usize;
                                                    while let Some(pfx) = stack.pop() {
                                                        let (objs, folders) = c.list_objects(&bucket, &pfx)?;
                                                        for o in objs {
                                                            c.delete_object(&bucket, &o.key)?;
                                                            count += 1;
                                                        }
                                                        stack.extend(folders);
                                                    }
                                                    Ok(format!("Emptied Trash ({count} item(s) deleted)"))
                                                });
                                                let _ = p.send_event(UserEvent::TrashActionDone(res));
                                            });
                                        }
                                    }
                                }
                            }
                            "test_account" => {
                                if let Ok(acct) = serde_json::from_value::<StorageAccount>(val["account"].clone()) {
                                    let cfg = ctrl.build_s3_config(&acct);
                                    let p = proxy.clone();
                                    std::thread::spawn(move || {
                                        let t0 = std::time::Instant::now();
                                        let res = S3Client::new(cfg)
                                            .and_then(|c| c.list_buckets())
                                            .map(|b| format!("Connected ({} ms) â€” {} bucket(s) found", t0.elapsed().as_millis(), b.len()));
                                        let _ = p.send_event(UserEvent::AccountTested(res));
                                    });
                                }
                            }
                            "browse_prefix" => {
                                if let (Some(aid), Some(bucket)) = (
                                    val.get("account_id").and_then(|v| v.as_str()),
                                    val.get("bucket").and_then(|v| v.as_str()),
                                ) {
                                    let prefix = val.get("prefix").and_then(|v| v.as_str()).unwrap_or("").to_string();
                                    let bucket = bucket.to_string();
                                    if let Some(acct) = ctrl.persisted.accounts.iter().find(|a| a.id == aid).cloned() {
                                        let cfg = ctrl.build_s3_config(&acct);
                                        let p = proxy.clone();
                                        std::thread::spawn(move || {
                                            let res = S3Client::new(cfg).and_then(|c| {
                                                let (_, folders) = c.list_objects(&bucket, &prefix)?;
                                                Ok(folders)
                                            });
                                            let _ = p.send_event(UserEvent::PrefixBrowsed(res));
                                        });
                                    }
                                }
                            }
                            "load_buckets" => {
                                if let Some(aid) = val.get("account_id").and_then(|v| v.as_str()) {
                                    if let Some(acct) = ctrl.persisted.accounts.iter().find(|a| a.id == aid).cloned() {
                                        let cfg = ctrl.build_s3_config(&acct);
                                        let p = proxy.clone();
                                        std::thread::spawn(move || {
                                            let res = S3Client::new(cfg).and_then(|c| c.list_buckets());
                                            let _ = p.send_event(UserEvent::BucketsLoaded(res));
                                        });
                                    }
                                }
                            }
                            _ => {}
                        }
                    }
                }
            },
            _ => {}
        }
    });
}
