use chrono::Utc;
use hmac::{Hmac, Mac};
use quick_xml::events::Event;
use quick_xml::Reader;
use reqwest::blocking::Client;
use reqwest::header::{HeaderMap, HeaderName, HeaderValue, HOST};
use reqwest::Method;
use sha2::{Digest, Sha256};
use std::time::Duration;

type HmacSha256 = Hmac<Sha256>;

#[derive(Clone, Debug)]
pub struct S3Config {
    pub endpoint: String,
    pub region: String,
    pub access_key: String,
    pub secret_key: String,
    pub path_style: bool,
    pub custom_headers: Vec<(String, String)>,
    pub timeout_secs: u64,
    pub no_proxy: bool,
    pub proxy_url: Option<String>,
}

#[derive(Clone, Debug)]
pub struct S3Entry {
    pub key: String,
    pub size: u64,
    pub last_modified_filetime: u64,
    pub is_dir: bool,
}

#[derive(Clone, Debug)]
pub struct S3ObjectMeta {
    pub size: u64,
    pub last_modified_filetime: u64,
}

pub struct S3Client {
    config: S3Config,
    http: Client,
}

impl S3Client {
    pub fn new(config: S3Config) -> Result<Self, String> {
        let secs = config.timeout_secs.clamp(5, 600);
        let mut builder = Client::builder()
            .timeout(Duration::from_secs(secs))
            .connect_timeout(Duration::from_secs(15));
        if config.no_proxy {
            builder = builder.no_proxy();
        } else if let Some(ref p) = config.proxy_url {
            if !p.trim().is_empty() {
                let proxy = reqwest::Proxy::all(p.trim())
                    .map_err(|e| format!("Invalid proxy URL: {e}"))?;
                builder = builder.proxy(proxy);
            }
        }
        let http = builder
            .build()
            .map_err(|e| format!("HTTP client init failed: {e}"))?;
        Ok(Self { config, http })
    }

    pub fn list_buckets(&self) -> Result<Vec<String>, String> {
        let resp = self.signed_request(Method::GET, "", "", &[], &[], None)?;
        let body = resp.text().map_err(|e| format!("Read body failed: {e}"))?;
        parse_list_buckets(&body)
    }

    pub fn list_objects(
        &self,
        bucket: &str,
        prefix: &str,
    ) -> Result<(Vec<S3Entry>, Vec<String>), String> {
        let mut entries = Vec::new();
        let mut folders = Vec::new();
        let mut continuation: Option<String> = None;

        loop {
            let mut query = vec![
                ("list-type", "2".to_string()),
                ("delimiter", "/".to_string()),
                ("prefix", prefix.to_string()),
                ("max-keys", "1000".to_string()),
            ];
            if let Some(tok) = &continuation {
                query.push(("continuation-token", tok.clone()));
            }
            let query_refs: Vec<(&str, &str)> =
                query.iter().map(|(k, v)| (*k, v.as_str())).collect();

            let resp = self.signed_request(Method::GET, bucket, "", &query_refs, &[], None)?;
            let body = resp.text().map_err(|e| format!("Read body failed: {e}"))?;
            let page = parse_list_objects_v2(&body)?;
            entries.extend(page.entries);
            folders.extend(page.folders);

            if page.is_truncated {
                if let Some(next) = page.next_token {
                    continuation = Some(next);
                    continue;
                }
            }
            break;
        }
        Ok((entries, folders))
    }

    pub fn head_object(&self, bucket: &str, key: &str) -> Result<Option<S3ObjectMeta>, String> {
        let resp = self.signed_request_raw(Method::HEAD, bucket, key, &[], &[], None)?;
        if resp.status().as_u16() == 404 {
            return Ok(None);
        }
        if !resp.status().is_success() {
            return Err(format!("HEAD {key} failed: HTTP {}", resp.status()));
        }
        let size = resp
            .headers()
            .get(reqwest::header::CONTENT_LENGTH)
            .and_then(|v| v.to_str().ok())
            .and_then(|s| s.parse::<u64>().ok())
            .unwrap_or(0);
        let ft = resp
            .headers()
            .get(reqwest::header::LAST_MODIFIED)
            .and_then(|v| v.to_str().ok())
            .map(parse_rfc2822_to_filetime)
            .unwrap_or_else(now_filetime);
        Ok(Some(S3ObjectMeta {
            size,
            last_modified_filetime: ft,
        }))
    }

