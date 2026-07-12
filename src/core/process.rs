#![allow(dead_code)]

use std::io::{BufRead, BufReader};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::{Arc, Mutex, mpsc::Sender};
use std::os::windows::process::CommandExt;

use crate::pages::console::ConsoleStatus;
use crate::pages::settings::{EnvSource, ProxyType, SettingsState, TavernDataMode};

const CREATE_NO_WINDOW: u32 = 0x08000000;

// ---- 消息类型 ----

/// UI → 进程管理器的命令
#[derive(Clone)]
pub enum ConsoleCommand {
    Start,
    Stop,
    ForceStop,
}

/// 进程管理器 → UI 的消息
pub enum ProcessMsg {
    Log(String),
    StateChange(ConsoleStatus),
}

// ---- 公共入口 ----

/// 在后台线程中启动酒馆服务
pub fn start_tavern(
    tx: Sender<ProcessMsg>,
    settings: &SettingsState,
    child_handle: Arc<Mutex<Option<Child>>>,
) {
    let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Starting));

    // --- 第一步：检查 Node.js 环境 ---
    let _ = tx.send(ProcessMsg::Log("[系统] 正在检查 Node.js 环境...".to_string()));
    let node_path = match settings.nodejs_env {
        EnvSource::Builtin => crate::core::env::get_builtin_node_path(),
        EnvSource::System => crate::core::env::get_system_cmd_path("node"),
    };
    let node_path = match node_path {
        Some(p) => {
            let ver = crate::core::env::get_cmd_version(&p).unwrap_or_else(|| "Unknown".to_string());
            let _ = tx.send(ProcessMsg::Log(format!("[系统] Node.js 环境检查通过: {}", ver)));
            p
        }
        None => {
            let _ = tx.send(ProcessMsg::Log(
                "[错误] Node.js 环境缺失，请在设置中配置正确的 Node.js 环境来源".to_string(),
            ));
            let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Stopped));
            return;
        }
    };

    // --- 第二步：检查 Git 环境 ---
    let _ = tx.send(ProcessMsg::Log("[系统] 正在检查 Git 环境...".to_string()));
    let git_path = match settings.git_env {
        EnvSource::Builtin => crate::core::env::get_builtin_git_path(),
        EnvSource::System => crate::core::env::get_system_cmd_path("git"),
    };
    match &git_path {
        Some(p) => {
            let ver = crate::core::env::get_cmd_version(p).unwrap_or_else(|| "Unknown".to_string());
            let _ = tx.send(ProcessMsg::Log(format!("[系统] Git 环境检查通过: {}", ver)));
        }
        None => {
            let _ = tx.send(ProcessMsg::Log(
                "[警告] Git 环境缺失，酒馆的 Git 相关功能可能不可用".to_string(),
            ));
        }
    };

    // --- 第三步：确定酒馆工作目录 ---
    let tavern_dir = match resolve_tavern_dir(&settings, &tx) {
        Some(d) => d,
        None => return, // 错误已发送
    };

    if !tavern_dir.join("server.js").exists() {
        let _ = tx.send(ProcessMsg::Log(format!(
            "[错误] 未找到 server.js: {}",
            tavern_dir.display()
        )));
        let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Stopped));
        return;
    }

    // --- 第四步：端口检查与释放 ---
    release_port_if_occupied(&tavern_dir, &tx);

    // --- 第五步：代理环境配置 ---
    let proxy_url = setup_proxy(&settings, &tx);

    // --- 第六步：选择启动模式 ---
    if settings.allow_tavern_background {
        start_with_pm2(&tx, &node_path, &tavern_dir, settings, proxy_url, &git_path);
    } else {
        start_direct(&tx, &node_path, &tavern_dir, settings, proxy_url, &git_path, child_handle);
    }
}

// ---- 端口管理 ----

