use crate::cache::{upload_file_concurrent, DiskCache, UploadJob, WriteBackQueue, CACHE_CHUNK_SIZE};
use crate::s3::{now_filetime, S3Client};
use std::collections::HashMap;
use std::ffi::c_void;
use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use windows::Win32::Foundation::{
    STATUS_ACCESS_DENIED, STATUS_DIRECTORY_NOT_EMPTY, STATUS_MEDIA_WRITE_PROTECTED,
    STATUS_OBJECT_NAME_COLLISION, STATUS_OBJECT_NAME_NOT_FOUND, STATUS_OBJECT_PATH_NOT_FOUND,
};
use windows::Win32::Storage::FileSystem::{
    FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_READONLY,
};
use winfsp::filesystem::{
    DirBuffer, DirInfo, DirMarker, FileInfo, FileSecurity, FileSystemContext,
    ModificationDescriptor, OpenFileInfo, VolumeInfo, WideNameInfo,
};
use winfsp::U16CStr;
use winfsp_sys::{FspCleanupDelete, FILE_ACCESS_RIGHTS, FILE_FLAGS_AND_ATTRIBUTES};

const FILE_DIRECTORY_FILE: u32 = 0x0000_0001;

#[derive(Clone, Debug)]
struct CachedMeta {
    is_dir: bool,
    size: u64,
    last_modified: u64,
}

struct DirCacheEntry {
    fetched_at: Instant,
    children: Vec<(String, CachedMeta)>,
}

pub struct S3FileSystem {
    client: Arc<S3Client>,
    bucket: String,
    prefix: String,
    volume_label: String,
    read_only: bool,
    cache_ttl: Duration,
    multipart_part_size: usize,
    upload_concurrency: usize,
    prefetch_chunks: u64,
    soft_delete: bool,
    trash_prefix: String,
    disk_cache: Arc<DiskCache>,
    write_back_queue: Option<Arc<WriteBackQueue>>,
    dir_cache: Mutex<HashMap<String, DirCacheEntry>>,
    meta_cache: Mutex<HashMap<String, (Instant, Option<CachedMeta>)>>,
}

pub struct S3FileContext {
    key: String,
    is_dir: bool,
    dir_buffer: DirBuffer,
    state: Mutex<FileState>,
}

enum WriteBackend {
    None,
    /// Streaming sequential upload: buffers up to 8 MB, uploads parts on the fly.
    Streaming {
        buf: Vec<u8>,
        upload_id: Option<String>,
        parts: Vec<(usize, String)>,
        written_total: u64,
    },
    /// Temp-file fallback for random writes or modifying an existing remote file.
    TempFile {
        file: File,
        path: PathBuf,
    },
}

struct FileState {
    size: u64,
    last_modified: u64,
    backend: WriteBackend,
    dirty: bool,
    delete_on_close: bool,
}

impl Drop for FileState {
    fn drop(&mut self) {
        if let WriteBackend::TempFile { path, .. } = &self.backend {
            let _ = std::fs::remove_file(path);
        }
    }
}