    pub fn get_object_range(
        &self,
        bucket: &str,
        key: &str,
        offset: u64,
        len: usize,
    ) -> Result<Vec<u8>, String> {
        if len == 0 {
            return Ok(Vec::new());
        }
        let end = offset.saturating_add(len as u64).saturating_sub(1);
        let range_val = format!("bytes={offset}-{end}");
        let extra = [("range", range_val.as_str())];
        let resp = self.signed_request(Method::GET, bucket, key, &[], &extra, None)?;
        resp.bytes()
            .map(|b| b.to_vec())
            .map_err(|e| format!("Read bytes failed: {e}"))
    }

    pub fn get_object(&self, bucket: &str, key: &str) -> Result<Vec<u8>, String> {
        let resp = self.signed_request(Method::GET, bucket, key, &[], &[], None)?;
        resp.bytes()
            .map(|b| b.to_vec())
            .map_err(|e| format!("Read bytes failed: {e}"))
    }

    pub fn put_object(&self, bucket: &str, key: &str, data: &[u8]) -> Result<(), String> {
        self.signed_request(Method::PUT, bucket, key, &[], &[], Some(data))?;
        Ok(())
    }

    pub fn delete_object(&self, bucket: &str, key: &str) -> Result<(), String> {
        self.signed_request(Method::DELETE, bucket, key, &[], &[], None)?;
        Ok(())
    }

    pub fn copy_object(&self, bucket: &str, src_key: &str, dst_key: &str) -> Result<(), String> {
        let copy_src = format!("/{}/{}", bucket, uri_encode_path(src_key));
        let extra = [("x-amz-copy-source", copy_src.as_str())];
        self.signed_request(Method::PUT, bucket, dst_key, &[], &extra, Some(&[]))?;
        Ok(())
    }

    pub fn create_multipart_upload(&self, bucket: &str, key: &str) -> Result<String, String> {
        let resp = self.signed_request(Method::POST, bucket, key, &[("uploads", "")], &[], Some(&[]))?;
        let body = resp.text().map_err(|e| format!("Read uploadId body: {e}"))?;
        parse_upload_id(&body)
    }

    pub fn upload_part(
        &self,
        bucket: &str,
        key: &str,
        upload_id: &str,
        part_number: usize,
        data: &[u8],
    ) -> Result<String, String> {
        let pn = part_number.to_string();
        let resp = self.signed_request(
            Method::PUT,
            bucket,
            key,
            &[("partNumber", pn.as_str()), ("uploadId", upload_id)],
            &[],
            Some(data),
        )?;
        let etag = resp
            .headers()
            .get(reqwest::header::ETAG)
            .and_then(|v| v.to_str().ok())
            .map(|s| s.to_string())
            .ok_or_else(|| "Missing ETag on UploadPart".to_string())?;
        Ok(etag)
    }

    pub fn complete_multipart_upload(
        &self,
        bucket: &str,
        key: &str,
        upload_id: &str,
        parts: &[(usize, String)],
    ) -> Result<(), String> {
        let mut xml = String::from("<CompleteMultipartUpload>");
        for (pn, etag) in parts {
            xml.push_str(&format!(
                "<Part><PartNumber>{pn}</PartNumber><ETag>{etag}</ETag></Part>"
            ));
        }
        xml.push_str("</CompleteMultipartUpload>");
        self.signed_request(
            Method::POST,
            bucket,
            key,
            &[("uploadId", upload_id)],
            &[],
            Some(xml.as_bytes()),
        )?;
        Ok(())
    }

    pub fn abort_multipart_upload(&self, bucket: &str, key: &str, upload_id: &str) -> Result<(), String> {
        self.signed_request(
            Method::DELETE,
            bucket,
            key,
            &[("uploadId", upload_id)],
            &[],
            None,
        )?;
        Ok(())
    }

