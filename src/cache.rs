use crate::s3::S3Client;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet, VecDeque};
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};

pub const CACHE_CHUNK_SIZE: u64 = 4 * 1024 * 1024; // 4 MB

#[derive(Clone, Debug, serde::Serialize)]
pub struct TransferItem {
    pub id: u64,
    pub bucket: String,
    pub key: String,
    pub kind: String,
    pub bytes_done: u64,
    pub bytes_total: u64,
    pub status: String,
    pub error: Option<String>,
}

pub struct TransferTracker {
    next_id: AtomicU64,
    items: Mutex<Vec<TransferItem>>,
}

impl TransferTracker {
    pub fn new() -> Self {
        Self {
            next_id: AtomicU64::new(1),
            items: Mutex::new(Vec::new()),
        }
    }

    pub fn start_transfer(&self, bucket: &str, key: &str, kind: &str, total: u64, status: &str) -> u64 {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        if let Ok(mut list) = self.items.lock() {
            list.push(TransferItem {
                id,
                bucket: bucket.to_string(),
                key: key.to_string(),
                kind: kind.to_string(),
                bytes_done: 0,
                bytes_total: total,
                status: status.to_string(),
                error: None,
            });
            if list.len() > 50 {
                // Keep recent 50 items, removing oldest completed/errored first.
                if let Some(pos) = list.iter().position(|x| x.status == "done" || x.status == "error") {
                    list.remove(pos);
                } else {
                    list.remove(0);
                }
            }
        }
        id
    }

    pub fn update_progress(&self, id: u64, done: u64, status: Option<&str>) {
        if let Ok(mut list) = self.items.lock() {
            if let Some(item) = list.iter_mut().find(|x| x.id == id) {
                item.bytes_done = done.min(item.bytes_total);
                if let Some(st) = status {
                    item.status = st.to_string();
                }
            }
        }
    }

    pub fn finish(&self, id: u64, err: Option<String>) {
        if let Ok(mut list) = self.items.lock() {
            if let Some(item) = list.iter_mut().find(|x| x.id == id) {
                if let Some(e) = err {
                    item.status = "error".to_string();
                    item.error = Some(e);
                } else {
                    item.bytes_done = item.bytes_total;
                    item.status = "done".to_string();
                }
            }
        }
    }

    pub fn snapshot(&self) -> Vec<TransferItem> {
        self.items.lock().map(|l| l.clone()).unwrap_or_default()
    }
}

struct CacheEntry {
    path: PathBuf,
    size: u64,
    seq: u64,
}

struct DiskCacheInner {
    entries: HashMap<(String, u64), CacheEntry>,
    total_bytes: u64,
    seq_counter: u64,
    max_bytes: u64,
    enabled: bool,
}

pub struct DiskCache {
    root_dir: PathBuf,
    inner: Mutex<DiskCacheInner>,
    inflight_prefetch: Mutex<HashSet<(String, u64)>>,
}

impl DiskCache {
    pub fn new(root_dir: PathBuf, max_mb: u64, enabled: bool) -> Self {
        let _ = fs::create_dir_all(&root_dir);
        // Clean stale cache files on fresh start to keep index consistent.
        if let Ok(rd) = fs::read_dir(&root_dir) {
            for e in rd.flatten() {
                let p = e.path();
                if p.is_file() {
                    let _ = fs::remove_file(p);
                }
            }
        }
        Self {
            root_dir,
            inner: Mutex::new(DiskCacheInner {
                entries: HashMap::new(),
                total_bytes: 0,
                seq_counter: 0,
                max_bytes: max_mb.saturating_mul(1024 * 1024),
                enabled,
            }),
            inflight_prefetch: Mutex::new(HashSet::new()),
        }
    }