impl S3FileSystem {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        client: Arc<S3Client>,
        bucket: String,
        prefix: String,
        volume_label: String,
        read_only: bool,
        cache_ttl_secs: u64,
        multipart_part_size_mb: usize,
        upload_concurrency: usize,
        prefetch_chunks: u64,
        soft_delete: bool,
        trash_prefix: String,
        disk_cache: Arc<DiskCache>,
        write_back_queue: Option<Arc<WriteBackQueue>>,
    ) -> Self {
        let clean_prefix = prefix.trim().trim_matches('/').to_string();
        let part_mb = multipart_part_size_mb.clamp(5, 128);
        let clean_trash = trash_prefix.trim().trim_matches('/').to_string();
        let clean_trash = if clean_trash.is_empty() {
            ".trash".to_string()
        } else {
            clean_trash
        };
        Self {
            client,
            bucket,
            prefix: clean_prefix,
            volume_label,
            read_only,
            cache_ttl: Duration::from_secs(cache_ttl_secs),
            multipart_part_size: part_mb * 1024 * 1024,
            upload_concurrency: upload_concurrency.clamp(1, 8),
            prefetch_chunks: prefetch_chunks.min(8),
            soft_delete,
            trash_prefix: clean_trash,
            disk_cache,
            write_back_queue,
            dir_cache: Mutex::new(HashMap::new()),
            meta_cache: Mutex::new(HashMap::new()),
        }
    }

    pub fn bucket(&self) -> &str {
        &self.bucket
    }

    pub fn prefix(&self) -> &str {
        &self.prefix
    }

    pub fn client(&self) -> &Arc<S3Client> {
        &self.client
    }

    pub fn resolve_s3_key_from_relative(&self, rel_path: &str) -> String {
        let clean = rel_path
            .trim_start_matches(['\\', '/'])
            .replace('\\', "/")
            .trim_end_matches('/')
            .to_string();
        if self.prefix.is_empty() {
            clean
        } else if clean.is_empty() {
            self.prefix.clone()
        } else {
            format!("{}/{}", self.prefix, clean)
        }
    }

    pub fn clear_cache(&self) {
        if let Ok(mut dc) = self.dir_cache.lock() {
            dc.clear();
        }
        if let Ok(mut mc) = self.meta_cache.lock() {
            mc.clear();
        }
        self.disk_cache.clear();
    }

    pub fn purge_path_cache(&self, key: &str) {
        let clean = key.trim_end_matches('/');
        if let Ok(mut mc) = self.meta_cache.lock() {
            let prefix_slash = format!("{clean}/");
            mc.retain(|k, _| k != clean && !k.starts_with(&prefix_slash));
        }
        let parent = Self::parent_dir(clean);
        if let Ok(mut dc) = self.dir_cache.lock() {
            let prefix_slash = format!("{clean}/");
            dc.retain(|k, _| k != clean && k != &parent && !k.starts_with(&prefix_slash));
        }
        self.disk_cache.invalidate_prefix(&self.bucket, clean);
    }

    fn trash_root(&self) -> String {
        if self.prefix.is_empty() {
            self.trash_prefix.clone()
        } else {
            format!("{}/{}", self.prefix, self.trash_prefix)
        }
    }

    fn is_in_trash(&self, key: &str) -> bool {
        let root = self.trash_root();
        key == root || key.starts_with(&format!("{root}/"))
    }

    fn move_to_trash(&self, key: &str) -> Result<(), String> {
        let rel = if !self.prefix.is_empty() {
            key.strip_prefix(&format!("{}/", self.prefix))
                .unwrap_or(key)
        } else {
            key
        };
        let ts = chrono::Utc::now().format("%Y%m%dT%H%M%SZ");
        let dst = format!("{}/{}_{}", self.trash_root(), ts, rel);
        if self.client.copy_object(&self.bucket, key, &dst).is_err() {
            let data = self.client.get_object(&self.bucket, key)?;
            self.client.put_object(&self.bucket, &dst, &data)?;
        }
        self.client.delete_object(&self.bucket, key)?;
        self.invalidate_path(&dst);
        Ok(())
    }

    fn is_root_key(&self, key: &str) -> bool {
        key == self.prefix
    }

    fn normalize_key(&self, path: &U16CStr) -> String {
        let s = path.to_string_lossy();
        let trimmed = s.trim_start_matches(['\\', '/']).replace('\\', "/");
        let clean = trimmed.trim_end_matches('/');
        if self.prefix.is_empty() {
            clean.to_string()
        } else if clean.is_empty() {
            self.prefix.clone()
        } else {
            format!("{}/{}", self.prefix, clean)
        }
    }

    fn parent_dir(key: &str) -> String {
        match key.rfind('/') {
            Some(idx) => key[..idx].to_string(),
            None => String::new(),
        }
    }

    fn invalidate_path(&self, key: &str) {
        if let Ok(mut mc) = self.meta_cache.lock() {
            mc.remove(key);
        }
        let parent = Self::parent_dir(key);
        if let Ok(mut dc) = self.dir_cache.lock() {
            dc.remove(&parent);
            dc.remove(key);
        }
    }

    fn list_dir(&self, dir_key: &str) -> Result<Vec<(String, CachedMeta)>, String> {
        if let Ok(dc) = self.dir_cache.lock() {
            if let Some(entry) = dc.get(dir_key) {
                if entry.fetched_at.elapsed() < self.cache_ttl {
                    return Ok(entry.children.clone());
                }
            }
        }

        let prefix = if dir_key.is_empty() {
            String::new()
        } else {
            format!("{dir_key}/")
        };

        let (objects, folders) = self.client.list_objects(&self.bucket, &prefix)?;
        let mut children_map: HashMap<String, CachedMeta> = HashMap::new();

        for folder in folders {
            let trimmed = folder.trim_end_matches('/');
            if let Some(name) = trimmed.strip_prefix(&prefix) {
                if !name.is_empty() && !name.contains('/') {
                    children_map.insert(
                        name.to_string(),
                        CachedMeta {
                            is_dir: true,
                            size: 0,
                            last_modified: now_filetime(),
                        },
                    );
                }
            }
        }

        for obj in objects {
            if obj.key == prefix {
                continue;
            }
            if let Some(rest) = obj.key.strip_prefix(&prefix) {
                if rest.is_empty() {
                    continue;
                }
                if let Some(slash) = rest.find('/') {
                    let folder_name = &rest[..slash];
                    children_map.entry(folder_name.to_string()).or_insert(CachedMeta {
                        is_dir: true,
                        size: 0,
                        last_modified: obj.last_modified_filetime,
                    });
                } else {
                    children_map.insert(
                        rest.to_string(),
                        CachedMeta {
                            is_dir: false,
                            size: obj.size,
                            last_modified: obj.last_modified_filetime,
                        },
                    );
                }
            }
        }

        let mut children: Vec<(String, CachedMeta)> = children_map.into_iter().collect();
        children.sort_by(|a, b| a.0.cmp(&b.0));

        if let Ok(mut dc) = self.dir_cache.lock() {
            dc.insert(
                dir_key.to_string(),
                DirCacheEntry {
                    fetched_at: Instant::now(),
                    children: children.clone(),
                },
            );
        }
        if let Ok(mut mc) = self.meta_cache.lock() {
            for (name, meta) in &children {
                let full_key = if dir_key.is_empty() {
                    name.clone()
                } else {
                    format!("{dir_key}/{name}")
                };
                mc.insert(full_key, (Instant::now(), Some(meta.clone())));
            }
        }

        Ok(children)
    }

    fn get_meta(&self, key: &str) -> Result<Option<CachedMeta>, String> {
        if self.is_root_key(key) {
            return Ok(Some(CachedMeta {
                is_dir: true,
                size: 0,
                last_modified: now_filetime(),
            }));
        }

        if let Ok(mc) = self.meta_cache.lock() {
            if let Some((ts, cached)) = mc.get(key) {
                if ts.elapsed() < self.cache_ttl {
                    return Ok(cached.clone());
                }
            }
        }

        let parent = Self::parent_dir(key);
        let name = key.rsplit('/').next().unwrap_or(key);
        let children = self.list_dir(&parent)?;
        if let Some((_, meta)) = children.into_iter().find(|(n, _)| n == name) {
            return Ok(Some(meta));
        }

        if let Ok(mut mc) = self.meta_cache.lock() {
            mc.insert(key.to_string(), (Instant::now(), None));
        }
        Ok(None)
    }

    fn fill_file_info(&self, meta: &CachedMeta, info: &mut FileInfo) {
        let mut attrs = if meta.is_dir {
            FILE_ATTRIBUTE_DIRECTORY.0
        } else {
            FILE_ATTRIBUTE_NORMAL.0
        };
        if self.read_only && !meta.is_dir {
            attrs |= FILE_ATTRIBUTE_READONLY.0;
        }
        info.file_attributes = attrs;
        info.file_size = meta.size;
        info.allocation_size = (meta.size + 4095) & !4095;
        info.creation_time = meta.last_modified;
        info.last_access_time = meta.last_modified;
        info.last_write_time = meta.last_modified;
        info.change_time = meta.last_modified;
    }

    fn open_temp_file(initial: Option<&[u8]>) -> std::io::Result<(File, PathBuf)> {
        let id = now_filetime();
        let path = std::env::temp_dir().join(format!("vps3drive-rust-{id}-{}.tmp", std::process::id()));
        let mut file = OpenOptions::new()
            .create(true)
            .read(true)
            .write(true)
            .truncate(true)
            .open(&path)?;
        if let Some(bytes) = initial {
            file.write_all(bytes)?;
        }
        Ok((file, path))
    }

    fn staging_dir() -> PathBuf {
        let base = std::env::var("LOCALAPPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|_| std::env::temp_dir());
        let dir = base.join("VPS3Drive").join("staging");
        let _ = std::fs::create_dir_all(&dir);
        dir
    }

    fn update_caches_on_write(&self, key: &str, size: u64, last_modified: u64) {
        self.disk_cache.invalidate_key(&self.bucket, key);
        if let Ok(mut mc) = self.meta_cache.lock() {
            mc.insert(
                key.to_string(),
                (
                    Instant::now(),
                    Some(CachedMeta {
                        is_dir: false,
                        size,
                        last_modified,
                    }),
                ),
            );
        }
        let parent = Self::parent_dir(key);
        if let Ok(mut dc) = self.dir_cache.lock() {
            dc.remove(&parent);
            dc.remove(key);
        }
    }

    fn flush_state(&self, key: &str, state: &mut FileState) -> Result<(), String> {
        if !state.dirty || state.delete_on_close {
            return Ok(());
        }

        let staging_id = now_filetime();
        let mut queued = false;

        match &mut state.backend {
            WriteBackend::None => {}
            WriteBackend::Streaming {
                buf,
                upload_id,
                parts,
                ..
            } => {
                if let Some(uid) = upload_id.take() {
                    if !buf.is_empty() {
                        let pn = parts.len() + 1;
                        match self.client.upload_part(&self.bucket, key, &uid, pn, buf) {
                            Ok(etag) => parts.push((pn, etag)),
                            Err(e) => {
                                let _ = self.client.abort_multipart_upload(&self.bucket, key, &uid);
                                return Err(e);
                            }
                        }
                        buf.clear();
                    }
                    if let Err(e) = self
                        .client
                        .complete_multipart_upload(&self.bucket, key, &uid, parts)
                    {
                        let _ = self.client.abort_multipart_upload(&self.bucket, key, &uid);
                        return Err(e);
                    }
                    parts.clear();
                } else if let Some(ref queue) = self.write_back_queue {
                    let staging_path = Self::staging_dir()
                        .join(format!("wb-{staging_id}-{}.bin", std::process::id()));
                    std::fs::write(&staging_path, &*buf).map_err(|e| e.to_string())?;
                    let size = buf.len() as u64;
                    let tid = queue.tracker().start_transfer(
                        &self.bucket,
                        key,
                        "upload",
                        size,
                        "queued",
                    );
                    queue.enqueue(UploadJob {
                        transfer_id: tid,
                        client: Arc::clone(&self.client),
                        bucket: self.bucket.clone(),
                        key: key.to_string(),
                        staging_path,
                        size,
                        part_size: self.multipart_part_size,
                        concurrency: self.upload_concurrency,
                    });
                    queued = true;
                } else {
                    self.client.put_object(&self.bucket, key, buf)?;
                }
            }
            WriteBackend::TempFile { file, path } => {
                file.flush().map_err(|e| e.to_string())?;
                if let Some(ref queue) = self.write_back_queue {
                    let staging_path = Self::staging_dir()
                        .join(format!("wb-{staging_id}-{}.bin", std::process::id()));
                    std::fs::copy(&*path, &staging_path).map_err(|e| e.to_string())?;
                    let tid = queue.tracker().start_transfer(
                        &self.bucket,
                        key,
                        "upload",
                        state.size,
                        "queued",
                    );
                    queue.enqueue(UploadJob {
                        transfer_id: tid,
                        client: Arc::clone(&self.client),
                        bucket: self.bucket.clone(),
                        key: key.to_string(),
                        staging_path,
                        size: state.size,
                        part_size: self.multipart_part_size,
                        concurrency: self.upload_concurrency,
                    });
                    queued = true;
                } else {
                    upload_file_concurrent(
                        &self.client,
                        &self.bucket,
                        key,
                        path,
                        state.size,
                        self.multipart_part_size,
                        self.upload_concurrency,
                        |_| {},
                    )?;
                }
            }
        }

        state.dirty = false;
        if queued {
            self.update_caches_on_write(key, state.size, state.last_modified);
        } else {
            self.disk_cache.invalidate_key(&self.bucket, key);
            self.invalidate_path(key);
        }
        Ok(())
    }
}

