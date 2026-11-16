<p align="center">
  <img src="assets/vps3drive-logo.png" alt="VPS3Drive Logo" width="110" />
</p>

# VPS3Drive (Rust Edition)

**VPS3Drive** mounts **Amazon S3** and **S3-compatible** object storage (Cloudflare R2, MinIO, Wasabi, Backblaze B2, DigitalOcean Spaces, etc.) as native Windows drive letters via **WinFsp**.

> Looking for the **.NET 8 / C# WPF** implementation? Switch to the **[`dotnet`](https://github.com/vps3drive/vps3drive/tree/dotnet)** branch.

---

## Highlights
- **Ultra-Lightweight Native Core:** ~21–43 MB Working Set (~6–17 MB Private Memory) with zero .NET runtime dependency.
- **Streaming S3 Multipart Uploads:** Constant low memory usage when writing large files directly to S3.
- **Fast Metadata & Small-File I/O:** ~1.5×–10× faster directory listings and small-file operations compared to managed wrappers.
- **Familiar Windows Desktop UI:** Native WebView2 interface matching classic drive-mapper workflows, with System Tray background mode, Auto-Mount on startup, and Start with Windows.
- **Hybrid Disk LRU Cache & Prefetching:** 4 MB chunked local read cache with LRU eviction, sequential range-request prefetching, and a background Write-Back Queue with concurrent multipart uploads.
- **Windows Explorer Context Menu:** Right-click files/folders on mounted drives to copy Presigned URLs (1h / 24h / 7d), S3 URI, HTTPS URL, Purge Cache, or open Versions & Trash Recovery.
- **S3 Versioning & Trash Recovery:** Per-drive `.trash/` soft-delete mode plus full S3 object version / delete-marker listing, restore, and Trash management.
- **Auto-Update & Auto-Release:** Built-in GitHub Release update checker (`Help → Check for updates...`) and automated Windows Setup + Portable builds via GitHub Actions (`v*` tags).

## Benchmark (Windows 11 x64, same S3 bucket)

| Operation | Rust (`main`) | .NET 8 (`dotnet`) |
|---|---|---|
| Write 10 × 100 KB files | **414 ms** | 603 ms |
| Directory listing (10 files) | **8 ms** | 80 ms |
| Read 10 × 100 KB files | **217 ms** | 397 ms |
| Write 30 MB file (Streaming Multipart) | **9.19 MB/s** | 8.88 MB/s |
| Read 30 MB file | **8.97 MB/s** | 8.79 MB/s |
| Private Memory | **16.8 MB** | 113.2 MB |

## Installation
Download either package from [**Releases**](https://github.com/vps3drive/vps3drive/releases):
- **Installer (`VPS3Drive-Rust-Setup-vX.Y.Z.exe`):** Bundles WinFsp automatically — one-click install on a fresh Windows PC.
- **Portable (`VPS3Drive-Rust-vX.Y.Z-win-x64-portable.zip`):** Extract and run `vps3drive-rust.exe` (includes `winfsp-x64.dll` and `winfsp.msi` if WinFsp is not yet installed).

## Build from Source
Requirements: Rust (`stable-x86_64-pc-windows-gnullvm`), `llvm-mingw` / `libclang`, WinFsp SDK, and Inno Setup 6/7 (for installer).
```powershell
.\scripts\build-release.ps1 -Version 1.1.0
```

## License
MIT — see [LICENSE](LICENSE).
