//! 启动器核心业务模块。

pub(crate) mod auto_launch;
/// Windows 环境路径与命令解析（内置 lib 目录、系统 PATH、无黑窗启动）。
pub(crate) mod env;
pub(crate) mod extensions;
pub(crate) mod network;
pub(crate) mod settings;
/// Windows Shell 辅助（打开 URL / 定位文件，避免黑窗闪烁）。
pub(crate) mod shell;

pub(crate) mod library;
pub(crate) mod local_instances;

pub(crate) mod tavern_config;
/// 时间戳格式化（Windows 无 `date` 命令，自行换算民用历法）。
pub(crate) mod time;
pub(crate) mod typography;
pub(crate) mod updater;

pub(crate) mod desktop_webview;
pub(crate) mod pm2;
pub(crate) mod tavern_process;