impl FileSystemContext for S3FileSystem {
    type FileContext = S3FileContext;

    fn get_volume_info(&self, out_volume_info: &mut VolumeInfo) -> winfsp::Result<()> {
        out_volume_info.total_size = 1024 * 1024 * 1024 * 1024;
        out_volume_info.free_size = 512 * 1024 * 1024 * 1024;
        out_volume_info.set_volume_label(&self.volume_label);
        Ok(())
    }

    fn get_security_by_name(
        &self,
        file_name: &U16CStr,
        _security_descriptor: Option<&mut [c_void]>,
        reparse_point_resolver: impl FnOnce(&U16CStr) -> Option<FileSecurity>,
    ) -> winfsp::Result<FileSecurity> {
        if let Some(sec) = reparse_point_resolver(file_name) {
            return Ok(sec);
        }
        let key = self.normalize_key(file_name);
        let meta = self
            .get_meta(&key)
            .map_err(|_| STATUS_OBJECT_PATH_NOT_FOUND)?
            .ok_or(STATUS_OBJECT_NAME_NOT_FOUND)?;

        let mut attrs = if meta.is_dir {
            FILE_ATTRIBUTE_DIRECTORY.0
        } else {
            FILE_ATTRIBUTE_NORMAL.0
        };
        if self.read_only && !meta.is_dir {
            attrs |= FILE_ATTRIBUTE_READONLY.0;
        }

        Ok(FileSecurity {
            reparse: false,
            sz_security_descriptor: 0,
            attributes: attrs,
        })
    }

