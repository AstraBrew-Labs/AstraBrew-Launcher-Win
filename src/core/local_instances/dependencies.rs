//! 只读检测实际安装树，并使用与在线安装相同的 npm 环境安装运行依赖。

use std::io::{Read, Write};
use std::path::Path;
use std::process::{Command, ExitStatus, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use super::{DependencyStatus, LocalError, LocalErrorKind, inspect_package, online_dir};
use crate::core::settings::EnvSource;
use serde_json::Value;

pub struct CommandOutput {
    pub status: ExitStatus,
    pub stdout: Vec<u8>,
    pub stderr: Vec<u8>,
}

/// 两路管道并发读取，限制内存并保持排空，防止 npm 输出过大堵塞子进程。
pub fn capture(
    mut command: Command,
    input: Option<Vec<u8>>,
    timeout: Duration,
    cancel: &AtomicBool,
) -> Result<CommandOutput, LocalError> {
    const LIMIT: usize = 32 * 1024 * 1024;
    fn read_pipe(mut pipe: impl Read) -> std::io::Result<Vec<u8>> {
        let mut result = Vec::new();
        let mut buffer = [0; 8192];
        loop {
            let count = pipe.read(&mut buffer)?;
            if count == 0 {
                break;
            }
            if result.len() <= LIMIT {
                let retain = count.min(LIMIT + 1 - result.len());
                result.extend_from_slice(&buffer[..retain]);
            }
        }
        Ok(result)
    }
    command
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .stdin(if input.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        });
    let mut child = command.spawn().map_err(|error| {
        if error.kind() == std::io::ErrorKind::NotFound {
            // GUI 应用的 PATH 可能不包含 Node.js；不要把底层 ENOENT 暴露给用户，
            // 交由应用层显示安装引导弹窗。
            LocalError::new("environment.nodejs_required.error", "")
                .with_kind(LocalErrorKind::MissingNodeJs)
        } else {
            LocalError::new("local.task.start_failed", error)
        }
    })?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| LocalError::new("local.task.read_output_failed", "stdout"))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| LocalError::new("local.task.read_output_failed", "stderr"))?;
    let out = std::thread::spawn(move || read_pipe(stdout));
    let err = std::thread::spawn(move || read_pipe(stderr));
    let writer = input.and_then(|input| {
        child
            .stdin
            .take()
            .map(|mut stdin| std::thread::spawn(move || stdin.write_all(&input)))
    });
    let start = Instant::now();
    let status = loop {
        if cancel.load(Ordering::Relaxed) || start.elapsed() >= timeout {
            let _ = child.kill();
            let _ = child.wait();
            break Err(LocalError::new(
                if cancel.load(Ordering::Relaxed) {
                    "local.task.cancelled"
                } else {
                    "local.task.timeout"
                },
                "",
            )
            .with_kind(if cancel.load(Ordering::Relaxed) {
                LocalErrorKind::Cancelled
            } else {
                LocalErrorKind::Service
            }));
        }
        match child.try_wait() {
            Ok(Some(status)) => break Ok(status),
            Ok(None) => std::thread::sleep(Duration::from_millis(50)),
            Err(e) => {
                let _ = child.kill();
                let _ = child.wait();
                break Err(LocalError::new("local.task.read_output_failed", e));
            }
        }
    };
    let stdout = out
        .join()
        .map_err(|_| LocalError::new("local.task.read_output_failed", "stdout worker"))?
        .map_err(|e| LocalError::new("local.task.read_output_failed", e))?;
    let stderr = err
        .join()
        .map_err(|_| LocalError::new("local.task.read_output_failed", "stderr worker"))?
        .map_err(|e| LocalError::new("local.task.read_output_failed", e))?;
    if let Some(writer) = writer {
        writer
            .join()
            .map_err(|_| LocalError::new("local.task.read_output_failed", "stdin worker"))?
            .map_err(|e| LocalError::new("local.task.read_output_failed", e))?;
    }
    let status = status?;
    if stdout.len() > LIMIT || stderr.len() > LIMIT {
        return Err(LocalError::new("local.task.output_too_large", ""));
    }
    Ok(CommandOutput {
        status,
        stdout,
        stderr,
    })
}