    fn signed_request(
        &self,
        method: Method,
        bucket: &str,
        key: &str,
        query: &[(&str, &str)],
        extra_headers: &[(&str, &str)],
        body: Option<&[u8]>,
    ) -> Result<reqwest::blocking::Response, String> {
        let resp = self.signed_request_raw(method.clone(), bucket, key, query, extra_headers, body)?;
        let status = resp.status();
        if !status.is_success() {
            let err_body = resp.text().unwrap_or_default();
            return Err(format!("S3 {} {} failed ({status}): {err_body}", method, key));
        }
        Ok(resp)
    }

    fn signed_request_raw(
        &self,
        method: Method,
        bucket: &str,
        key: &str,
        query: &[(&str, &str)],
        extra_headers: &[(&str, &str)],
        body: Option<&[u8]>,
    ) -> Result<reqwest::blocking::Response, String> {
        let endpoint = self.config.endpoint.trim_end_matches('/');
        let (scheme, host_port) = if let Some(rest) = endpoint.strip_prefix("https://") {
            ("https", rest)
        } else if let Some(rest) = endpoint.strip_prefix("http://") {
            ("http", rest)
        } else {
            ("https", endpoint)
        };

        let encoded_key = uri_encode_path(key);
        let (req_host, canonical_uri) = if bucket.is_empty() {
            (host_port.to_string(), "/".to_string())
        } else if self.config.path_style {
            let uri = if encoded_key.is_empty() {
                format!("/{bucket}")
            } else {
                format!("/{bucket}/{encoded_key}")
            };
            (host_port.to_string(), uri)
        } else {
            let uri = if encoded_key.is_empty() {
                "/".to_string()
            } else {
                format!("/{encoded_key}")
            };
            (format!("{bucket}.{host_port}"), uri)
        };

        let mut sorted_query: Vec<(String, String)> = query
            .iter()
            .map(|(k, v)| (uri_encode(k), uri_encode(v)))
            .collect();
        sorted_query.sort();
        let canonical_query = sorted_query
            .iter()
            .map(|(k, v)| format!("{k}={v}"))
            .collect::<Vec<_>>()
            .join("&");

        let payload = body.unwrap_or(&[]);
        let payload_hash = hex::encode(Sha256::digest(payload));

        let now = Utc::now();
        let amz_date = now.format("%Y%m%dT%H%M%SZ").to_string();
        let date_stamp = now.format("%Y%m%d").to_string();

        let mut sign_headers: Vec<(String, String)> = vec![
            ("host".to_string(), req_host.clone()),
            ("x-amz-content-sha256".to_string(), payload_hash.clone()),
            ("x-amz-date".to_string(), amz_date.clone()),
        ];
        for (k, v) in extra_headers {
            sign_headers.push((k.to_ascii_lowercase(), v.trim().to_string()));
        }
        sign_headers.sort_by(|a, b| a.0.cmp(&b.0));

        let canonical_headers = sign_headers
            .iter()
            .map(|(k, v)| format!("{k}:{v}\n"))
            .collect::<String>();
        let signed_headers = sign_headers
            .iter()
            .map(|(k, _)| k.as_str())
            .collect::<Vec<_>>()
            .join(";");

        let canonical_request = format!(
            "{}\n{}\n{}\n{}\n{}\n{}",
            method.as_str(),
            canonical_uri,
            canonical_query,
            canonical_headers,
            signed_headers,
            payload_hash
        );

        let region = if self.config.region.trim().is_empty() {
            "auto"
        } else {
            self.config.region.trim()
        };
        let scope = format!("{date_stamp}/{region}/s3/aws4_request");
        let string_to_sign = format!(
            "AWS4-HMAC-SHA256\n{amz_date}\n{scope}\n{}",
            hex::encode(Sha256::digest(canonical_request.as_bytes()))
        );

        let k_date = hmac_sha256(format!("AWS4{}", self.config.secret_key).as_bytes(), date_stamp.as_bytes());
        let k_region = hmac_sha256(&k_date, region.as_bytes());
        let k_service = hmac_sha256(&k_region, b"s3");
        let k_signing = hmac_sha256(&k_service, b"aws4_request");
        let signature = hex::encode(hmac_sha256(&k_signing, string_to_sign.as_bytes()));

        let auth_header = format!(
            "AWS4-HMAC-SHA256 Credential={}/{scope}, SignedHeaders={signed_headers}, Signature={signature}",
            self.config.access_key
        );

        let url = if canonical_query.is_empty() {
            format!("{scheme}://{req_host}{canonical_uri}")
        } else {
            format!("{scheme}://{req_host}{canonical_uri}?{canonical_query}")
        };

        let mut headers = HeaderMap::new();
        headers.insert(
            HOST,
            HeaderValue::from_str(&req_host).map_err(|e| format!("Invalid host header: {e}"))?,
        );
        headers.insert(
            HeaderName::from_static("x-amz-date"),
            HeaderValue::from_str(&amz_date).unwrap(),
        );
        headers.insert(
            HeaderName::from_static("x-amz-content-sha256"),
            HeaderValue::from_str(&payload_hash).unwrap(),
        );
        headers.insert(
            reqwest::header::AUTHORIZATION,
            HeaderValue::from_str(&auth_header).map_err(|e| format!("Invalid auth header: {e}"))?,
        );
        for (k, v) in extra_headers {
            let hn = HeaderName::from_bytes(k.as_bytes()).map_err(|e| format!("Header name {k}: {e}"))?;
            let hv = HeaderValue::from_str(v).map_err(|e| format!("Header val {v}: {e}"))?;
            headers.insert(hn, hv);
        }
        for (k, v) in &self.config.custom_headers {
            let k_trim = k.trim();
            if k_trim.is_empty() {
                continue;
            }
            if let (Ok(hn), Ok(hv)) = (
                HeaderName::from_bytes(k_trim.as_bytes()),
                HeaderValue::from_str(v),
            ) {
                headers.entry(hn).or_insert(hv);
            }
        }

        let mut builder = self.http.request(method, &url).headers(headers);
        if let Some(bytes) = body {
            builder = builder.body(bytes.to_vec());
        }
        builder.send().map_err(|e| format!("HTTP request failed: {e}"))
    }
}

