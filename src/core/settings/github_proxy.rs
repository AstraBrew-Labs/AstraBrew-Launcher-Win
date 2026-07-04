use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

// ─── 节点数据结构 ─────────────────────────────────────────────────────────────

#[derive(Debug, Clone)]
pub struct ProxyNode {
    pub url: String,
    pub tag: String,
    /// 节点来源："第三方"
    pub source: String,
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

/// 节点加载状态（简化版，无 API 获取）
#[derive(Debug, Clone, PartialEq)]
pub enum NodeLoadState {
    Loading,
    Done(Vec<NodeEntry>),
}

/// 带实测延迟的节点条目
#[derive(Debug, Clone)]
pub struct NodeEntry {
    pub url: String,
    pub tag: String,
    pub source: String,   // "第三方"
    /// 实测延迟（ms），None = 测试中或失败
    pub measured_ms: Arc<Mutex<Option<Option<u64>>>>,
}

impl PartialEq for NodeEntry {
    fn eq(&self, other: &Self) -> bool {
        self.url == other.url
    }
}

/// 后台线程：加载默认节点列表 + 并发测速
pub fn start_fetch_and_test(tx: std::sync::mpsc::Sender<NodeLoadMsg>, _force_refresh: bool) {
    std::thread::spawn(move || {
        let nodes = fallback_nodes();
        let _entries = build_and_test_nodes(&nodes, &tx);
        let _ = tx.send(NodeLoadMsg::Done);
    });
}

/// 将 ProxyNode 列表转为 NodeEntry 列表并启动并发测速
fn build_and_test_nodes(
    nodes: &[ProxyNode],
    tx: &std::sync::mpsc::Sender<NodeLoadMsg>,
) -> Vec<NodeEntry> {
    let entries: Vec<NodeEntry> = nodes
        .iter()
        .map(|n| NodeEntry {
            url: n.url.clone(),
            tag: n.tag.clone(),
            source: n.source.clone(),
            measured_ms: Arc::new(Mutex::new(None)),
        })
        .collect();

    let _ = tx.send(NodeLoadMsg::Nodes(entries.clone()));

    let handles: Vec<_> = entries
        .iter()
        .map(|entry| {
            let url = entry.url.clone();
            let slot = Arc::clone(&entry.measured_ms);
            let tx2 = tx.clone();
            std::thread::spawn(move || {
                let result = measure_latency(&url);
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

    entries
}

/// 默认节点列表
fn fallback_nodes() -> Vec<ProxyNode> {
    vec![
        ProxyNode {
            url: "https://gh-proxy.org/".to_string(),
            tag: "首选".to_string(),
            source: "第三方".to_string(),
        },
        ProxyNode {
            url: "https://ghfast.top/".to_string(),
            tag: "备用".to_string(),
            source: "第三方".to_string(),
        },
        ProxyNode {
            url: "https://github-proxy.memory-echoes.cn/".to_string(),
            tag: "备用".to_string(),
            source: "第三方".to_string(),
        },
        ProxyNode {
            url: "https://github.dpik.top/".to_string(),
            tag: "备用".to_string(),
            source: "第三方".to_string(),
        },
    ]
}

/// 通道消息
#[derive(Debug)]
pub enum NodeLoadMsg {
    Nodes(Vec<NodeEntry>),
    LatencyUpdate,
    Done,
}