/// 只忽略 npm 自己标记的额外包及合法可选缺省，不忽略实际缺失或版本冲突。
fn analyze_tree(tree: &Value, success: bool) -> Result<DependencyStatus, LocalError> {
    fn inspect(node: &Value, problems: &mut Vec<String>) {
        if node.get("extraneous").and_then(Value::as_bool) == Some(true) {
            return;
        }
        if let Some(list) = node.get("problems").and_then(Value::as_array) {
            problems.extend(list.iter().filter_map(Value::as_str).map(str::to_owned));
        }
        if node.get("missing").and_then(Value::as_bool) == Some(true)
            && node.get("optional").and_then(Value::as_bool) != Some(true)
        {
            problems.push("missing: dependency".into());
        }
        if node
            .get("invalid")
            .is_some_and(|v| v != &Value::Bool(false) && !v.is_null())
        {
            problems.push("invalid: dependency".into());
        }
        if let Some(deps) = node.get("dependencies").and_then(Value::as_object) {
            for dependency in deps.values() {
                inspect(dependency, problems);
            }
        }
    }
    if !tree.is_object()
        || !tree
            .get("name")
            .and_then(Value::as_str)
            .is_some_and(|name| name.eq_ignore_ascii_case("sillytavern"))
    {
        return Err(LocalError::new("local.deps.parse_failed", tree));
    }
    if let Some(error) = tree.get("error") {
        if error.get("code").and_then(Value::as_str) != Some("ELSPROBLEMS") {
            return Err(LocalError::new("local.deps.check_failed", error));
        }
    }
    let mut problems = Vec::new();
    inspect(tree, &mut problems);
    if problems
        .iter()
        .any(|p| p.starts_with("missing:") || p.starts_with("invalid:"))
    {
        return Ok(DependencyStatus::Incomplete);
    }
    if problems.iter().any(|p| !p.starts_with("extraneous:")) || (!success && problems.is_empty()) {
        return Err(LocalError::new("local.deps.check_failed", problems.join("\n")));
    }
    Ok(DependencyStatus::Ready)
}

/// 按当前环境来源构建 node / npm 命令。
///
/// 内置环境使用 `lib/nodejs/` 中的 node 与 npm.cmd；系统环境走 `where` 解析。
fn node_command(name: &str, source: EnvSource) -> Command {
    crate::core::settings::env_detect::command_for(name, source)
}

pub fn check(
    path: &Path,
    source: EnvSource,
    cancel: &AtomicBool,
) -> Result<DependencyStatus, LocalError> {
    inspect_package(&path.join("package.json"), &online_dir())?;
    let mut command = node_command("npm", source);
    command.current_dir(path).args([
        "ls",
        "--all",
        "--omit=dev",
        "--json",
        "--long",
        "--ignore-scripts",
        "--offline",
        "--package-lock-only=false",
        "--logs-max=0",
    ]);
    let output = capture(command, None, Duration::from_secs(60), cancel)?;
    let tree: Value = serde_json::from_slice(&output.stdout).map_err(|error| {
        LocalError::new(
            "local.deps.parse_failed",
            format!("{error}\n{}", String::from_utf8_lossy(&output.stderr)),
        )
    })?;
    analyze_tree(&tree, output.status.success())
}

pub fn install(
    path: &Path,
    registry: &str,
    proxy_mode: &str,
    proxy_host: &str,
    source: EnvSource,
    cancel: &AtomicBool,
    log: impl FnMut(String),
) -> Result<DependencyStatus, LocalError> {
    inspect_package(&path.join("package.json"), &online_dir())?;
    for tool in ["node", "npm"] {
        let mut probe = node_command(tool, source);
        probe.arg("--version");
        let output = capture(probe, None, Duration::from_secs(15), cancel)
            .map_err(|error| LocalError::new("local.deps.need_nodejs", error.detail))?;
        if !output.status.success() {
            return Err(LocalError::new(
                "local.deps.need_nodejs",
                String::from_utf8_lossy(&output.stderr),
            ));
        }
    }
    let mut command = node_command("npm", source);
    command.current_dir(path).args(["install", "--omit=dev"]);
    if !registry.trim().is_empty() {
        command.env("npm_config_registry", registry);
    }
    crate::core::network::configure_npm_proxy(&mut command, proxy_mode, proxy_host);
    crate::core::network::run_logged_command(command, cancel, log)
        .map_err(|error| LocalError::new("local.deps.install_failed", error))?;
    let status = check(path, source, cancel)?;
    if status != DependencyStatus::Ready {
        return Err(LocalError::new("local.deps.incomplete_after_install", ""));
    }
    Ok(status)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn runtime_tree_validates_transitive_dependencies() {
        assert_eq!(analyze_tree(&json!({"name":"sillytavern","dependencies":{"a":{"dependencies":{"b":{"missing":true}}}}}), false).unwrap(), DependencyStatus::Incomplete);
        assert_eq!(analyze_tree(&json!({"name":"sillytavern","problems":["invalid: a@1"],"error":{"code":"ELSPROBLEMS"}}), false).unwrap(), DependencyStatus::Incomplete);
    }
    #[test]
    fn allows_optional_absence_and_extraneous_but_not_unknown_errors() {
        assert_eq!(analyze_tree(&json!({"name":"sillytavern","dependencies":{"optional":{"optional":true,"missing":true}}}), true).unwrap(), DependencyStatus::Ready);
        assert_eq!(
            analyze_tree(
                &json!({"name":"sillytavern","problems":["extraneous: extra@1"]}),
                false
            )
            .unwrap(),
            DependencyStatus::Ready
        );
        assert!(
            analyze_tree(
                &json!({"name":"sillytavern","error":{"code":"EACCES"}}),
                false
            )
            .is_err()
        );
        assert!(analyze_tree(&json!({}), true).is_err());
        assert!(analyze_tree(&json!({"name":"sillytavern"}), false).is_err());
    }
}