fn hmac_sha256(key: &[u8], data: &[u8]) -> Vec<u8> {
    let mut mac = HmacSha256::new_from_slice(key).expect("HMAC key");
    mac.update(data);
    mac.finalize().into_bytes().to_vec()
}

fn uri_encode(input: &str) -> String {
    let mut out = String::with_capacity(input.len());
    for b in input.as_bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'_' | b'-' | b'~' | b'.' => {
                out.push(*b as char);
            }
            _ => out.push_str(&format!("%{:02X}", b)),
        }
    }
    out
}

fn uri_encode_path(path: &str) -> String {
    path.split('/')
        .map(uri_encode)
        .collect::<Vec<_>>()
        .join("/")
}

struct ListPage {
    entries: Vec<S3Entry>,
    folders: Vec<String>,
    is_truncated: bool,
    next_token: Option<String>,
}

fn parse_list_buckets(xml: &str) -> Result<Vec<String>, String> {
    let mut reader = Reader::from_str(xml);
    let mut buckets = Vec::new();
    let mut in_bucket = false;
    let mut in_name = false;
    let mut buf = Vec::new();
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => match e.local_name().as_ref() {
                b"Bucket" => in_bucket = true,
                b"Name" if in_bucket => in_name = true,
                _ => {}
            },
            Ok(Event::End(e)) => match e.local_name().as_ref() {
                b"Bucket" => in_bucket = false,
                b"Name" => in_name = false,
                _ => {}
            },
            Ok(Event::Text(e)) if in_name => {
                if let Ok(text) = e.unescape() {
                    buckets.push(text.into_owned());
                }
            }
            Ok(Event::Eof) => break,
            Err(e) => return Err(format!("XML parse error: {e}")),
            _ => {}
        }
        buf.clear();
    }
    Ok(buckets)
}