    fn open(
        &self,
        file_name: &U16CStr,
        _create_options: u32,
        _granted_access: FILE_ACCESS_RIGHTS,
        file_info: &mut OpenFileInfo,
    ) -> winfsp::Result<Self::FileContext> {
        let key = self.normalize_key(file_name);
        let meta = self
            .get_meta(&key)
            .map_err(|_| STATUS_OBJECT_PATH_NOT_FOUND)?
            .ok_or(STATUS_OBJECT_NAME_NOT_FOUND)?;

        self.fill_file_info(&meta, file_info.as_mut());

        Ok(S3FileContext {
            key,
            is_dir: meta.is_dir,
            dir_buffer: DirBuffer::new(),
            state: Mutex::new(FileState {
                size: meta.size,
                last_modified: meta.last_modified,
                backend: WriteBackend::None,
                dirty: false,
                delete_on_close: false,
            }),
        })
    }

    fn create(
        &self,
        file_name: &U16CStr,
        create_options: u32,
        _granted_access: FILE_ACCESS_RIGHTS,
        _file_attributes: FILE_FLAGS_AND_ATTRIBUTES,
        _security_descriptor: Option<&[c_void]>,
        _allocation_size: u64,
        _extra_buffer: Option<&[u8]>,
        _extra_buffer_is_reparse_point: bool,
        file_info: &mut OpenFileInfo,
    ) -> winfsp::Result<Self::FileContext> {
        if self.read_only {
            return Err(STATUS_MEDIA_WRITE_PROTECTED.into());
        }
        let key = self.normalize_key(file_name);
        if self.is_root_key(&key) {
            return Err(STATUS_ACCESS_DENIED.into());
        }

        if let Ok(Some(_)) = self.get_meta(&key) {
            return Err(STATUS_OBJECT_NAME_COLLISION.into());
        }

        let is_dir = (create_options & FILE_DIRECTORY_FILE) != 0;
        if is_dir {
            let marker = format!("{key}/");
            self.client
                .put_object(&self.bucket, &marker, &[])
                .map_err(|_| STATUS_ACCESS_DENIED)?;
            self.invalidate_path(&key);
        }

        let now = now_filetime();
        let meta = CachedMeta {
            is_dir,
            size: 0,
            last_modified: now,
        };
        self.fill_file_info(&meta, file_info.as_mut());

        Ok(S3FileContext {
            key,
            is_dir,
            dir_buffer: DirBuffer::new(),
            state: Mutex::new(FileState {
                size: 0,
                last_modified: now,
                backend: if is_dir {
                    WriteBackend::None
                } else {
                    WriteBackend::Streaming {
                        buf: Vec::new(),
                        upload_id: None,
                        parts: Vec::new(),
                        written_total: 0,
                    }
                },
                dirty: !is_dir,
                delete_on_close: false,
            }),
        })
    }