    pub fn set_limits(&self, max_mb: u64, enabled: bool) {
        if let Ok(mut g) = self.inner.lock() {
            g.max_bytes = max_mb.saturating_mul(1024 * 1024);
            g.enabled = enabled;
            if !enabled {
                Self::clear_inner(&mut g);
            } else {
                Self::evict_to_limit(&mut g);
            }
        }
    }

    pub fn is_enabled(&self) -> bool {
        self.inner.lock().map(|g| g.enabled && g.max_bytes > 0).unwrap_or(false)
    }

    fn object_id(bucket: &str, key: &str) -> String {
        format!("{bucket}/{key}")
    }

    fn chunk_path(&self, obj_id: &str, chunk_idx: u64) -> PathBuf {
        let hash = hex::encode(Sha256::digest(obj_id.as_bytes()));
        self.root_dir.join(format!("{}_{}.chk", &hash[..24], chunk_idx))
    }

    pub fn get_chunk(&self, bucket: &str, key: &str, chunk_idx: u64) -> Option<Vec<u8>> {
        let obj_id = Self::object_id(bucket, key);
        let path = {
            let mut g = self.inner.lock().ok()?;
            if !g.enabled {
                return None;
            }
            g.seq_counter += 1;
            let seq = g.seq_counter;
            let entry = g.entries.get_mut(&(obj_id.clone(), chunk_idx))?;
            entry.seq = seq;
            entry.path.clone()
        };
        match fs::read(&path) {
            Ok(bytes) => Some(bytes),
            Err(_) => {
                if let Ok(mut g) = self.inner.lock() {
                    if let Some(removed) = g.entries.remove(&(obj_id, chunk_idx)) {
                        g.total_bytes = g.total_bytes.saturating_sub(removed.size);
                    }
                }
                None
            }
        }
    }

    pub fn put_chunk(&self, bucket: &str, key: &str, chunk_idx: u64, data: &[u8]) {
        let obj_id = Self::object_id(bucket, key);
        let path = self.chunk_path(&obj_id, chunk_idx);
        let size = data.len() as u64;

        {
            let g = match self.inner.lock() {
                Ok(g) => g,
                Err(_) => return,
            };
            if !g.enabled || g.max_bytes == 0 || size > g.max_bytes {
                return;
            }
        }

        if fs::write(&path, data).is_err() {
            return;
        }

        if let Ok(mut g) = self.inner.lock() {
            if !g.enabled {
                let _ = fs::remove_file(&path);
                return;
            }
            g.seq_counter += 1;
            let seq = g.seq_counter;
            if let Some(old) = g.entries.insert(
                (obj_id, chunk_idx),
                CacheEntry { path, size, seq },
            ) {
                g.total_bytes = g.total_bytes.saturating_sub(old.size);
            }
            g.total_bytes = g.total_bytes.saturating_add(size);
            Self::evict_to_limit(&mut g);
        }
    }

    pub fn invalidate_key(&self, bucket: &str, key: &str) {
        let obj_id = Self::object_id(bucket, key);
        if let Ok(mut g) = self.inner.lock() {
            let keys: Vec<(String, u64)> = g
                .entries
                .keys()
                .filter(|(id, _)| id == &obj_id)
                .cloned()
                .collect();
            for k in keys {
                if let Some(e) = g.entries.remove(&k) {
                    g.total_bytes = g.total_bytes.saturating_sub(e.size);
                    let _ = fs::remove_file(e.path);
                }
            }
        }
    }

    pub fn invalidate_prefix(&self, bucket: &str, prefix: &str) {
        let obj_prefix = Self::object_id(bucket, prefix);
        if let Ok(mut g) = self.inner.lock() {
            let keys: Vec<(String, u64)> = g
                .entries
                .keys()
                .filter(|(id, _)| id == &obj_prefix || id.starts_with(&format!("{obj_prefix}/")))
                .cloned()
                .collect();
            for k in keys {
                if let Some(e) = g.entries.remove(&k) {
                    g.total_bytes = g.total_bytes.saturating_sub(e.size);
                    let _ = fs::remove_file(e.path);
                }
            }
        }
    }