/// 检测并释放被占用的酒馆端口
fn release_port_if_occupied(tavern_dir: &PathBuf, tx: &Sender<ProcessMsg>) {
    let port = get_tavern_port(tavern_dir);
    let _ = tx.send(ProcessMsg::Log(format!(
        "[系统] 正在检查端口 {} 是否被占用...",
        port
    )));

    let output = Command::new("cmd")
        .arg("/c")
        .arg(format!("netstat -ano | findstr \":{} \"", port))
        .creation_flags(CREATE_NO_WINDOW)
        .output();

    let mut killed_pids: Vec<u32> = Vec::new();
    if let Ok(output) = output {
        let stdout = String::from_utf8_lossy(&output.stdout);
        for line in stdout.lines() {
            // 只处理 LISTENING 状态的行
            if !line.to_uppercase().contains("LISTENING") {
                continue;
            }
            let parts: Vec<&str> = line.split_whitespace().collect();
            if let Some(&pid_str) = parts.last() {
                if let Ok(pid) = pid_str.parse::<u32>() {
                    if pid != 0 && !killed_pids.contains(&pid) {
                        killed_pids.push(pid);
                        let _ = tx.send(ProcessMsg::Log(format!(
                            "[系统] 端口 {} 被进程 PID:{} 占用，正在释放...",
                            port, pid
                        )));
                        let _ = Command::new("taskkill")
                            .arg("/F")
                            .arg("/PID")
                            .arg(pid.to_string())
                            .creation_flags(CREATE_NO_WINDOW)
                            .output();
                        let _ = tx.send(ProcessMsg::Log(format!(
                            "[系统] 已终止占用端口的进程 (PID:{})",
                            pid
                        )));
                    }
                }
            }
        }
    }

    if killed_pids.is_empty() {
        let _ = tx.send(ProcessMsg::Log(format!(
            "[系统] 端口 {} 未被占用",
            port
        )));
    } else {
        // 短暂等待端口释放
        std::thread::sleep(std::time::Duration::from_millis(500));
    }
}

/// 从酒馆 config.yaml 读取端口号
fn get_tavern_port(tavern_dir: &PathBuf) -> u16 {
    let config_path = tavern_dir.join("config.yaml");
    if let Ok(content) = std::fs::read_to_string(&config_path) {
        for line in content.lines() {
            let trimmed = line.trim();
            if trimmed.starts_with("port:") || trimmed.starts_with("port :") {
                let value = trimmed
                    .splitn(2, ':')
                    .nth(1)
                    .unwrap_or("8000")
                    .trim();
                // 去掉可能存在的注释
                let value = value.split('#').next().unwrap_or(value).trim();
                if let Ok(p) = value.parse::<u16>() {
                    return p;
                }
            }
        }
    }
    8000 // 默认端口
}

// ---- 停止 ----

/// 停止酒馆服务（优雅/强制）
pub fn stop_tavern(force: bool, child_handle: &Arc<Mutex<Option<Child>>>) -> Vec<String> {
    let mut logs = Vec::new();

    // 先尝试 PM2 停止
    if let Some(pm2_path) = crate::core::env::get_pm2_path() {
        // 先清空 PM2 日志
        logs.push("[系统] 正在清空 PM2 日志...".to_string());
        match run_cmd_output(&pm2_path, &["flush"]) {
            Ok(_) => logs.push("[系统] PM2 日志已清空".to_string()),
            Err(e) => logs.push(format!("[警告] 清空 PM2 日志失败: {}", e)),
        }

        // 停止 PM2 进程（不删除）
        let action = if force { "kill" } else { "stop" };
        logs.push(format!("[系统] 通过 PM2 {} 服务...", action));
        match run_cmd_output(&pm2_path, &[action, "astrabrew-tavern"]) {
            Ok(output) => {
                let stderr = String::from_utf8_lossy(&output.stderr);
                if output.status.success() {
                    logs.push(format!("[系统] 服务已{}", if force { "强制终止" } else { "停止" }));
                } else {
                    let err_msg = stderr.lines().last().unwrap_or("未知错误");
                    logs.push(format!("[警告] PM2 {} 返回: {}", action, err_msg.trim()));
                }
            }
            Err(e) => {
                logs.push(format!("[警告] 执行 PM2 失败: {}，尝试直接终止进程", e));
            }
        }
    }

    // 直接模式：终止子进程
    if let Some(mut child) = child_handle.lock().unwrap().take() {
        logs.push("[系统] 正在终止直接启动的酒馆进程...".to_string());
        let _ = child.kill();
        let _ = child.wait();
        logs.push("[系统] 酒馆进程已终止".to_string());
    } else if logs.is_empty() {
        logs.push("[警告] 未找到运行中的酒馆进程".to_string());
    }

    logs
}

