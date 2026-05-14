use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

// ─── 接口数据结构 ─────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProxyNode {
    pub url: String,
    pub server: String,
    pub ip: String,
    pub location: String,
    pub latency: u64, // 接口返回的 latency（ms），仅供参考
    pub speed: f64,   // KB/s
    pub tag: String,
}

#[derive(Debug, Serialize, Deserialize)]
struct ApiResponse {
    code: u16,
    msg: String,
    total: usize,
    update_time: String,
    data: Vec<ProxyNode>,
}

// ─── 缓存结构 ─────────────────────────────────────────────────────────────────

#[derive(Debug, Serialize, Deserialize)]
struct CacheFile {
    /// Unix 时间戳（秒）——写入时间
    cached_at: u64,
    nodes: Vec<ProxyNode>,
}

const CACHE_TTL_SECS: u64 = 3 * 24 * 60 * 60; // 3 天
const API_URL: &str = "https://api.akams.cn/github";

fn cache_path() -> PathBuf {
    let mut exe = std::env::current_exe().unwrap_or_else(|_| PathBuf::from("."));
    exe.pop();
    let path_str = exe.to_string_lossy().to_string();
    let mut root = if path_str.contains("target\\debug") || path_str.contains("target\\release") {
        let mut p = exe.clone();
        p.pop();
        p.pop();
        p
    } else {
        exe
    };
    root.push("data");
    root.push("github_proxy_cache.json");
    root
}

fn now_secs() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

/// 从磁盘读取缓存，若未过期则返回节点列表
fn load_cache() -> Option<Vec<ProxyNode>> {
    let path = cache_path();
    if !path.exists() {
        return None;
    }
    let content = fs::read_to_string(&path).ok()?;
    let cache: CacheFile = serde_json::from_str(&content).ok()?;
    if now_secs().saturating_sub(cache.cached_at) < CACHE_TTL_SECS {
        Some(cache.nodes)
    } else {
        None
    }
}

/// 将节点列表写入磁盘缓存
fn save_cache(nodes: &[ProxyNode]) {
    let cache = CacheFile {
        cached_at: now_secs(),
        nodes: nodes.to_vec(),
    };
    let path = cache_path();
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    if let Ok(content) = serde_json::to_string_pretty(&cache) {
        let _ = fs::write(path, content);
    }
}

/// 从接口获取节点列表（阻塞）
fn fetch_nodes_from_api() -> Result<Vec<ProxyNode>, String> {
    let response = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(15))
        .build()
        .map_err(|e| e.to_string())?
        .get(API_URL)
        .send()
        .map_err(|e| format!("请求失败: {e}"))?;

    let api_resp: ApiResponse = response.json().map_err(|e| format!("解析响应失败: {e}"))?;

    if api_resp.code == 200 {
        Ok(api_resp.data)
    } else {
        Err(format!("接口错误: {}", api_resp.msg))
    }
}

// ─── 延迟测试 ─────────────────────────────────────────────────────────────────

/// 测试单个节点 URL 的实测延迟（ms），失败则返回 None
/// 通过对 {url}/favicon.ico 发起 HEAD 请求来测量
pub fn measure_latency(url: &str) -> Option<u64> {
    let test_url = format!("{}/favicon.ico", url.trim_end_matches('/'));
    let client = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(5))
        .build()
        .ok()?;
    let start = Instant::now();
    let resp = client.head(&test_url).send();
    let elapsed = start.elapsed().as_millis() as u64;
    match resp {
        Ok(_) => Some(elapsed),
        Err(_) => None,
    }
}

// ─── 公开 API ─────────────────────────────────────────────────────────────────

/// 异步状态机，供 UI 层轮询
#[derive(Debug, Clone, PartialEq)]
pub enum NodeLoadState {
    Idle,
    Loading,
    Done(Vec<NodeEntry>),
    Error(String),
}

/// 带实测延迟的节点条目
#[derive(Debug, Clone)]
pub struct NodeEntry {
    pub url: String,
    pub server: String,
    pub ip: String,
    pub location: String,
    pub speed: f64,       // KB/s
    pub api_latency: u64,  // 接口返回的参考延迟（ms）
    pub tag: String,
    /// 实测延迟（ms），None = 测试中或失败
    pub measured_ms: Arc<Mutex<Option<Option<u64>>>>,
}

impl PartialEq for NodeEntry {
    fn eq(&self, other: &Self) -> bool {
        self.url == other.url
    }
}

/// 后台线程：获取节点列表 + 并发测速，通过 channel 回传结果
pub fn start_fetch_and_test(tx: std::sync::mpsc::Sender<NodeLoadMsg>, force_refresh: bool) {
    std::thread::spawn(move || {
        // 1. 读缓存（除非强制刷新）
        let nodes = if !force_refresh { load_cache() } else { None };

        let nodes = match nodes {
            Some(n) => n,
            None => match fetch_nodes_from_api() {
                Ok(n) => {
                    save_cache(&n);
                    n
                }
                Err(e) => {
                    let _ = tx.send(NodeLoadMsg::Error(e));
                    return;
                }
            },
        };

        // 2. 构建条目列表（初始 measured_ms = None，表示"测试中"）
        let entries: Vec<NodeEntry> = nodes
            .iter()
            .map(|n| NodeEntry {
                url: n.url.clone(),
                server: n.server.clone(),
                ip: n.ip.clone(),
                location: n.location.clone(),
                speed: n.speed,
                api_latency: n.latency,
                tag: n.tag.clone(),
                measured_ms: Arc::new(Mutex::new(None)),
            })
            .collect();

        let _ = tx.send(NodeLoadMsg::Nodes(entries.clone()));

        // 3. 多线程并发测速
        let handles: Vec<_> = entries
            .iter()
            .map(|entry| {
                let url = entry.url.clone();
                let slot = Arc::clone(&entry.measured_ms);
                let tx2 = tx.clone();
                std::thread::spawn(move || {
                    let result = measure_latency(&url);
                    // Some(result)：测试完成，result 为 Some(ms) 或 None（超时）
                    if let Ok(mut guard) = slot.lock() {
                        *guard = Some(result);
                    }
                    let _ = tx2.send(NodeLoadMsg::LatencyUpdate);
                })
            })
            .collect();

        for h in handles {
            let _ = h.join();
        }

        let _ = tx.send(NodeLoadMsg::Done);
    });
}

/// 通道消息
#[derive(Debug)]
pub enum NodeLoadMsg {
    Nodes(Vec<NodeEntry>),
    LatencyUpdate,
    Done,
    Error(String),
}
