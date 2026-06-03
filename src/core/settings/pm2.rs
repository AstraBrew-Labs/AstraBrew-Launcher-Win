use std::io::{BufRead, BufReader};
use std::process::Command;
use std::os::windows::process::CommandExt;

const CREATE_NO_WINDOW: u32 = 0x08000000;

/// 安装 PM2 全局包，通过 npm 发送安装进度
/// registry: 临时镜像源 URL，用于加速下载，不覆盖系统配置
pub fn install_pm2(tx: std::sync::mpsc::Sender<crate::EnvInstallProgress>, registry: &str) {
    let npm_cmd = crate::core::env::get_builtin_npm_path()
        .or_else(|| crate::core::env::get_system_cmd_path("npm"));

    let npm = match npm_cmd {
        Some(p) => p,
        None => {
            let _ = tx.send(crate::EnvInstallProgress::Error("npm 未找到，无法安装 PM2".to_string()));
            return;
        }
    };

    let _ = tx.send(crate::EnvInstallProgress::Status("正在安装 PM2...".to_string()));

    // npm install pm2 -g --registry=<url>
    let mut cmd = if npm.extension().map(|e| e == "cmd").unwrap_or(false) {
        let mut c = Command::new("cmd");
        c.arg("/c").arg(&npm);
        c
    } else {
        Command::new(&npm)
    };

    cmd.arg("install")
       .arg("pm2")
       .arg("-g")
       .arg("--registry")
       .arg(registry)
       .creation_flags(CREATE_NO_WINDOW)
       .stdout(std::process::Stdio::piped())
       .stderr(std::process::Stdio::piped());

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            let _ = tx.send(crate::EnvInstallProgress::Error(
                format!("无法启动 npm: {}", e)
            ));
            return;
        }
    };

    let stdout = child.stdout.take();
    let stderr = child.stderr.take();

    // 流式读取 stdout
    let tx_out = tx.clone();
    if let Some(out) = stdout {
        std::thread::spawn(move || {
            let reader = BufReader::new(out);
            for line in reader.lines() {
                if let Ok(l) = line {
                    if !l.trim().is_empty() {
                        let _ = tx_out.send(crate::EnvInstallProgress::Log(l));
                    }
                }
            }
        });
    }

    // 流式读取 stderr（npm 的安装进度走 stderr）
    let tx_err = tx.clone();
    if let Some(err) = stderr {
        std::thread::spawn(move || {
            let reader = BufReader::new(err);
            for line in reader.lines() {
                if let Ok(l) = line {
                    if !l.trim().is_empty() {
                        let _ = tx_err.send(crate::EnvInstallProgress::Log(l));
                    }
                }
            }
        });
    }

    let status = child.wait().unwrap_or_else(|_| std::process::ExitStatus::default());

    if status.success() {
        let _ = tx.send(crate::EnvInstallProgress::Finished);
    } else {
        let _ = tx.send(crate::EnvInstallProgress::Error("PM2 安装失败，请查看日志".to_string()));
    }
}