// ---- 内部辅助函数 ----

/// 解析酒馆工作目录
fn resolve_tavern_dir(settings: &SettingsState, tx: &Sender<ProcessMsg>) -> Option<PathBuf> {
    match &settings.sillytavern {
        Some(inst) => match inst.instance_type.as_str() {
            "builtin" => {
                let dir = crate::core::env::get_data_dir().join("sillytavern");
                let _ = tx.send(ProcessMsg::Log(format!(
                    "[系统] 使用内置酒馆实例: {}",
                    dir.display()
                )));
                Some(dir)
            }
            "local" => {
                if let Some(ref path) = inst.path {
                    let dir = PathBuf::from(path);
                    let _ = tx.send(ProcessMsg::Log(format!(
                        "[系统] 使用本地酒馆实例: {}",
                        dir.display()
                    )));
                    Some(dir)
                } else {
                    let _ = tx.send(ProcessMsg::Log("[错误] 本地实例路径无效".to_string()));
                    let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Stopped));
                    None
                }
            }
            _ => {
                let _ = tx.send(ProcessMsg::Log(format!(
                    "[错误] 未知的实例类型: {}",
                    inst.instance_type
                )));
                let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Stopped));
                None
            }
        },
        None => {
            // 如果没有任何实例，尝试使用默认内置路径
            let dir = crate::core::env::get_data_dir().join("sillytavern");
            let _ = tx.send(ProcessMsg::Log(format!(
                "[系统] 未设置活动实例，使用默认内置酒馆: {}",
                dir.display()
            )));
            Some(dir)
        }
    }
}

/// 设置代理环境变量，返回代理 URL（如果启用了）
fn setup_proxy(settings: &SettingsState, tx: &Sender<ProcessMsg>) -> Option<String> {
    match settings.proxy_type {
        ProxyType::Custom if !settings.custom_proxy.is_empty() => {
            let _ = tx.send(ProcessMsg::Log(format!(
                "[系统] 正在配置自定义代理: {}",
                settings.custom_proxy
            )));
            Some(settings.custom_proxy.clone())
        }
        ProxyType::System => {
            if let Some((url, enabled)) = crate::core::network::read_windows_system_proxy() {
                if enabled && !url.is_empty() {
                    let _ = tx.send(ProcessMsg::Log(format!(
                        "[系统] 正在配置系统代理: {}",
                        url
                    )));
                    Some(url)
                } else {
                    let _ = tx.send(ProcessMsg::Log("[系统] 代理已设置为跟随系统，但系统代理未启用".to_string()));
                    None
                }
            } else {
                let _ = tx.send(ProcessMsg::Log("[系统] 无法读取系统代理设置".to_string()));
                None
            }
        }
        _ => {
            let _ = tx.send(ProcessMsg::Log("[系统] 代理未启用".to_string()));
            None
        }
    }
}

/// 获取全局配置文件的绝对路径
fn get_global_config_path() -> PathBuf {
    crate::utils::app_paths().global_tavern_config_file()
}