fn parse_list_objects_v2(xml: &str) -> Result<ListPage, String> {
    let mut reader = Reader::from_str(xml);
    let mut buf = Vec::new();

    let mut entries = Vec::new();
    let mut folders = Vec::new();
    let mut is_truncated = false;
    let mut next_token = None;

    let mut in_contents = false;
    let mut in_common_prefixes = false;
    let mut current_tag = String::new();

    let mut cur_key = String::new();
    let mut cur_size: u64 = 0;
    let mut cur_ft: u64 = 0;

    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) => {
                let name = String::from_utf8_lossy(e.local_name().as_ref()).into_owned();
                if name == "Contents" {
                    in_contents = true;
                    cur_key.clear();
                    cur_size = 0;
                    cur_ft = now_filetime();
                } else if name == "CommonPrefixes" {
                    in_common_prefixes = true;
                }
                current_tag = name;
            }
            Ok(Event::End(e)) => {
                let name = String::from_utf8_lossy(e.local_name().as_ref()).into_owned();
                if name == "Contents" {
                    in_contents = false;
                    if !cur_key.is_empty() {
                        let is_dir = cur_key.ends_with('/');
                        entries.push(S3Entry {
                            key: cur_key.clone(),
                            size: if is_dir { 0 } else { cur_size },
                            last_modified_filetime: cur_ft,
                            is_dir,
                        });
                    }
                } else if name == "CommonPrefixes" {
                    in_common_prefixes = false;
                }
                current_tag.clear();
            }
            Ok(Event::Text(e)) => {
                let text = e.unescape().map_err(|e| format!("XML text: {e}"))?;
                let val = text.trim();
                if in_contents {
                    match current_tag.as_str() {
                        "Key" => cur_key = val.to_string(),
                        "Size" => cur_size = val.parse::<u64>().unwrap_or(0),
                        "LastModified" => cur_ft = parse_iso8601_to_filetime(val),
                        _ => {}
                    }
                } else if in_common_prefixes && current_tag == "Prefix" {
                    folders.push(val.to_string());
                } else {
                    match current_tag.as_str() {
                        "IsTruncated" => is_truncated = val.eq_ignore_ascii_case("true"),
                        "NextContinuationToken" => next_token = Some(val.to_string()),
                        _ => {}
                    }
                }
            }
            Ok(Event::Eof) => break,
            Err(e) => return Err(format!("XML parse error: {e}")),
            _ => {}
        }
        buf.clear();
    }

    Ok(ListPage {
        entries,
        folders,
        is_truncated,
        next_token,
    })
}

pub fn now_filetime() -> u64 {
    unix_secs_to_filetime(Utc::now().timestamp())
}

fn unix_secs_to_filetime(secs: i64) -> u64 {
    const EPOCH_DIFF_SECS: i64 = 11_644_473_600;
    let total = secs.saturating_add(EPOCH_DIFF_SECS).max(0) as u64;
    total.saturating_mul(10_000_000)
}

fn parse_iso8601_to_filetime(s: &str) -> u64 {
    chrono::DateTime::parse_from_rfc3339(s)
        .map(|dt| unix_secs_to_filetime(dt.timestamp()))
        .unwrap_or_else(|_| now_filetime())
}

fn parse_rfc2822_to_filetime(s: &str) -> u64 {
    chrono::DateTime::parse_from_rfc2822(s)
        .map(|dt| unix_secs_to_filetime(dt.timestamp()))
        .unwrap_or_else(|_| now_filetime())
}

fn parse_upload_id(xml: &str) -> Result<String, String> {
    let mut reader = Reader::from_str(xml);
    let mut buf = Vec::new();
    let mut in_id = false;
    loop {
        match reader.read_event_into(&mut buf) {
            Ok(Event::Start(e)) if e.local_name().as_ref() == b"UploadId" => in_id = true,
            Ok(Event::End(e)) if e.local_name().as_ref() == b"UploadId" => in_id = false,
            Ok(Event::Text(e)) if in_id => {
                return e.unescape().map(|s| s.trim().to_string()).map_err(|e| e.to_string());
            }
            Ok(Event::Eof) => break,
            Err(e) => return Err(format!("XML parse error: {e}")),
            _ => {}
        }
        buf.clear();
    }
    Err("UploadId not found in InitiateMultipartUploadResult".to_string())
}
