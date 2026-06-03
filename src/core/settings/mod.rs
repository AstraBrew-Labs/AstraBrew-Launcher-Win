pub mod autostart;
pub mod nodejs;
pub mod pm2;
pub mod git;
pub mod github_proxy;
pub mod tavern;

use crate::pages::settings::NpmRegistry;

/// 将 NpmRegistry 枚举映射为实际的镜像 URL
pub fn npm_registry_url(registry: &NpmRegistry) -> &'static str {
    match registry {
        NpmRegistry::Official => "https://registry.npmjs.org/",
        NpmRegistry::Taobao => "https://registry.npmmirror.com/",
        NpmRegistry::Tencent => "https://mirrors.cloud.tencent.com/npm/",
    }
}