/// 通过 PM2 启动酒馆
fn start_with_pm2(
    tx: &Sender<ProcessMsg>,
    _node_path: &PathBuf,
    tavern_dir: &PathBuf,
    settings: &SettingsState,
    proxy_url: Option<String>,
    git_path: &Option<PathBuf>,
) {
    let _ = tx.send(ProcessMsg::Log("[系统] 使用 PM2 后台模式启动酒馆...".to_string()));

    let pm2_path = match crate::core::env::get_pm2_path() {
        Some(p) => p,
        None => {
            let _ = tx.send(ProcessMsg::Log(
                "[错误] PM2 未安装，请先在设置中安装 PM2".to_string(),
            ));
            let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Stopped));
            return;
        }
    };

    // 清空 PM2 之前的日志
    let _ = tx.send(ProcessMsg::Log("[系统] 正在清空 PM2 历史日志...".to_string()));
    let _ = run_cmd_output(&pm2_path, &["flush"]);

    // 构建启动参数
    let mut args: Vec<String> = vec![
        "start".to_string(),
        "server.js".to_string(),
        "--name".to_string(),
        "astrabrew-tavern".to_string(),
    ];

    // 全局数据模式 → 传递 configPath
    if settings.data_mode == TavernDataMode::Global {
        let config_path = get_global_config_path();
        let path_str = config_path.to_string_lossy().replace("\\\\", "\\");
        let _ = tx.send(ProcessMsg::Log(format!(
            "[系统] 全局数据模式，配置文件: {}",
            path_str
        )));
        args.push("--".to_string());
        args.push("--configPath".to_string());
        args.push(path_str.to_string());
    }

    let mut cmd = build_command(&pm2_path, &args);
    cmd.current_dir(tavern_dir);

    apply_env_vars(&mut cmd, &proxy_url, git_path);

    match cmd.output() {
        Ok(output) => {
            let stdout = String::from_utf8_lossy(&output.stdout);
            let stderr = String::from_utf8_lossy(&output.stderr);

            if output.status.success() {
                let _ = tx.send(ProcessMsg::Log("[系统] 酒馆服务已通过 PM2 启动成功".to_string()));
                for line in stdout.lines() {
                    let trimmed = line.trim();
                    if !trimmed.is_empty() {
                        let _ = tx.send(ProcessMsg::Log(format!("[PM2] {}", trimmed)));
                    }
                }
                let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Running));
            } else {
                let _ = tx.send(ProcessMsg::Log("[错误] PM2 启动失败".to_string()));
                for line in stderr.lines() {
                    let trimmed = line.trim();
                    if !trimmed.is_empty() {
                        let _ = tx.send(ProcessMsg::Log(format!("[PM2] {}", trimmed)));
                    }
                }
                let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Stopped));
            }
        }
        Err(e) => {
            let _ = tx.send(ProcessMsg::Log(format!("[错误] 无法执行 PM2: {}", e)));
            let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Stopped));
        }
    }
}