    fn get_file_info(&self, context: &Self::FileContext, file_info: &mut FileInfo) -> winfsp::Result<()> {
        let state = context.state.lock().map_err(|_| STATUS_ACCESS_DENIED)?;
        let meta = CachedMeta {
            is_dir: context.is_dir,
            size: state.size,
            last_modified: state.last_modified,
        };
        self.fill_file_info(&meta, file_info);
        Ok(())
    }

    fn get_security(
        &self,
        _context: &Self::FileContext,
        _security_descriptor: Option<&mut [c_void]>,
    ) -> winfsp::Result<u64> {
        Ok(0)
    }

    fn set_security(
        &self,
        _context: &Self::FileContext,
        _security_information: u32,
        _modification_descriptor: ModificationDescriptor,
    ) -> winfsp::Result<()> {
        Ok(())
    }

    fn set_basic_info(
        &self,
        context: &Self::FileContext,
        _file_attributes: u32,
        _creation_time: u64,
        _last_access_time: u64,
        _last_write_time: u64,
        _last_change_time: u64,
        file_info: &mut FileInfo,
    ) -> winfsp::Result<()> {
        self.get_file_info(context, file_info)
    }

    fn read(
        &self,
        context: &Self::FileContext,
        buffer: &mut [u8],
        offset: u64,
    ) -> winfsp::Result<u32> {
        if context.is_dir {
            return Err(STATUS_ACCESS_DENIED.into());
        }
        let mut state = context.state.lock().map_err(|_| STATUS_ACCESS_DENIED)?;
        if offset >= state.size {
            return Ok(0);
        }

        match &mut state.backend {
            WriteBackend::Streaming {
                buf,
                upload_id: None,
                ..
            } => {
                let start = offset as usize;
                let avail = buf.len().saturating_sub(start);
                let to_copy = avail.min(buffer.len());
                buffer[..to_copy].copy_from_slice(&buf[start..start + to_copy]);
                return Ok(to_copy as u32);
            }
            WriteBackend::TempFile { file, .. } => {
                file.seek(SeekFrom::Start(offset)).map_err(|_| STATUS_ACCESS_DENIED)?;
                let n = file.read(buffer).map_err(|_| STATUS_ACCESS_DENIED)?;
                return Ok(n as u32);
            }
            _ => {}
        }

        let total_size = state.size;
        let max_len = (total_size - offset).min(buffer.len() as u64) as usize;
        drop(state);

        if self.disk_cache.is_enabled() && max_len > 0 {
            let mut copied = 0usize;
            while copied < max_len {
                let cur_off = offset + copied as u64;
                let chunk_idx = cur_off / CACHE_CHUNK_SIZE;
                let in_chunk = (cur_off % CACHE_CHUNK_SIZE) as usize;

                let chunk_data = if let Some(cached) =
                    self.disk_cache.get_chunk(&self.bucket, &context.key, chunk_idx)
                {
                    cached
                } else {
                    let chunk_start = chunk_idx * CACHE_CHUNK_SIZE;
                    let chunk_len = (total_size - chunk_start).min(CACHE_CHUNK_SIZE) as usize;
                    let fetched = self
                        .client
                        .get_object_range(&self.bucket, &context.key, chunk_start, chunk_len)
                        .map_err(|_| STATUS_ACCESS_DENIED)?;
                    self.disk_cache
                        .put_chunk(&self.bucket, &context.key, chunk_idx, &fetched);
                    fetched
                };

                if in_chunk >= chunk_data.len() {
                    break;
                }
                let avail = chunk_data.len() - in_chunk;
                let take = avail.min(max_len - copied);
                buffer[copied..copied + take].copy_from_slice(&chunk_data[in_chunk..in_chunk + take]);
                copied += take;

                // Trigger sequential range-request prefetching for next chunks
                if self.prefetch_chunks > 0 {
                    for ahead in 1..=self.prefetch_chunks {
                        let next_idx = chunk_idx + ahead;
                        let next_start = next_idx * CACHE_CHUNK_SIZE;
                        if next_start >= total_size {
                            break;
                        }
                        if self.disk_cache.try_begin_prefetch(&self.bucket, &context.key, next_idx) {
                            let client = Arc::clone(&self.client);
                            let cache = Arc::clone(&self.disk_cache);
                            let bucket = self.bucket.clone();
                            let key = context.key.clone();
                            let fetch_len = (total_size - next_start).min(CACHE_CHUNK_SIZE) as usize;
                            std::thread::spawn(move || {
                                if let Ok(data) =
                                    client.get_object_range(&bucket, &key, next_start, fetch_len)
                                {
                                    cache.put_chunk(&bucket, &key, next_idx, &data);
                                }
                                cache.end_prefetch(&bucket, &key, next_idx);
                            });
                        }
                    }
                }
            }
            return Ok(copied as u32);
        }

        let bytes = self
            .client
            .get_object_range(&self.bucket, &context.key, offset, max_len)
            .map_err(|_| STATUS_ACCESS_DENIED)?;
        let to_copy = bytes.len().min(buffer.len());
        buffer[..to_copy].copy_from_slice(&bytes[..to_copy]);
        Ok(to_copy as u32)
    }