    pub fn clear(&self) {
        if let Ok(mut g) = self.inner.lock() {
            Self::clear_inner(&mut g);
        }
    }

    pub fn stats(&self) -> (u64, usize) {
        self.inner
            .lock()
            .map(|g| (g.total_bytes, g.entries.len()))
            .unwrap_or((0, 0))
    }

    pub fn try_begin_prefetch(&self, bucket: &str, key: &str, chunk_idx: u64) -> bool {
        let obj_id = Self::object_id(bucket, key);
        if let Ok(g) = self.inner.lock() {
            if !g.enabled || g.entries.contains_key(&(obj_id.clone(), chunk_idx)) {
                return false;
            }
        }
        if let Ok(mut inf) = self.inflight_prefetch.lock() {
            return inf.insert((obj_id, chunk_idx));
        }
        false
    }

    pub fn end_prefetch(&self, bucket: &str, key: &str, chunk_idx: u64) {
        let obj_id = Self::object_id(bucket, key);
        if let Ok(mut inf) = self.inflight_prefetch.lock() {
            inf.remove(&(obj_id, chunk_idx));
        }
    }

    fn clear_inner(g: &mut DiskCacheInner) {
        for (_, e) in g.entries.drain() {
            let _ = fs::remove_file(e.path);
        }
        g.total_bytes = 0;
    }

    fn evict_to_limit(g: &mut DiskCacheInner) {
        while g.total_bytes > g.max_bytes && !g.entries.is_empty() {
            let lru_key = g
                .entries
                .iter()
                .min_by_key(|(_, e)| e.seq)
                .map(|(k, _)| k.clone());
            if let Some(k) = lru_key {
                if let Some(e) = g.entries.remove(&k) {
                    g.total_bytes = g.total_bytes.saturating_sub(e.size);
                    let _ = fs::remove_file(e.path);
                }
            } else {
                break;
            }
        }
    }
}

pub struct UploadJob {
    pub transfer_id: u64,
    pub client: Arc<S3Client>,
    pub bucket: String,
    pub key: String,
    pub staging_path: PathBuf,
    pub size: u64,
    pub part_size: usize,
    pub concurrency: usize,
}

pub struct WriteBackQueue {
    queue: Arc<(Mutex<VecDeque<UploadJob>>, Condvar)>,
    tracker: Arc<TransferTracker>,
}

impl WriteBackQueue {
    pub fn new(workers: usize, tracker: Arc<TransferTracker>) -> Self {
        let queue = Arc::new((Mutex::new(VecDeque::<UploadJob>::new()), Condvar::new()));
        let count = workers.clamp(1, 8);
        for _ in 0..count {
            let q = Arc::clone(&queue);
            let tr = Arc::clone(&tracker);
            std::thread::spawn(move || loop {
                let job = {
                    let (lock, cvar) = &*q;
                    let mut guard = match lock.lock() {
                        Ok(g) => g,
                        Err(_) => return,
                    };
                    while guard.is_empty() {
                        guard = match cvar.wait(guard) {
                            Ok(g) => g,
                            Err(_) => return,
                        };
                    }
                    guard.pop_front()
                };
                if let Some(j) = job {
                    Self::process_job(j, &tr);
                }
            });
        }
        Self { queue, tracker }
    }

    pub fn enqueue(&self, job: UploadJob) {
        let (lock, cvar) = &*self.queue;
        if let Ok(mut guard) = lock.lock() {
            guard.push_back(job);
            cvar.notify_one();
        }
    }

    pub fn pending_count(&self) -> usize {
        self.queue.0.lock().map(|g| g.len()).unwrap_or(0)
    }

    pub fn tracker(&self) -> Arc<TransferTracker> {
        Arc::clone(&self.tracker)
    }