/// 直接通过 node server.js 启动酒馆
fn start_direct(
    tx: &Sender<ProcessMsg>,
    node_path: &PathBuf,
    tavern_dir: &PathBuf,
    settings: &SettingsState,
    proxy_url: Option<String>,
    git_path: &Option<PathBuf>,
    child_handle: Arc<Mutex<Option<Child>>>,
) {
    let _ = tx.send(ProcessMsg::Log("[系统] 使用直接启动模式 (node server.js)...".to_string()));

    let mut args = vec!["server.js".to_string()];

    if settings.data_mode == TavernDataMode::Global {
        let config_path = get_global_config_path();
        let path_str = config_path.to_string_lossy().replace("\\\\", "\\");
        let _ = tx.send(ProcessMsg::Log(format!(
            "[系统] 全局数据模式，配置文件: {}",
            path_str
        )));
        args.push("--configPath".to_string());
        args.push(path_str);
    }

    let mut cmd = build_command(node_path, &args);
    cmd.current_dir(tavern_dir)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());

    apply_env_vars(&mut cmd, &proxy_url, git_path);

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            let _ = tx.send(ProcessMsg::Log(format!("[错误] 无法启动酒馆进程: {}", e)));
            let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Stopped));
            return;
        }
    };

    let pid = child.id();
    let _ = tx.send(ProcessMsg::Log(format!(
        "[系统] 酒馆进程已启动 (PID: {:?})",
        pid
    )));
    let _ = tx.send(ProcessMsg::StateChange(ConsoleStatus::Running));

    // 取出 stdout / stderr
    let stdout = child.stdout.take();
    let stderr = child.stderr.take();

    // 存储 Child 句柄供停止时使用
    *child_handle.lock().unwrap() = Some(child);

    // 读取 stdout
    if let Some(out) = stdout {
        let tx_out = tx.clone();
        std::thread::spawn(move || {
            let reader = BufReader::new(out);
            for line in reader.lines() {
                if let Ok(l) = line {
                    if !l.trim().is_empty() {
                        let _ = tx_out.send(ProcessMsg::Log(format!("[酒馆] {}", l)));
                    }
                }
            }
        });
    }

    // 读取 stderr
    if let Some(err) = stderr {
        let tx_err = tx.clone();
        std::thread::spawn(move || {
            let reader = BufReader::new(err);
            for line in reader.lines() {
                if let Ok(l) = line {
                    if !l.trim().is_empty() {
                        let _ = tx_err.send(ProcessMsg::Log(format!("[酒馆] {}", l)));
                    }
                }
            }
        });
    }

    // 等待进程退出（在独立线程中）
    let tx_wait = tx.clone();
    let ch = child_handle.clone();
    std::thread::spawn(move || {
        // 等待进程结束
        let exit_status = {
            let mut guard = ch.lock().unwrap();
            if let Some(ref mut child) = *guard {
                match child.wait() {
                    Ok(s) => Some(s),
                    Err(_) => None,
                }
            } else {
                None
            }
        };

        let _ = tx_wait.send(ProcessMsg::Log(format!(
            "[系统] 酒馆进程已退出 (状态: {:?})",
            exit_status
        )));
        let _ = tx_wait.send(ProcessMsg::StateChange(ConsoleStatus::Stopped));

        // 清空句柄
        if let Ok(mut guard) = ch.lock() {
            *guard = None;
        }
    });
}

// ---- 底层工具函数 ----

/// 为 Windows 命令构建 Command（自动处理 .cmd/.bat 扩展名）
fn build_command(binary: &PathBuf, args: &[String]) -> Command {
    let is_script = binary
        .extension()
        .map(|e| e == "cmd" || e == "bat")
        .unwrap_or(false);

    if is_script {
        let mut cmd = Command::new("cmd");
        cmd.creation_flags(CREATE_NO_WINDOW);
        cmd.arg("/c").arg(binary);
        for arg in args {
            cmd.arg(arg);
        }
        cmd
    } else {
        let mut cmd = Command::new(binary);
        cmd.creation_flags(CREATE_NO_WINDOW);
        for arg in args {
            cmd.arg(arg);
        }
        cmd
    }
}

/// 执行命令并获取输出
fn run_cmd_output(binary: &PathBuf, args: &[&str]) -> std::io::Result<std::process::Output> {
    let str_args: Vec<String> = args.iter().map(|s| s.to_string()).collect();
    build_command(binary, &str_args)
        .creation_flags(CREATE_NO_WINDOW)
        .output()
}

/// 应用环境变量（代理、Git PATH）
fn apply_env_vars(cmd: &mut Command, proxy_url: &Option<String>, git_path: &Option<PathBuf>) {
    cmd.creation_flags(CREATE_NO_WINDOW);

    // 代理环境变量
    if let Some(proxy) = proxy_url {
        cmd.env("HTTP_PROXY", proxy);
        cmd.env("HTTPS_PROXY", proxy);
        cmd.env("http_proxy", proxy);
        cmd.env("https_proxy", proxy);
    }

    // 内置 Git → 追加到 PATH
    if let Some(git_p) = git_path {
        if let Some(git_dir) = git_p.parent() {
            let current_path = std::env::var("PATH").unwrap_or_default();
            let new_path = format!("{};{}", git_dir.to_string_lossy(), current_path);
            cmd.env("PATH", &new_path);
        }
    }
}