    fn write(
        &self,
        context: &Self::FileContext,
        buffer: &[u8],
        offset: u64,
        write_to_eof: bool,
        constrained_io: bool,
        file_info: &mut FileInfo,
    ) -> winfsp::Result<u32> {
        if self.read_only {
            return Err(STATUS_MEDIA_WRITE_PROTECTED.into());
        }
        if context.is_dir {
            return Err(STATUS_ACCESS_DENIED.into());
        }

        let mut state = context.state.lock().map_err(|_| STATUS_ACCESS_DENIED)?;
        let current_size = state.size;

        if matches!(state.backend, WriteBackend::None) {
            let existing = if current_size > 0 {
                self.client
                    .get_object(&self.bucket, &context.key)
                    .map_err(|_| STATUS_ACCESS_DENIED)?
            } else {
                Vec::new()
            };
            if existing.is_empty() && offset == 0 {
                state.backend = WriteBackend::Streaming {
                    buf: Vec::new(),
                    upload_id: None,
                    parts: Vec::new(),
                    written_total: 0,
                };
            } else {
                let (file, path) = Self::open_temp_file(Some(&existing)).map_err(|_| STATUS_ACCESS_DENIED)?;
                state.backend = WriteBackend::TempFile { file, path };
            }
        }

        let target_offset = if write_to_eof { current_size } else { offset };
        if constrained_io && target_offset >= current_size {
            return Ok(0);
        }
        let write_len = if constrained_io {
            (current_size - target_offset).min(buffer.len() as u64) as usize
        } else {
            buffer.len()
        };

        // If streaming backend receives a non-sequential write, convert to TempFile if no parts uploaded yet.
        let need_convert = match &state.backend {
            WriteBackend::Streaming {
                upload_id,
                written_total,
                ..
            } => target_offset != *written_total && upload_id.is_none(),
            _ => false,
        };
        if need_convert {
            if let WriteBackend::Streaming { buf, .. } =
                std::mem::replace(&mut state.backend, WriteBackend::None)
            {
                let (file, path) = Self::open_temp_file(Some(&buf)).map_err(|_| STATUS_ACCESS_DENIED)?;
                state.backend = WriteBackend::TempFile { file, path };
            }
        }

        let mut updated_size: Option<u64> = None;
        match &mut state.backend {
            WriteBackend::Streaming {
                buf,
                upload_id,
                parts,
                written_total,
            } => {
                buf.extend_from_slice(&buffer[..write_len]);
                *written_total += write_len as u64;
                updated_size = Some(*written_total);

                while buf.len() >= self.multipart_part_size {
                    let uid = match upload_id {
                        Some(id) => id.clone(),
                        None => {
                            let id = self
                                .client
                                .create_multipart_upload(&self.bucket, &context.key)
                                .map_err(|_| STATUS_ACCESS_DENIED)?;
                            *upload_id = Some(id.clone());
                            id
                        }
                    };
                    let chunk: Vec<u8> = buf.drain(..self.multipart_part_size).collect();
                    let pn = parts.len() + 1;
                    let etag = self
                        .client
                        .upload_part(&self.bucket, &context.key, &uid, pn, &chunk)
                        .map_err(|_| STATUS_ACCESS_DENIED)?;
                    parts.push((pn, etag));
                }
            }
            WriteBackend::TempFile { file, .. } => {
                file.seek(SeekFrom::Start(target_offset))
                    .map_err(|_| STATUS_ACCESS_DENIED)?;
                file.write_all(&buffer[..write_len])
                    .map_err(|_| STATUS_ACCESS_DENIED)?;
                let end = target_offset + write_len as u64;
                if end > current_size {
                    updated_size = Some(end);
                }
            }
            WriteBackend::None => unreachable!(),
        }
        if let Some(sz) = updated_size {
            state.size = sz;
        }

        state.last_modified = now_filetime();
        state.dirty = true;

        let meta = CachedMeta {
            is_dir: false,
            size: state.size,
            last_modified: state.last_modified,
        };
        self.fill_file_info(&meta, file_info);
        Ok(write_len as u32)
    }