    fn process_job(job: UploadJob, tracker: &TransferTracker) {
        tracker.update_progress(job.transfer_id, 0, Some("active"));
        let res = upload_file_concurrent(
            &job.client,
            &job.bucket,
            &job.key,
            &job.staging_path,
            job.size,
            job.part_size,
            job.concurrency,
            |done| tracker.update_progress(job.transfer_id, done, Some("active")),
        );
        let _ = fs::remove_file(&job.staging_path);
        match res {
            Ok(()) => tracker.finish(job.transfer_id, None),
            Err(e) => tracker.finish(job.transfer_id, Some(e)),
        }
    }
}

pub fn upload_file_concurrent<F>(
    client: &Arc<S3Client>,
    bucket: &str,
    key: &str,
    path: &PathBuf,
    size: u64,
    part_size: usize,
    concurrency: usize,
    on_progress: F,
) -> Result<(), String>
where
    F: Fn(u64) + Send + Sync,
{
    if size <= part_size as u64 {
        let data = fs::read(path).map_err(|e| format!("Read staging failed: {e}"))?;
        client.put_object(bucket, key, &data)?;
        on_progress(size);
        return Ok(());
    }

    let uid = client.create_multipart_upload(bucket, key)?;
    let part_sz = part_size.max(5 * 1024 * 1024) as u64;
    let total_parts = ((size + part_sz - 1) / part_sz) as usize;
    let workers = concurrency.clamp(1, 8).min(total_parts);

    let next_part = Mutex::new(0usize);
    let uploaded_bytes = AtomicU64::new(0);
    let results: Mutex<Vec<(usize, String)>> = Mutex::new(Vec::with_capacity(total_parts));
    let first_err: Mutex<Option<String>> = Mutex::new(None);

    std::thread::scope(|s| {
        for _ in 0..workers {
            s.spawn(|| loop {
                if first_err.lock().map(|g| g.is_some()).unwrap_or(true) {
                    break;
                }
                let idx = {
                    let mut g = match next_part.lock() {
                        Ok(g) => g,
                        Err(_) => break,
                    };
                    if *g >= total_parts {
                        break;
                    }
                    let cur = *g;
                    *g += 1;
                    cur
                };

                let offset = idx as u64 * part_sz;
                let len = (size - offset).min(part_sz) as usize;
                let mut buf = vec![0u8; len];
                let read_res = (|| -> Result<(), String> {
                    use std::io::{Read, Seek, SeekFrom};
                    let mut f = fs::File::open(path).map_err(|e| format!("Open staging: {e}"))?;
                    f.seek(SeekFrom::Start(offset))
                        .map_err(|e| format!("Seek staging: {e}"))?;
                    f.read_exact(&mut buf)
                        .map_err(|e| format!("Read staging part: {e}"))?;
                    Ok(())
                })();

                if let Err(e) = read_res {
                    if let Ok(mut err_g) = first_err.lock() {
                        if err_g.is_none() {
                            *err_g = Some(e);
                        }
                    }
                    break;
                }

                let pn = idx + 1;
                match client.upload_part(bucket, key, &uid, pn, &buf) {
                    Ok(etag) => {
                        if let Ok(mut res_g) = results.lock() {
                            res_g.push((pn, etag));
                        }
                        let done = uploaded_bytes.fetch_add(len as u64, Ordering::Relaxed) + len as u64;
                        on_progress(done);
                    }
                    Err(e) => {
                        if let Ok(mut err_g) = first_err.lock() {
                            if err_g.is_none() {
                                *err_g = Some(e);
                            }
                        }
                        break;
                    }
                }
            });
        }
    });

    if let Some(err) = first_err.into_inner().ok().flatten() {
        let _ = client.abort_multipart_upload(bucket, key, &uid);
        return Err(err);
    }

    let mut parts = results.into_inner().map_err(|_| "Lock error".to_string())?;
    parts.sort_by_key(|(pn, _)| *pn);
    client.complete_multipart_upload(bucket, key, &uid, &parts)
}