    fn set_file_size(
        &self,
        context: &Self::FileContext,
        new_size: u64,
        set_allocation_size: bool,
        file_info: &mut FileInfo,
    ) -> winfsp::Result<()> {
        if self.read_only {
            return Err(STATUS_MEDIA_WRITE_PROTECTED.into());
        }
        if context.is_dir {
            return Err(STATUS_ACCESS_DENIED.into());
        }
        let mut state = context.state.lock().map_err(|_| STATUS_ACCESS_DENIED)?;
        if !set_allocation_size {
            if matches!(state.backend, WriteBackend::None) {
                let existing = if state.size > 0 {
                    self.client
                        .get_object(&self.bucket, &context.key)
                        .map_err(|_| STATUS_ACCESS_DENIED)?
                } else {
                    Vec::new()
                };
                let (file, path) = Self::open_temp_file(Some(&existing)).map_err(|_| STATUS_ACCESS_DENIED)?;
                state.backend = WriteBackend::TempFile { file, path };
            }
            match &mut state.backend {
                WriteBackend::Streaming {
                    buf,
                    upload_id: None,
                    written_total,
                    ..
                } => {
                    buf.resize(new_size as usize, 0);
                    *written_total = new_size;
                }
                WriteBackend::TempFile { file, .. } => {
                    file.set_len(new_size).map_err(|_| STATUS_ACCESS_DENIED)?;
                }
                _ => {}
            }
            state.size = new_size;
            state.last_modified = now_filetime();
            state.dirty = true;
        }
        let meta = CachedMeta {
            is_dir: false,
            size: state.size,
            last_modified: state.last_modified,
        };
        self.fill_file_info(&meta, file_info);
        Ok(())
    }

    fn overwrite(
        &self,
        context: &Self::FileContext,
        _file_attributes: FILE_FLAGS_AND_ATTRIBUTES,
        _replace_file_attributes: bool,
        _allocation_size: u64,
        _extra_buffer: Option<&[u8]>,
        file_info: &mut FileInfo,
    ) -> winfsp::Result<()> {
        if self.read_only {
            return Err(STATUS_MEDIA_WRITE_PROTECTED.into());
        }
        let mut state = context.state.lock().map_err(|_| STATUS_ACCESS_DENIED)?;
        state.backend = WriteBackend::Streaming {
            buf: Vec::new(),
            upload_id: None,
            parts: Vec::new(),
            written_total: 0,
        };
        state.size = 0;
        state.last_modified = now_filetime();
        state.dirty = true;
        let meta = CachedMeta {
            is_dir: false,
            size: 0,
            last_modified: state.last_modified,
        };
        self.fill_file_info(&meta, file_info);
        Ok(())
    }

    fn flush(
        &self,
        context: Option<&Self::FileContext>,
        file_info: &mut FileInfo,
    ) -> winfsp::Result<()> {
        let Some(ctx) = context else {
            return Ok(());
        };
        let mut state = ctx.state.lock().map_err(|_| STATUS_ACCESS_DENIED)?;
        if !ctx.is_dir {
            self.flush_state(&ctx.key, &mut state)
                .map_err(|_| STATUS_ACCESS_DENIED)?;
        }
        let meta = CachedMeta {
            is_dir: ctx.is_dir,
            size: state.size,
            last_modified: state.last_modified,
        };
        self.fill_file_info(&meta, file_info);
        Ok(())
    }

    fn set_delete(
        &self,
        context: &Self::FileContext,
        _file_name: &U16CStr,
        delete_file: bool,
    ) -> winfsp::Result<()> {
        if self.read_only {
            return Err(STATUS_MEDIA_WRITE_PROTECTED.into());
        }
        if context.is_dir && delete_file {
            let children = self
                .list_dir(&context.key)
                .map_err(|_| STATUS_ACCESS_DENIED)?;
            if !children.is_empty() {
                return Err(STATUS_DIRECTORY_NOT_EMPTY.into());
            }
        }
        let mut state = context.state.lock().map_err(|_| STATUS_ACCESS_DENIED)?;
        state.delete_on_close = delete_file;
        Ok(())
    }

    fn cleanup(&self, context: &Self::FileContext, _file_name: Option<&U16CStr>, flags: u32) {
        let delete_flag = (flags & FspCleanupDelete) != 0;
        if let Ok(mut state) = context.state.lock() {
            if state.delete_on_close || delete_flag {
                if let WriteBackend::Streaming {
                    upload_id: Some(uid),
                    ..
                } = &mut state.backend
                {
                    let _ = self.client.abort_multipart_upload(&self.bucket, &context.key, uid);
                }
                let target_key = if context.is_dir {
                    format!("{}/", context.key)
                } else {
                    context.key.clone()
                };
                if self.soft_delete && !context.is_dir && !self.is_in_trash(&context.key) {
                    let _ = self.move_to_trash(&context.key);
                } else {
                    let _ = self.client.delete_object(&self.bucket, &target_key);
                }
                state.dirty = false;
                self.disk_cache.invalidate_key(&self.bucket, &context.key);
                self.invalidate_path(&context.key);
            } else if !context.is_dir {
                let _ = self.flush_state(&context.key, &mut state);
            }
        }
    }

    fn close(&self, context: Self::FileContext) {
        if let Ok(mut state) = context.state.lock() {
            if !state.delete_on_close && !context.is_dir {
                let _ = self.flush_state(&context.key, &mut state);
            }
        }
    }

    fn rename(
        &self,
        context: &Self::FileContext,
        file_name: &U16CStr,
        new_file_name: &U16CStr,
        replace_if_exists: bool,
    ) -> winfsp::Result<()> {
        if self.read_only {
            return Err(STATUS_MEDIA_WRITE_PROTECTED.into());
        }
        let old_key = self.normalize_key(file_name);
        let new_key = self.normalize_key(new_file_name);
        if old_key == new_key || new_key.is_empty() {
            return Ok(());
        }

        if !replace_if_exists {
            if let Ok(Some(_)) = self.get_meta(&new_key) {
                return Err(STATUS_OBJECT_NAME_COLLISION.into());
            }
        }

        if context.is_dir {
            let old_marker = format!("{old_key}/");
            let new_marker = format!("{new_key}/");
            let (objs, _) = self
                .client
                .list_objects(&self.bucket, &old_marker)
                .map_err(|_| STATUS_ACCESS_DENIED)?;
            let _ = self.client.put_object(&self.bucket, &new_marker, &[]);
            for obj in objs {
                if let Some(suffix) = obj.key.strip_prefix(&old_marker) {
                    let dst = format!("{new_marker}{suffix}");
                    if self.client.copy_object(&self.bucket, &obj.key, &dst).is_err() {
                        let data = self
                            .client
                            .get_object(&self.bucket, &obj.key)
                            .map_err(|_| STATUS_ACCESS_DENIED)?;
                        self.client
                            .put_object(&self.bucket, &dst, &data)
                            .map_err(|_| STATUS_ACCESS_DENIED)?;
                    }
                    let _ = self.client.delete_object(&self.bucket, &obj.key);
                }
            }
            let _ = self.client.delete_object(&self.bucket, &old_marker);
        } else {
            if self.client.copy_object(&self.bucket, &old_key, &new_key).is_err() {
                let data = self
                    .client
                    .get_object(&self.bucket, &old_key)
                    .map_err(|_| STATUS_ACCESS_DENIED)?;
                self.client
                    .put_object(&self.bucket, &new_key, &data)
                    .map_err(|_| STATUS_ACCESS_DENIED)?;
            }
            let _ = self.client.delete_object(&self.bucket, &old_key);
        }

        self.disk_cache.invalidate_prefix(&self.bucket, &old_key);
        self.disk_cache.invalidate_prefix(&self.bucket, &new_key);
        self.invalidate_path(&old_key);
        self.invalidate_path(&new_key);
        Ok(())
    }

    fn read_directory(
        &self,
        context: &Self::FileContext,
        _pattern: Option<&U16CStr>,
        marker: DirMarker,
        buffer: &mut [u8],
    ) -> winfsp::Result<u32> {
        if let Ok(lock) = context.dir_buffer.acquire(marker.is_none(), None) {
            let entries = self
                .list_dir(&context.key)
                .map_err(|_| STATUS_OBJECT_PATH_NOT_FOUND)?;

            let mut dot = DirInfo::<255>::new();
            dot.file_info_mut().file_attributes = FILE_ATTRIBUTE_DIRECTORY.0;
            let _ = dot.set_name(".");
            let _ = lock.write(&mut dot);

            let mut dotdot = DirInfo::<255>::new();
            dotdot.file_info_mut().file_attributes = FILE_ATTRIBUTE_DIRECTORY.0;
            let _ = dotdot.set_name("..");
            let _ = lock.write(&mut dotdot);

            for (name, meta) in entries {
                let mut di = DirInfo::<255>::new();
                self.fill_file_info(&meta, di.file_info_mut());
                if di.set_name(&name).is_ok() && lock.write(&mut di).is_err() {
                    break;
                }
            }
        }
        Ok(context.dir_buffer.read(marker, buffer))
    }
}
