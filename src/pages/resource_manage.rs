//! SillyTavern 资源管理页面。
//!
//! 合并旧版 Web 页面的管理操作与原生 v2 页面中的文件解析能力，统一管理
//! 角色卡、世界书、历史对话和预设。所有操作都直接作用于当前数据目录。

use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Instant, UNIX_EPOCH};

use iced::widget::{
    button, column, container, image, markdown, mouse_area, row, scrollable, space, stack,
    text_input, tooltip,
};
use iced::{Alignment, Background, Border, Color, Element, Fill, Length, Task, Theme};
use lucide_icons::Icon;

use astra_ui::{BLUE_600, ButtonVariant, DANGER, INK_MUTED, SUCCESS, WHITE, icons};

use super::notice::TransientNotice;
use super::settings::{SettingsState, TavernDataMode};
use super::versions::VersionState;
use crate::core::library::{
    ResourceData, ResourceImportItem, ResourceImportReport, ResourceKind, ValidationIssue,
    WorldEntry, import_batch, validate_path,
};
use crate::lang::{current_language, raw, t, t_in, text, tf};
use crate::theme::button_style;

pub(crate) mod workbench;
use workbench::{WorkbenchEvent, WorkbenchKind, WorkbenchMessage, WorkbenchState, WorldBookOption};

const LIST_WIDTH: f32 = 390.0;
const CHARACTER_THUMB_WIDTH: f32 = 52.0;
const CHARACTER_THUMB_HEIGHT: f32 = 70.0;
const CHAT_MESSAGE_LIMIT: usize = 300;
// 预设详情按旧版的 2×2 分页展示，避免一次性布局大量提示词卡片。
const PRESET_PROMPTS_PER_PAGE: usize = 4;
/// 右下角悬浮分页栏滑出 / 收起的动画时长。
const PRESET_PAGER_SLIDE_SECONDS: f32 = 0.18;
/// 「依赖酒馆助手」为真时的强调色；为假时改用中性灰，避免与提示词指标抢视觉。
const TAVERN_HELPER_ACCENT: Color = Color::from_rgb8(142, 68, 220);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ResourceTab {
    #[default]
    Characters,
    WorldBooks,
    Chats,
    Presets,
}

impl ResourceTab {
    const ALL: [Self; 4] = [
        Self::Characters,
        Self::WorldBooks,
        Self::Chats,
        Self::Presets,
    ];

    const fn label_key(self) -> &'static str {
        match self {
            Self::Characters => "resources.import.kind.character",
            Self::WorldBooks => "resources.import.kind.world_book",
            Self::Chats => "resources.tab.chats",
            Self::Presets => "resources.import.kind.preset",
        }
    }

    const fn icon(self) -> Icon {
        match self {
            Self::Characters => Icon::ContactRound,
            Self::WorldBooks => Icon::BookOpenText,
            Self::Chats => Icon::MessagesSquare,
            Self::Presets => Icon::SlidersHorizontal,
        }
    }

    const fn directory(self) -> &'static str {
        match self {
            Self::Characters => "characters",
            Self::WorldBooks => "worlds",
            Self::Chats => "chats",
            Self::Presets => "OpenAI Settings",
        }
    }

    const fn supports_import(self) -> bool {
        !matches!(self, Self::Chats)
    }

    const fn resource_kind(self) -> Option<ResourceKind> {
        match self {
            Self::Characters => Some(ResourceKind::CharacterCard),
            Self::WorldBooks => Some(ResourceKind::WorldBook),
            Self::Presets => Some(ResourceKind::Preset),
            Self::Chats => None,
        }
    }
}

#[derive(Debug, Clone)]
pub struct CharacterCardInfo {
    pub filename: String,
    pub filepath: PathBuf,
    /// 直接持有扫描时的 PNG 字节，避免文件路径句柄在替换同名文件后复用旧图片缓存。
    pub cover: image::Handle,
    pub name: String,
    pub description: String,
    pub creator: String,
    pub version: String,
    pub tags: Vec<String>,
    pub personality: String,
    pub scenario: String,
    pub first_message: String,
    pub spec: String,
    pub spec_version: String,
    pub world_name: String,
    pub world_entries: Vec<WorldEntry>,
    pub file_size: u64,
    pub modified_secs: u64,
    pub image_width: u32,
    pub image_height: u32,
}

#[derive(Debug, Clone)]
pub struct WorldBookInfo {
    pub filename: String,
    pub filepath: PathBuf,
    pub name: String,
    pub author: String,
    pub entries: Vec<WorldEntry>,
    pub file_size: u64,
    pub modified_secs: u64,
}

#[derive(Debug, Clone)]
pub struct ChatFileInfo {
    pub filename: String,
    pub filepath: PathBuf,
    pub display_time: String,
    pub file_size: u64,
    pub modified_secs: u64,
}

#[derive(Debug, Clone)]
pub struct ChatGroup {
    pub name: String,
    pub files: Vec<ChatFileInfo>,
}

#[derive(Debug, Clone)]
pub struct ChatMessage {
    pub name: String,
    pub is_user: bool,
    pub send_date: String,
    pub content: String,
    /// 加载时解析一次的 Markdown 结构。
    ///
    /// 对话预览最多展示 [`CHAT_MESSAGE_LIMIT`] 条消息，如果每帧都重新解析正文，
    /// 会把 pulldown-cmark 的开销压到渲染路径上，因此这里预解析并随消息一起持有。
    markdown: Vec<markdown::Item>,
}

pub(crate) use crate::core::library::PresetPrompt;

#[derive(Debug, Clone)]
pub struct PresetInfo {
    pub filename: String,
    pub filepath: PathBuf,
    pub name: String,
    pub source: String,
    pub model: String,
    pub max_context: i64,
    pub max_tokens: i64,
    pub stream: bool,
    pub prompt_count: usize,
    pub enabled_prompt_count: usize,
    pub prompts: Vec<PresetPrompt>,
    /// 预设的扩展格式是否为旧版 SPreset。
    pub has_spreset: bool,
    /// 预设是否声明或隐式依赖酒馆助手。
    pub requires_tavern_helper: bool,
    pub file_size: u64,
    pub modified_secs: u64,
    prompts_loaded: bool,
}

#[derive(Debug, Clone)]
struct PendingDelete {
    path: PathBuf,
    label: String,
}

#[derive(Debug, Clone)]
pub enum ResourceManageMessage {
    SelectTab(ResourceTab),
    SearchChanged(String),
    Refresh,
    OpenDirectory,
    Import,
    ImportCompleted(ResourceTab, ResourceImportReport),
    ShowImportFailures,
    CloseImportFailures,
    ClearImportFailures,
    /// 消费导入失败弹窗内部及遮罩点击，防止事件穿透到底层页面。
    ImportFailureModalInteract,
    SelectCharacter(usize),
    SelectWorldBook(usize),
    SelectChat(usize, usize),
    SelectPreset(usize),
    PresetsLoaded(u64, Result<Vec<PresetInfo>, String>),
    PresetDetailLoaded(u64, usize, Result<Vec<PresetPrompt>, String>),
    /// 跳转到预设提示词结构的指定页；页索引从 0 开始。
    PresetDetailGoToPage(usize),
    /// 鼠标进入 / 离开右下角悬浮分页栏。
    PresetPagerHover(bool),
    /// 推进悬浮分页栏的滑出 / 收起动画。
    PresetPagerTick,
    RequestDelete,
    ConfirmDelete,
    CancelDelete,
    /// 消费删除确认弹窗内部及遮罩点击，防止事件穿透到底层页面。
    DeleteModalInteract,
    OpenWorkbench,
    Workbench(WorkbenchMessage),
    /// 点击对话预览里 Markdown 渲染出的链接。
    OpenMarkdownLink(String),
}

/// 悬浮分页栏进行中的滑动动画。
#[derive(Debug, Clone, Copy)]
struct PagerSlide {
    from: f32,
    to: f32,
    started_at: Instant,
}

#[derive(Debug)]
pub struct ResourceManageState {
    pub tab: ResourceTab,
    pub search: String,
    data_root: Option<PathBuf>,
    context_key: String,
    pub characters: Vec<CharacterCardInfo>,
    pub world_books: Vec<WorldBookInfo>,
    pub chat_groups: Vec<ChatGroup>,
    pub presets: Vec<PresetInfo>,
    pub presets_loaded: bool,
    pub presets_loading: bool,
    pub selected_character: Option<usize>,
    pub selected_world_book: Option<usize>,
    pub selected_chat: Option<(usize, usize)>,
    pub selected_preset: Option<usize>,
    pub preset_detail_page: usize,
    /// 悬浮分页栏的滑出进度：0 = 收起（只露出拉手），1 = 完全滑出。
    preset_pager_progress: f32,
    /// 进行中的滑动动画；为空表示进度已经稳定在目标值。
    preset_pager_slide: Option<PagerSlide>,
    pub preset_detail_loading: bool,
    pub preset_detail_error: Option<String>,
    preset_load_request_id: u64,
    preset_detail_request_id: u64,
    pub chat_messages: Vec<ChatMessage>,
    pub notice: Option<TransientNotice>,
    pending_delete: Option<PendingDelete>,
    pub import_pending: bool,
    import_failures: Vec<ResourceImportItem>,
    import_failure_kind: Option<ResourceKind>,
    import_failures_visible: bool,
    pub(crate) workbench: WorkbenchState,
    selection_restore_path: Option<PathBuf>,
}

impl Default for ResourceManageState {
    fn default() -> Self {
        Self {
            tab: ResourceTab::Characters,
            search: String::new(),
            data_root: None,
            context_key: String::new(),
            characters: Vec::new(),
            world_books: Vec::new(),
            chat_groups: Vec::new(),
            presets: Vec::new(),
            presets_loaded: false,
            presets_loading: false,
            selected_character: None,
            selected_world_book: None,
            selected_chat: None,
            selected_preset: None,
            preset_detail_page: 0,
            preset_pager_progress: 0.0,
            preset_pager_slide: None,
            preset_detail_loading: false,
            preset_detail_error: None,
            preset_load_request_id: 0,
            preset_detail_request_id: 0,
            chat_messages: Vec::new(),
            notice: None,
            pending_delete: None,
            import_pending: false,
            import_failures: Vec::new(),
            import_failure_kind: None,
            import_failures_visible: false,
            workbench: WorkbenchState::default(),
            selection_restore_path: None,
        }
    }
}

impl ResourceManageState {
    pub fn configure(&mut self, settings: &SettingsState, versions: &VersionState) {
        let root = match settings.data_mode {
            TavernDataMode::Global => {
                let path = expand_home(&settings.global_data_path).join("default-user");
                Some(path)
            }
            TavernDataMode::Current => {
                let instance = versions.current_path.as_deref().or_else(|| {
                    versions
                        .local_instances
                        .first()
                        .map(|item| item.path.as_str())
                });
                match instance {
                    Some(path) => Some(PathBuf::from(path).join("data").join("default-user")),
                    None => None,
                }
            }
        };

        let key = root
            .as_ref()
            .map(|path| path.to_string_lossy().into_owned())
            .unwrap_or_default();
        if key != self.context_key {
            self.context_key = key;
            self.data_root = root;
            self.clear_loaded_data();
        }
    }

    pub fn update(&mut self, message: ResourceManageMessage) -> Task<ResourceManageMessage> {
        let task = match message {
            ResourceManageMessage::SelectTab(tab) => {
                self.tab = tab;
                self.search.clear();
                self.pending_delete = None;
                Task::none()
            }
            ResourceManageMessage::SearchChanged(value) => {
                self.search = value;
                Task::none()
            }
            ResourceManageMessage::Refresh => {
                self.refresh_all();
                self.notice = Some(TransientNotice::info(
                    "notice.refresh_complete",
                    "resources.notice.refresh_done",
                ));
                Task::none()
            }
            ResourceManageMessage::OpenDirectory => {
                self.open_current_directory();
                Task::none()
            }
            ResourceManageMessage::Import => self.import_current_resource(),
            ResourceManageMessage::ImportCompleted(tab, report) => {
                self.apply_import_report(tab, report);
                Task::none()
            }
            ResourceManageMessage::ShowImportFailures => {
                self.import_failures_visible = !self.import_failures.is_empty();
                Task::none()
            }
            ResourceManageMessage::CloseImportFailures => {
                self.import_failures_visible = false;
                Task::none()
            }
            ResourceManageMessage::ClearImportFailures => {
                self.import_failures.clear();
                self.import_failure_kind = None;
                self.import_failures_visible = false;
                Task::none()
            }
            ResourceManageMessage::ImportFailureModalInteract => Task::none(),
            ResourceManageMessage::SelectCharacter(index) => {
                self.selected_character = Some(index);
                self.pending_delete = None;
                Task::none()
            }
            ResourceManageMessage::SelectWorldBook(index) => {
                self.selected_world_book = Some(index);
                self.pending_delete = None;
                Task::none()
            }
            ResourceManageMessage::SelectChat(group_index, file_index) => {
                self.selected_chat = Some((group_index, file_index));
                self.pending_delete = None;
                self.chat_messages = self
                    .chat_groups
                    .get(group_index)
                    .and_then(|group| group.files.get(file_index))
                    .map(|file| load_chat_messages(&file.filepath))
                    .unwrap_or_default();
                Task::none()
            }
            ResourceManageMessage::SelectPreset(index) => self.select_preset(index),
            ResourceManageMessage::PresetsLoaded(request_id, result) => {
                self.apply_presets_loaded(request_id, result);
                if let Some(index) = self.selected_preset
                    && self
                        .presets
                        .get(index)
                        .is_some_and(|preset| !preset.prompts_loaded)
                {
                    self.select_preset(index)
                } else {
                    Task::none()
                }
            }
            ResourceManageMessage::PresetDetailLoaded(request_id, index, result) => {
                self.apply_preset_detail_loaded(request_id, index, result);
                Task::none()
            }
            ResourceManageMessage::PresetDetailGoToPage(page) => {
                if let Some(index) = self.selected_preset
                    && let Some(preset) = self.presets.get(index)
                {
                    let page_count = preset.prompt_count.div_ceil(PRESET_PROMPTS_PER_PAGE);
                    self.preset_detail_page = page.min(page_count.saturating_sub(1));
                }
                Task::none()
            }
            ResourceManageMessage::RequestDelete => {
                self.request_delete();
                Task::none()
            }
            ResourceManageMessage::ConfirmDelete => {
                self.confirm_delete();
                Task::none()
            }
            ResourceManageMessage::CancelDelete => {
                self.pending_delete = None;
                Task::none()
            }
            ResourceManageMessage::DeleteModalInteract => Task::none(),
            ResourceManageMessage::OpenWorkbench => self.open_selected_workbench(),
            ResourceManageMessage::Workbench(message) => {
                let edited_path = self.workbench.path().map(Path::to_path_buf);
                let (task, event) = self.workbench.update(message);
                if event == WorkbenchEvent::Closed {
                    self.selection_restore_path = edited_path;
                    self.refresh_all();
                    self.restore_selection_path();
                }
                task.map(ResourceManageMessage::Workbench)
            }
            ResourceManageMessage::OpenMarkdownLink(url) => {
                self.open_external_link(&url);
                Task::none()
            }
            ResourceManageMessage::PresetPagerHover(open) => {
                self.set_pager_open(open);
                Task::none()
            }
            ResourceManageMessage::PresetPagerTick => {
                self.advance_pager_slide();
                Task::none()
            }
        };

        // 顶部标签始终展示预设数量；即使当前停留在其他资源页，也要保证预设扫描已启动。
        // 资源目录在异步切换时，过期扫描结果会被丢弃，此处会自动为新目录补发扫描任务。
        let load_task = self.start_presets_loading();
        Task::batch([task, load_task])
    }

    fn open_selected_workbench(&mut self) -> Task<ResourceManageMessage> {
        let target = match self.tab {
            ResourceTab::Characters => self
                .selected_character
                .and_then(|index| self.characters.get(index))
                .map(|item| (item.filepath.clone(), WorkbenchKind::Character)),
            ResourceTab::WorldBooks => self
                .selected_world_book
                .and_then(|index| self.world_books.get(index))
                .map(|item| (item.filepath.clone(), WorkbenchKind::WorldBook)),
            ResourceTab::Presets => self
                .selected_preset
                .and_then(|index| self.presets.get(index))
                .map(|item| (item.filepath.clone(), WorkbenchKind::Preset)),
            ResourceTab::Chats => None,
        };
        let Some((path, kind)) = target else {
            return Task::none();
        };
        let options = self
            .world_books
            .iter()
            .map(|item| WorldBookOption {
                name: item.name.clone(),
                path: item.filepath.clone(),
            })
            .collect();
        self.workbench
            .open(path, kind, options)
            .map(ResourceManageMessage::Workbench)
    }

    pub fn refresh_all(&mut self) {
        self.characters = self.scan_characters();
        self.world_books = self.scan_world_books();
        self.chat_groups = self.scan_chats();
        // 预设文件可能包含大量提示词，列表改由后台任务异步加载。
        self.presets.clear();
        self.presets_loaded = false;
        self.presets_loading = false;
        self.preset_load_request_id = self.preset_load_request_id.wrapping_add(1);
        self.preset_detail_request_id = self.preset_detail_request_id.wrapping_add(1);
        self.preset_detail_loading = false;
        self.preset_detail_error = None;
        self.repair_selections();
        self.restore_selection_path();
    }

    /// 启动预设列表后台加载，只读取列表所需的轻量元数据。
    pub fn start_presets_loading(&mut self) -> Task<ResourceManageMessage> {
        if self.presets_loaded || self.presets_loading {
            return Task::none();
        }

        let Some(directory) = self.directory_for(ResourceTab::Presets) else {
            self.presets_loaded = true;
            return Task::none();
        };

        self.preset_load_request_id = self.preset_load_request_id.wrapping_add(1);
        let request_id = self.preset_load_request_id;
        self.presets_loading = true;

        Task::perform(
            async move {
                std::thread::spawn(move || scan_presets_from_directory(&directory))
                    .join()
                    .unwrap_or_else(|_| Err(t("resources.preset.load_thread_crashed").to_owned()))
            },
            move |result| ResourceManageMessage::PresetsLoaded(request_id, result),
        )
    }

    /// 选择预设时只异步读取当前预设的完整提示词内容，避免切换详情阻塞界面。
    /// 右下角悬浮分页栏是否正在滑动，用于决定是否订阅按帧推进。
    pub fn pager_animating(&self) -> bool {
        self.preset_pager_slide.is_some()
    }

    /// 请求悬浮分页栏滑出 / 收起；重复请求同一个目标不会重置动画。
    fn set_pager_open(&mut self, open: bool) {
        let to = if open { 1.0 } else { 0.0 };
        if self.preset_pager_progress == to && self.preset_pager_slide.is_none() {
            return;
        }
        if let Some(slide) = self.preset_pager_slide
            && slide.to == to
        {
            return;
        }
        self.preset_pager_slide = Some(PagerSlide {
            from: self.preset_pager_progress,
            to,
            started_at: Instant::now(),
        });
    }

    /// 按帧推进滑动动画；进度到位后立刻停止订阅。
    fn advance_pager_slide(&mut self) {
        let Some(slide) = self.preset_pager_slide else {
            return;
        };
        let elapsed = slide.started_at.elapsed().as_secs_f32();
        let progress = (elapsed / PRESET_PAGER_SLIDE_SECONDS).clamp(0.0, 1.0);
        // 缓出，滑到位时更自然。
        let eased = 1.0 - (1.0 - progress) * (1.0 - progress);
        self.preset_pager_progress = slide.from + (slide.to - slide.from) * eased;
        if progress >= 1.0 {
            self.preset_pager_progress = slide.to;
            self.preset_pager_slide = None;
        }
    }

    /// 回到收起状态；切换预设时调用，避免沿用上一个预设的展开状态。
    fn reset_pager(&mut self) {
        self.preset_pager_progress = 0.0;
        self.preset_pager_slide = None;
    }

    fn select_preset(&mut self, index: usize) -> Task<ResourceManageMessage> {
        // 先取出后面要用的字段，避免持有 presets 的借用时再修改状态。
        let Some((prompts_loaded, path)) = self
            .presets
            .get(index)
            .map(|preset| (preset.prompts_loaded, preset.filepath.clone()))
        else {
            return Task::none();
        };

        self.selected_preset = Some(index);
        self.pending_delete = None;
        self.preset_detail_page = 0;
        self.reset_pager();
        self.preset_detail_error = None;
        self.preset_detail_request_id = self.preset_detail_request_id.wrapping_add(1);

        if prompts_loaded {
            self.preset_detail_loading = false;
            return Task::none();
        }

        let request_id = self.preset_detail_request_id;
        self.preset_detail_loading = true;

        Task::perform(
            async move {
                std::thread::spawn(move || load_preset_prompts(&path))
                    .join()
                    .unwrap_or_else(|_| Err(t("resources.preset.thread_exit").to_owned()))
            },
            move |result| ResourceManageMessage::PresetDetailLoaded(request_id, index, result),
        )
    }

    /// 应用后台返回的预设列表，并丢弃已经过期的请求结果。
    fn apply_presets_loaded(&mut self, request_id: u64, result: Result<Vec<PresetInfo>, String>) {
        if request_id != self.preset_load_request_id {
            return;
        }

        self.presets_loading = false;
        self.presets_loaded = true;
        match result {
            Ok(presets) => self.presets = presets,
            Err(error) => {
                self.presets.clear();
                self.notice = Some(TransientNotice::danger(
                    "notice.load_failed",
                    tf("resources.notice.preset_load_failed", &[("error", &error)]),
                ));
            }
        }
        self.repair_selections();
        self.restore_selection_path();
    }

    /// 文件保存后列表会按修改时间重排，必须按路径恢复选择而不能沿用旧索引。
    fn restore_selection_path(&mut self) {
        let Some(path) = self.selection_restore_path.as_ref() else {
            return;
        };
        let restored = match self.tab {
            ResourceTab::Characters => self
                .characters
                .iter()
                .position(|item| item.filepath == *path)
                .map(|index| self.selected_character = Some(index))
                .is_some(),
            ResourceTab::WorldBooks => self
                .world_books
                .iter()
                .position(|item| item.filepath == *path)
                .map(|index| self.selected_world_book = Some(index))
                .is_some(),
            ResourceTab::Presets if self.presets_loaded => self
                .presets
                .iter()
                .position(|item| item.filepath == *path)
                .map(|index| self.selected_preset = Some(index))
                .is_some(),
            ResourceTab::Presets => false,
            ResourceTab::Chats => true,
        };
        if restored {
            self.selection_restore_path = None;
        }
    }

    /// 应用当前预设的异步详情内容，并确保切换预设后不会串入旧结果。
    fn apply_preset_detail_loaded(
        &mut self,
        request_id: u64,
        index: usize,
        result: Result<Vec<PresetPrompt>, String>,
    ) {
        if request_id != self.preset_detail_request_id || self.selected_preset != Some(index) {
            return;
        }

        self.preset_detail_loading = false;
        match result {
            Ok(prompts) => {
                if let Some(preset) = self.presets.get_mut(index) {
                    preset.prompt_count = prompts.len();
                    preset.enabled_prompt_count =
                        prompts.iter().filter(|prompt| prompt.enabled).count();
                    preset.prompts = prompts;
                    preset.prompts_loaded = true;
                }
            }
            Err(error) => self.preset_detail_error = Some(error),
        }
    }

    fn clear_loaded_data(&mut self) {
        self.characters.clear();
        self.world_books.clear();
        self.chat_groups.clear();
        self.presets.clear();
        self.selected_character = None;
        self.selected_world_book = None;
        self.selected_chat = None;
        self.selected_preset = None;
        self.preset_detail_page = 0;
        self.reset_pager();
        self.preset_detail_loading = false;
        self.preset_detail_error = None;
        self.presets_loaded = false;
        self.presets_loading = false;
        self.preset_load_request_id = self.preset_load_request_id.wrapping_add(1);
        self.preset_detail_request_id = self.preset_detail_request_id.wrapping_add(1);
        self.chat_messages.clear();
        self.pending_delete = None;
        self.import_pending = false;
        self.import_failures.clear();
        self.import_failure_kind = None;
        self.import_failures_visible = false;
        self.selection_restore_path = None;
    }

    fn repair_selections(&mut self) {
        if self
            .selected_character
            .is_some_and(|i| i >= self.characters.len())
        {
            self.selected_character = None;
        }
        if self
            .selected_world_book
            .is_some_and(|i| i >= self.world_books.len())
        {
            self.selected_world_book = None;
        }
        if self
            .selected_preset
            .is_some_and(|i| i >= self.presets.len())
        {
            self.selected_preset = None;
        }
        if let Some((group, file)) = self.selected_chat {
            if self
                .chat_groups
                .get(group)
                .is_none_or(|item| file >= item.files.len())
            {
                self.selected_chat = None;
                self.chat_messages.clear();
            }
        }
    }

    fn directory_for(&self, tab: ResourceTab) -> Option<PathBuf> {
        self.data_root
            .as_ref()
            .map(|root| root.join(tab.directory()))
    }

    fn open_current_directory(&mut self) {
        let Some(directory) = self.directory_for(self.tab) else {
            self.notice = Some(TransientNotice::warning(
                "notice.action_unavailable",
                "resources.notice.select_instance",
            ));
            return;
        };
        if let Err(error) = fs::create_dir_all(&directory) {
            self.notice = Some(TransientNotice::danger(
                "notice.operation_failed",
                tf("resources.notice.create_dir_failed", &[("error", &error)]),
            ));
            return;
        }
        match crate::core::shell::open_path(&directory) {
            Ok(()) => {
                self.notice = Some(TransientNotice::success(
                    "notice.directory_opened",
                    tf("resources.directory_opened", &[("tab", &t(self.tab.label_key()))]),
                ));
            }
            Err(error) => {
                self.notice = Some(TransientNotice::danger(
                    "notice.operation_failed",
                    tf("resources.notice.open_dir_failed", &[("error", &error)]),
                ));
            }
        }
    }

    /// 用系统默认浏览器打开对话预览中的链接。
    fn open_external_link(&mut self, url: &str) {
        if let Err(error) = super::markdown_doc::open_link(url) {
            self.notice = Some(TransientNotice::danger("notice.operation_failed", error));
        }
    }

    fn import_current_resource(&mut self) -> Task<ResourceManageMessage> {
        if self.import_pending {
            return Task::none();
        }
        let Some(kind) = self.tab.resource_kind() else {
            self.notice = Some(TransientNotice::warning(
                "notice.action_unavailable",
                "resources.notice.chats_import",
            ));
            return Task::none();
        };
        let Some(directory) = self.directory_for(self.tab) else {
            self.notice = Some(TransientNotice::warning(
                "notice.action_unavailable",
                "resources.notice.select_instance",
            ));
            return Task::none();
        };
        let dialog = match self.tab {
            ResourceTab::Characters => rfd::FileDialog::new().add_filter(t("resources.filter.character_png"), &["png"]),
            ResourceTab::WorldBooks | ResourceTab::Presets => {
                rfd::FileDialog::new().add_filter(t("resources.filter.json"), &["json"])
            }
            ResourceTab::Chats => return Task::none(),
        };
        let Some(files) = dialog.pick_files() else {
            return Task::none();
        };
        let tab = self.tab;
        self.import_pending = true;
        let task_sources = files.clone();
        Task::perform(
            async move {
                std::thread::spawn(move || import_batch(kind, files, &directory))
                    .join()
                    .unwrap_or_else(|_| {
                        ResourceImportReport::task_failed(
                            task_sources,
                            "resources.import.thread_exit",
                        )
                    })
            },
            move |report| ResourceManageMessage::ImportCompleted(tab, report),
        )
    }

    /// 应用后台导入结果，并保留逐文件失败原因供详情弹窗查看。
    fn apply_import_report(&mut self, tab: ResourceTab, report: ResourceImportReport) {
        self.import_pending = false;
        let imported = report.imported;
        let failed = report.failed.len();
        self.import_failures = report.failed;
        self.import_failure_kind = (!self.import_failures.is_empty()).then_some(
            tab.resource_kind()
                .expect("仅支持导入的资源页会产生导入报告"),
        );
        self.import_failures_visible = false;
        if imported > 0 {
            self.refresh_all();
        }
        self.notice = if failed == 0 {
            Some(TransientNotice::success(
                "notice.import_complete",
                import_result_detail(tab, imported, failed),
            ))
        } else if imported == 0 {
            Some(TransientNotice::danger(
                "notice.operation_failed",
                import_result_detail(tab, imported, failed),
            ))
        } else {
            Some(TransientNotice::warning(
                "notice.import_partial",
                import_result_detail(tab, imported, failed),
            ))
        };
    }

    fn selected_path_and_label(&self) -> Option<(PathBuf, String)> {
        match self.tab {
            ResourceTab::Characters => self
                .selected_character
                .and_then(|index| self.characters.get(index))
                .map(|item| (item.filepath.clone(), item.name.clone())),
            ResourceTab::WorldBooks => self
                .selected_world_book
                .and_then(|index| self.world_books.get(index))
                .map(|item| (item.filepath.clone(), item.name.clone())),
            ResourceTab::Chats => self
                .selected_chat
                .and_then(|(group, file)| {
                    self.chat_groups
                        .get(group)
                        .and_then(|item| item.files.get(file))
                })
                .map(|item| (item.filepath.clone(), item.filename.clone())),
            ResourceTab::Presets => self
                .selected_preset
                .and_then(|index| self.presets.get(index))
                .map(|item| (item.filepath.clone(), item.name.clone())),
        }
    }

    fn request_delete(&mut self) {
        self.pending_delete = self
            .selected_path_and_label()
            .map(|(path, label)| PendingDelete { path, label });
    }

    fn confirm_delete(&mut self) {
        let Some(pending) = self.pending_delete.take() else {
            return;
        };
        match fs::remove_file(&pending.path) {
            Ok(()) => {
                self.notice = Some(TransientNotice::success(
                    "notice.delete_complete",
                    tf("resources.deleted", &[("name", &pending.label)]),
                ));
                self.refresh_all();
            }
            Err(error) => {
                self.notice = Some(TransientNotice::danger(
                    "notice.delete_failed",
                    tf("resources.delete_failed", &[("error", &error)]),
                ));
            }
        }
    }

    /// 取出页面本次产生的轻提示，确保同一条消息只展示一次。
    pub fn take_notice(&mut self) -> Option<TransientNotice> {
        self.notice.take()
    }

    fn scan_characters(&self) -> Vec<CharacterCardInfo> {
        let Some(directory) = self.directory_for(ResourceTab::Characters) else {
            return Vec::new();
        };
        let mut items = read_files(&directory, "png")
            .into_iter()
            .filter_map(|path| {
                let metadata = fs::metadata(&path).ok()?;
                let cover = image::Handle::from_bytes(fs::read(&path).ok()?);
                let validated = validate_path(ResourceKind::CharacterCard, &path).ok()?;
                let ResourceData::CharacterCard(parsed) = validated.data else {
                    return None;
                };
                let filename = file_name(&path);
                let name = parsed.name.clone();
                let (world_name, world_entries) = parsed
                    .world_book
                    .map(|world| (world.name, world.entries))
                    .unwrap_or_default();
                Some(CharacterCardInfo {
                    filename,
                    filepath: path,
                    cover,
                    name,
                    description: parsed.description,
                    creator: parsed.creator,
                    version: parsed.version,
                    tags: parsed.tags,
                    personality: parsed.personality,
                    scenario: parsed.scenario,
                    first_message: parsed.first_message,
                    spec: parsed.spec,
                    spec_version: parsed.spec_version,
                    world_name,
                    world_entries,
                    file_size: metadata.len(),
                    modified_secs: modified_secs(&metadata),
                    image_width: parsed.image_width,
                    image_height: parsed.image_height,
                })
            })
            .collect::<Vec<_>>();
        items.sort_by(|a, b| b.modified_secs.cmp(&a.modified_secs));
        items
    }

    fn scan_world_books(&self) -> Vec<WorldBookInfo> {
        let Some(directory) = self.directory_for(ResourceTab::WorldBooks) else {
            return Vec::new();
        };
        let mut items = read_files(&directory, "json")
            .into_iter()
            .filter_map(|path| {
                let metadata = fs::metadata(&path).ok()?;
                let validated = validate_path(ResourceKind::WorldBook, &path).ok()?;
                let ResourceData::WorldBook(world) = validated.data else {
                    return None;
                };
                let name = non_empty(world.name, file_stem(&path));
                Some(WorldBookInfo {
                    filename: file_name(&path),
                    filepath: path,
                    name,
                    author: world.author,
                    entries: world.entries,
                    file_size: metadata.len(),
                    modified_secs: modified_secs(&metadata),
                })
            })
            .collect::<Vec<_>>();
        items.sort_by(|a, b| b.modified_secs.cmp(&a.modified_secs));
        items
    }

    fn scan_chats(&self) -> Vec<ChatGroup> {
        let Some(directory) = self.directory_for(ResourceTab::Chats) else {
            return Vec::new();
        };
        let Ok(entries) = fs::read_dir(directory) else {
            return Vec::new();
        };
        let mut groups = entries
            .flatten()
            .filter(|entry| entry.path().is_dir())
            .filter_map(|entry| {
                let path = entry.path();
                let mut files = read_files(&path, "jsonl")
                    .into_iter()
                    .filter_map(|path| {
                        let metadata = fs::metadata(&path).ok()?;
                        let filename = file_name(&path);
                        Some(ChatFileInfo {
                            display_time: chat_display_time(&filename),
                            filename,
                            filepath: path,
                            file_size: metadata.len(),
                            modified_secs: modified_secs(&metadata),
                        })
                    })
                    .collect::<Vec<_>>();
                files.sort_by(|a, b| b.modified_secs.cmp(&a.modified_secs));
                (!files.is_empty()).then(|| ChatGroup {
                    name: file_name(&path),
                    files,
                })
            })
            .collect::<Vec<_>>();
        groups.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
        groups
    }

    fn resource_count(&self, tab: ResourceTab) -> usize {
        match tab {
            ResourceTab::Characters => self.characters.len(),
            ResourceTab::WorldBooks => self.world_books.len(),
            ResourceTab::Chats => self.chat_groups.iter().map(|group| group.files.len()).sum(),
            ResourceTab::Presets => self.presets.len(),
        }
    }

    fn current_count(&self) -> usize {
        self.resource_count(self.tab)
    }
}

/// 在后台线程中读取预设列表，只解析卡片展示需要的元数据。
fn scan_presets_from_directory(directory: &Path) -> Result<Vec<PresetInfo>, String> {
    let mut items = read_files(directory, "json")
        .into_iter()
        .filter_map(|path| {
            let metadata = fs::metadata(&path).ok()?;
            let validated = validate_path(ResourceKind::Preset, &path).ok()?;
            let ResourceData::Preset(preset) = validated.data else {
                return None;
            };
            let name = file_stem(&path);
            Some(PresetInfo {
                filename: file_name(&path),
                filepath: path,
                name,
                source: preset.source,
                model: preset.model,
                max_context: preset.max_context,
                max_tokens: preset.max_tokens,
                stream: preset.stream,
                prompt_count: preset.prompts.len(),
                enabled_prompt_count: preset
                    .prompts
                    .iter()
                    .filter(|prompt| prompt.enabled)
                    .count(),
                prompts: Vec::new(),
                has_spreset: preset.has_spreset,
                requires_tavern_helper: preset.requires_tavern_helper,
                file_size: metadata.len(),
                modified_secs: modified_secs(&metadata),
                prompts_loaded: false,
            })
        })
        .collect::<Vec<_>>();

    items.sort_by(|a, b| b.modified_secs.cmp(&a.modified_secs));
    Ok(items)
}

/// 异步加载单个预设的完整提示词内容。
fn load_preset_prompts(path: &Path) -> Result<Vec<PresetPrompt>, String> {
    let validated = validate_path(ResourceKind::Preset, path)
        .map_err(|issues| validation_issue_summary(&issues))?;
    let ResourceData::Preset(preset) = validated.data else {
        return Err(t("resources.preset.result_type_invalid").to_owned());
    };
    Ok(preset.prompts)
}

/// 将一组字段级问题压缩为预设详情加载使用的单行错误。
fn validation_issue_summary(issues: &[ValidationIssue]) -> String {
    issues
        .first()
        .map(|issue| {
            if issue.field_path.is_empty() {
                issue.detail.clone()
            } else {
                format!("{}：{}", issue.field_path, issue.detail)
            }
        })
        .unwrap_or_else(|| t("resources.validation.summary_fallback").to_owned())
}

fn load_chat_messages(path: &Path) -> Vec<ChatMessage> {
    let Ok(content) = fs::read_to_string(path) else {
        return Vec::new();
    };
    let mut messages = content
        .lines()
        .filter_map(|line| serde_json::from_str::<serde_json::Value>(line).ok())
        .filter(|value| {
            !value
                .get("is_system")
                .and_then(|value| value.as_bool())
                .unwrap_or(false)
        })
        .filter_map(|value| {
            let content = value
                .get("mes")
                .or_else(|| value.get("message"))
                .and_then(|value| value.as_str())?
                .to_owned();
            // 消息正文在加载阶段解析成 Markdown 结构，供对话预览直接渲染。
            let markdown = markdown::parse(&content).collect();
            Some(ChatMessage {
                name: string_value(&value, "name"),
                is_user: value
                    .get("is_user")
                    .and_then(|value| value.as_bool())
                    .unwrap_or(false),
                send_date: string_value(&value, "send_date"),
                content,
                markdown,
            })
        })
        .collect::<Vec<_>>();
    if messages.len() > CHAT_MESSAGE_LIMIT {
        messages.drain(..messages.len() - CHAT_MESSAGE_LIMIT);
    }
    messages
}

fn read_files(directory: &Path, extension: &str) -> Vec<PathBuf> {
    fs::read_dir(directory)
        .into_iter()
        .flatten()
        .flatten()
        .map(|entry| entry.path())
        .filter(|path| path.is_file())
        .filter(|path| {
            path.extension()
                .and_then(|value| value.to_str())
                .is_some_and(|value| value.eq_ignore_ascii_case(extension))
        })
        .collect()
}

fn modified_secs(metadata: &fs::Metadata) -> u64 {
    metadata
        .modified()
        .ok()
        .and_then(|value| value.duration_since(UNIX_EPOCH).ok())
        .map(|value| value.as_secs())
        .unwrap_or_default()
}

fn file_name(path: &Path) -> String {
    path.file_name()
        .unwrap_or_default()
        .to_string_lossy()
        .into_owned()
}

fn file_stem(path: &Path) -> String {
    path.file_stem()
        .unwrap_or_default()
        .to_string_lossy()
        .into_owned()
}

fn string_value(value: &serde_json::Value, key: &str) -> String {
    value
        .get(key)
        .and_then(|value| value.as_str())
        .unwrap_or_default()
        .to_owned()
}

fn non_empty(value: String, fallback: String) -> String {
    if value.trim().is_empty() {
        fallback
    } else {
        value
    }
}

/// 展开 `~` 家目录前缀（Windows 上是 `%USERPROFILE%`）。
fn expand_home(path: &str) -> PathBuf {
    let user_profile = || {
        std::env::var_os("USERPROFILE")
            .map(PathBuf::from)
            .unwrap_or_default()
    };
    if path == "~" {
        return user_profile();
    }
    if let Some(rest) = path.strip_prefix("~/").or_else(|| path.strip_prefix("~\\")) {
        return user_profile().join(rest);
    }
    PathBuf::from(path)
}

fn chat_display_time(filename: &str) -> String {
    filename
        .strip_suffix(".jsonl")
        .unwrap_or(filename)
        .split_once(" - ")
        .map(|(_, value)| value.to_owned())
        .unwrap_or_else(|| filename.trim_end_matches(".jsonl").to_owned())
}

fn format_size(bytes: u64) -> String {
    if bytes < 1024 {
        format!("{bytes} B")
    } else if bytes < 1024 * 1024 {
        format!("{:.1} KB", bytes as f64 / 1024.0)
    } else {
        format!("{:.1} MB", bytes as f64 / (1024.0 * 1024.0))
    }
}

fn truncate(value: &str, length: usize) -> String {
    let mut chars = value.chars();
    let preview = chars.by_ref().take(length).collect::<String>();
    if chars.next().is_some() {
        format!("{preview}…")
    } else {
        preview
    }
}

pub fn resource_manage_view<'a>(
    state: &'a ResourceManageState,
    theme: &Theme,
) -> Element<'a, ResourceManageMessage> {
    // 资源管理页头部只保留标题，避免重复展示全局数据路径卡片。
    let header = row![column![
        row![
            container(icons::icon(Icon::LibraryBig, 19, BLUE_600))
                .width(36)
                .height(36)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(page_icon_surface),
            column![
                text("resources.title")
                    .size(22)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::text_style),
                text("resources.subtitle")
                    .size(13)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(3),
        ]
        .spacing(11)
        .align_y(Alignment::Center),
    ],]
    .align_y(Alignment::Center);

    let tabs = row(ResourceTab::ALL
        .into_iter()
        .map(|tab| resource_tab(state, tab)))
    .spacing(3)
    .width(Fill)
    .align_y(Alignment::End);

    let import_button: Element<'_, ResourceManageMessage> = if state.tab.supports_import() {
        let content = row![
            icons::icon(
                if state.import_pending {
                    Icon::LoaderCircle
                } else {
                    Icon::FileUp
                },
                14,
                WHITE,
            ),
            raw(if state.import_pending {
                tr("resources.import.validating").to_owned()
            } else {
                format!(
                    "{}{}",
                    tr("resources.import.action"),
                    state
                        .tab
                        .resource_kind()
                        .map(resource_kind_label)
                        .unwrap_or_default()
                )
            })
            .size(13)
            .font(crate::core::typography::medium()),
        ]
        .spacing(6)
        .align_y(Alignment::Center);
        let control = button(content)
            .padding([8, 12])
            .style(button_style(ButtonVariant::Primary));
        if state.import_pending {
            control.into()
        } else {
            control.on_press(ResourceManageMessage::Import).into()
        }
    } else {
        space::horizontal().width(Length::Shrink).into()
    };

    let failure_button: Element<'_, ResourceManageMessage> = if state.import_failures.is_empty() {
        space::horizontal().width(Length::Shrink).into()
    } else {
        button(
            row![
                icons::icon(Icon::TriangleAlert, 14, DANGER),
                raw(format!(
                    "{} ({})",
                    tr("resources.import.show_failures"),
                    state.import_failures.len()
                ))
                .size(13)
                .font(crate::core::typography::medium()),
            ]
            .spacing(6)
            .align_y(Alignment::Center),
        )
        .on_press(ResourceManageMessage::ShowImportFailures)
        .padding([8, 12])
        .style(danger_outline_button_style)
        .into()
    };

    let workbench_button: Element<'_, ResourceManageMessage> = if match state.tab {
        ResourceTab::Characters => state.selected_character.is_some(),
        ResourceTab::WorldBooks => state.selected_world_book.is_some(),
        ResourceTab::Presets => state.selected_preset.is_some(),
        ResourceTab::Chats => false,
    } {
        button(
            row![
                icons::icon(Icon::SquarePen, 14, BLUE_600),
                raw(t_in("workbench.open", current_language())).size(12),
            ]
            .spacing(6)
            .align_y(Alignment::Center),
        )
        .on_press(ResourceManageMessage::OpenWorkbench)
        .height(34)
        .padding([7, 10])
        .style(button_style(ButtonVariant::Secondary))
        .into()
    } else {
        space::horizontal().width(Length::Shrink).into()
    };

    let toolbar = row![
        container(
            row![
                crate::theme::subtle_icon(Icon::Search, 14),
                text_input(t("resources.search.placeholder"), &state.search)
                    .on_input(ResourceManageMessage::SearchChanged)
                    // 输入框保留足够的水平留白，避免文字贴着焦点边框显示。
                    .padding([7, 9])
                    .size(13)
                    .font(crate::core::typography::regular())
                    // 输入框本身负责边框和焦点状态，避免与外层搜索容器叠加边框。
                    .style(crate::theme::text_input_style)
                    .width(Fill),
            ]
            .spacing(7)
            .align_y(Alignment::Center),
        )
        .width(300)
        // 搜索图标与输入框整体距离工具栏边缘留出稳定空间。
        .padding([0, 8]),
        container(
            row![
                icons::icon(state.tab.icon(), 13, BLUE_600),
                raw(tf("resources.count.items", &[("count", &state.current_count())]))
                    .size(12)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(6)
            .align_y(Alignment::Center),
        )
        .padding([7, 10])
        .style(count_surface),
        failure_button,
        space::horizontal(),
        workbench_button,
        tooltip(
            button(crate::theme::muted_icon(Icon::FolderOpen, 15))
                .on_press(ResourceManageMessage::OpenDirectory)
                .width(34)
                .height(34)
                .style(button_style(ButtonVariant::Outline)),
            container(text("resources.tooltip.open_directory").size(12))
                .padding([5, 8])
                .style(tooltip_surface),
            tooltip::Position::Bottom,
        ),
        tooltip(
            button(crate::theme::muted_icon(Icon::RefreshCw, 15))
                .on_press(ResourceManageMessage::Refresh)
                .width(34)
                .height(34)
                .style(button_style(ButtonVariant::Outline)),
            container(text("resources.tooltip.rescan").size(12))
                .padding([5, 8])
                .style(tooltip_surface),
            tooltip::Position::Bottom,
        ),
        import_button,
    ]
    .spacing(8)
    .align_y(Alignment::Center);

    let body = if state.data_root.is_none() {
        empty_page(
            Icon::FolderCog,
            "resources.empty.no_directory.title",
            "resources.empty.hint",
        )
    } else {
        row![resource_list(state), resource_detail(state, theme)]
            .spacing(14)
            .height(Fill)
            .into()
    };

    let page = container(
        container(
            column![header, tabs, toolbar, body]
                .spacing(12)
                .width(Fill)
                .height(Fill),
        )
        .max_width(1120)
        .width(Fill)
        .height(Fill),
    )
    .width(Fill)
    .height(Fill)
    .padding([22, 28])
    .align_x(Alignment::Center)
    .style(crate::theme::canvas_style);

    if state.import_failures_visible {
        stack![page, import_failure_modal(state)]
            .width(Fill)
            .height(Fill)
            .into()
    } else if let Some(pending) = &state.pending_delete {
        stack![page, delete_confirmation_modal(pending)]
            .width(Fill)
            .height(Fill)
            .into()
    } else {
        page.into()
    }
}

/// 构建导入失败详情弹窗，按文件和字段展示稳定校验结果。
fn import_failure_modal(state: &ResourceManageState) -> Element<'_, ResourceManageMessage> {
    let resource_label = state
        .import_failure_kind
        .map(resource_kind_label)
        .unwrap_or_else(|| tr("resources.import.resource_unknown"));
    let header = row![
        container(icons::icon(Icon::ShieldAlert, 18, DANGER))
            .width(38)
            .height(38)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .style(delete_modal_icon_surface),
        column![
            raw(tr("resources.import.failure_title"))
                .size(18)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
            raw(format!(
                "{} · {} {}",
                resource_label,
                state.import_failures.len(),
                tr("resources.import.failure_count")
            ))
            .size(12)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style),
        ]
        .spacing(3)
        .width(Fill),
        button(crate::theme::muted_icon(Icon::X, 17))
            .on_press(ResourceManageMessage::CloseImportFailures)
            .width(32)
            .height(32)
            .style(button_style(ButtonVariant::Ghost)),
    ]
    .spacing(12)
    .align_y(Alignment::Center);

    let rows = state
        .import_failures
        .iter()
        .map(import_failure_item)
        .collect::<Vec<_>>();
    let details = scrollable(column(rows).spacing(10).width(Fill))
        .height(Length::Fill)
        .width(Fill);
    let footer = row![
        raw(tr("resources.import.failure_hint"))
            .size(11)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style),
        space::horizontal(),
        button(
            raw(tr("resources.import.clear_failures"))
                .size(12)
                .font(crate::core::typography::medium()),
        )
        .on_press(ResourceManageMessage::ClearImportFailures)
        .height(36)
        .padding([8, 14])
        .style(button_style(ButtonVariant::Secondary)),
        button(
            raw(tr("resources.import.close"))
                .size(12)
                .font(crate::core::typography::medium())
                .color(WHITE),
        )
        .on_press(ResourceManageMessage::CloseImportFailures)
        .height(36)
        .padding([8, 16])
        .style(button_style(ButtonVariant::Primary)),
    ]
    .spacing(8)
    .align_y(Alignment::Center);

    let panel = mouse_area(
        container(
            column![
                header,
                modal_separator(),
                details,
                modal_separator(),
                footer
            ]
            .spacing(14),
        )
        .width(660)
        .height(520)
        .padding(20)
        .style(delete_modal_surface),
    )
    .on_press(ResourceManageMessage::ImportFailureModalInteract);

    stack![
        button(space::Space::new())
            .on_press(ResourceManageMessage::CloseImportFailures)
            .width(Fill)
            .height(Fill)
            .padding(0)
            .style(delete_modal_backdrop_style),
        container(panel)
            .width(Fill)
            .height(Fill)
            .padding(24)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center),
    ]
    .width(Fill)
    .height(Fill)
    .into()
}

/// 构建单个失败文件及其字段级问题列表。
fn import_failure_item(item: &ResourceImportItem) -> Element<'_, ResourceManageMessage> {
    let file_name = truncate(
        &item
            .source
            .file_name()
            .unwrap_or_default()
            .to_string_lossy(),
        64,
    );
    let format = item
        .format
        .map(|format| format.label_key())
        .unwrap_or_else(|| tr("resources.import.format_unrecognized"));
    let issues = item
        .result
        .as_ref()
        .err()
        .map(Vec::as_slice)
        .unwrap_or_default();
    let issue_rows = issues
        .iter()
        .map(|issue| {
            let path = if issue.field_path.is_empty() {
                tr("resources.validation.file_level").to_owned()
            } else {
                issue.field_path.clone()
            };
            row![
                icons::icon(Icon::CircleX, 13, DANGER),
                raw(path)
                    .size(11)
                    .width(150)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::muted_text_style),
                raw(t_in(issue.message_key, current_language()))
                    .size(12)
                    .width(Fill)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::text_style),
            ]
            .spacing(8)
            .align_y(Alignment::Center)
            .into()
        })
        .collect::<Vec<Element<'_, ResourceManageMessage>>>();
    container(
        column![
            row![
                icons::icon(Icon::FileWarning, 15, DANGER),
                raw(file_name)
                    .size(13)
                    .width(Fill)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::text_style),
                space::horizontal(),
                text(format)
                    .size(11)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(8)
            .align_y(Alignment::Center),
            column(issue_rows).spacing(7),
        ]
        .spacing(10),
    )
    .width(Fill)
    .padding([12, 14])
    .style(info_surface)
    .into()
}

fn resource_kind_label(kind: ResourceKind) -> &'static str {
    match kind {
        ResourceKind::CharacterCard => tr("resources.import.kind.character"),
        ResourceKind::WorldBook => tr("resources.import.kind.world_book"),
        ResourceKind::Preset => tr("resources.import.kind.preset"),
    }
}

/// 根据当前语言生成带数量的导入结果，避免动态文案出现中英混排。
fn import_result_detail(tab: ResourceTab, imported: usize, failed: usize) -> String {
    let resource = tab
        .resource_kind()
        .map(resource_kind_label)
        .unwrap_or_else(|| tr("resources.import.resource_unknown"));
    match current_language() {
        crate::lang::Language::Chinese if failed == 0 => {
            tf("resources.import.result", &[("count", &imported), ("resource", &resource)])
        }
        crate::lang::Language::Chinese if imported == 0 => {
            tf("resources.import.summary_none", &[("resource", &resource), ("failed", &failed)])
        }
        crate::lang::Language::Chinese => {
            tf("resources.import.summary_partial", &[("imported", &imported), ("failed", &failed)])
        }
        crate::lang::Language::English if failed == 0 => {
            format!("Imported {imported} {resource}.")
        }
        crate::lang::Language::English if imported == 0 => {
            format!("No {resource} were imported; {failed} files failed validation.")
        }
        crate::lang::Language::English => {
            format!("Imported {imported} files; {failed} files failed validation.")
        }
    }
}

/// 构建资源删除二次确认弹窗，明确展示目标并阻止底层页面交互。
fn delete_confirmation_modal(pending: &PendingDelete) -> Element<'_, ResourceManageMessage> {
    let header = row![
        container(icons::icon(Icon::TriangleAlert, 18, DANGER))
            .width(38)
            .height(38)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .style(delete_modal_icon_surface),
        column![
            raw(tr("resources.confirm.delete.title"))
                .size(18)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
            raw(tr("resources.confirm.delete.description"))
                .size(12)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
        ]
        .spacing(3)
        .width(Fill),
        button(crate::theme::muted_icon(Icon::X, 17))
            .on_press(ResourceManageMessage::CancelDelete)
            .width(32)
            .height(32)
            .style(button_style(ButtonVariant::Ghost)),
    ]
    .spacing(12)
    .align_y(Alignment::Center);

    let target = container(
        row![
            icons::icon(Icon::File, 15, DANGER),
            raw(&pending.label)
                .size(13)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
        ]
        .spacing(8)
        .align_y(Alignment::Center),
    )
    .width(Fill)
    .padding([10, 12])
    .style(delete_target_surface);

    let footer = row![
        space::horizontal(),
        button(
            raw(tr("resources.confirm.delete.cancel"))
                .size(12)
                .font(crate::core::typography::medium()),
        )
        .on_press(ResourceManageMessage::CancelDelete)
        .height(36)
        .padding([8, 16])
        .style(button_style(ButtonVariant::Secondary)),
        button(
            raw(tr("resources.confirm.delete.confirm"))
                .size(12)
                .font(crate::core::typography::medium())
                .color(WHITE),
        )
        .on_press(ResourceManageMessage::ConfirmDelete)
        .height(36)
        .padding([8, 16])
        .style(button_style(ButtonVariant::Destructive)),
    ]
    .spacing(8)
    .align_y(Alignment::Center);

    let panel = mouse_area(
        container(
            column![
                header,
                modal_separator(),
                target,
                raw(tr("resources.confirm.delete.warning"))
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
                modal_separator(),
                footer,
            ]
            .spacing(16),
        )
        .width(460)
        .padding(20)
        .style(delete_modal_surface),
    )
    .on_press(ResourceManageMessage::DeleteModalInteract);

    stack![
        button(space::Space::new())
            .on_press(ResourceManageMessage::DeleteModalInteract)
            .width(Fill)
            .height(Fill)
            .padding(0)
            .style(delete_modal_backdrop_style),
        container(panel)
            .width(Fill)
            .height(Fill)
            .padding(24)
            .align_x(Alignment::Center)
            .align_y(Alignment::Center),
    ]
    .width(Fill)
    .height(Fill)
    .into()
}

/// 返回当前语言下的稳定键值文案。
fn tr(key: &'static str) -> &'static str {
    t_in(key, current_language())
}

/// 删除确认弹窗中的横向分隔线。
fn modal_separator<'a>() -> Element<'a, ResourceManageMessage> {
    container(space::vertical())
        .width(Fill)
        .height(1)
        .style(modal_separator_surface)
        .into()
}

fn resource_tab(
    state: &ResourceManageState,
    tab: ResourceTab,
) -> Element<'static, ResourceManageMessage> {
    let active = state.tab == tab;
    let tab_icon: Element<'static, ResourceManageMessage> = if active {
        icons::icon(tab.icon(), 16, BLUE_600)
    } else {
        crate::theme::muted_icon(tab.icon(), 16)
    };
    button(
        column![
            container(
                row![
                    tab_icon,
                    text(tab.label_key())
                        .size(14)
                        .font(crate::core::typography::medium())
                        .style(move |theme| iced::widget::text::Style {
                            color: Some(if active {
                                theme.palette().primary
                            } else {
                                crate::theme::text_muted(theme)
                            }),
                        }),
                    container(
                        raw(state.resource_count(tab).to_string())
                            .size(12)
                            .font(crate::core::typography::medium())
                            .style(move |theme| iced::widget::text::Style {
                                color: Some(if active {
                                    theme.palette().primary
                                } else {
                                    crate::theme::text_muted(theme)
                                }),
                            }),
                    )
                    .padding([2, 7])
                    .style(move |theme| tab_count_surface(theme, active)),
                ]
                .spacing(8)
                .align_y(Alignment::Center),
            )
            .height(36)
            .align_y(Alignment::Center),
            container(space::vertical())
                .height(2)
                .width(Fill)
                .style(move |_theme| tab_indicator(active)),
        ]
        .align_x(Alignment::Center),
    )
    .on_press(ResourceManageMessage::SelectTab(tab))
    .padding([0, 10])
    .style(tab_button_style)
    .into()
}

fn resource_list(state: &ResourceManageState) -> Element<'_, ResourceManageMessage> {
    let content = match state.tab {
        ResourceTab::Characters => character_list(state),
        ResourceTab::WorldBooks => world_book_list(state),
        ResourceTab::Chats => chat_list(state),
        ResourceTab::Presets => preset_list(state),
    };
    container(
        column![
            container(
                row![
                    raw(tf("resources.list.heading", &[("tab", &t(state.tab.label_key()))]))
                        .size(14)
                        .font(crate::core::typography::medium())
                        .style(crate::theme::text_style),
                    space::horizontal(),
                    text("resources.list.sort_modified")
                        .size(12)
                        .font(crate::core::typography::regular())
                        .style(crate::theme::muted_text_style),
                ]
                .align_y(Alignment::Center),
            )
            .padding([11, 13])
            .style(panel_header_surface),
            content,
        ]
        .spacing(0)
        .height(Fill),
    )
    .width(LIST_WIDTH)
    .height(Fill)
    .style(panel_surface)
    .into()
}

fn character_list(state: &ResourceManageState) -> Element<'_, ResourceManageMessage> {
    let query = state.search.to_lowercase();
    let rows = state
        .characters
        .iter()
        .enumerate()
        .filter(|(_, item)| {
            query.is_empty()
                || item.name.to_lowercase().contains(&query)
                || item.filename.to_lowercase().contains(&query)
                || item
                    .tags
                    .iter()
                    .any(|tag| tag.to_lowercase().contains(&query))
        })
        .map(|(index, item)| {
            let selected = state.selected_character == Some(index);
            button(
                row![
                    container(
                        image(item.cover.clone())
                            .width(CHARACTER_THUMB_WIDTH)
                            .height(CHARACTER_THUMB_HEIGHT)
                            .content_fit(iced::ContentFit::Cover),
                    )
                    .width(CHARACTER_THUMB_WIDTH)
                    .height(CHARACTER_THUMB_HEIGHT)
                    .style(thumbnail_surface),
                    column![
                        raw(&item.name)
                            .size(14)
                            .font(crate::core::typography::medium())
                            .style(crate::theme::text_style),
                        raw(if item.creator.is_empty() {
                            t("resources.unknown_author").to_owned()
                        } else {
                            tf("resources.author.label", &[("name", &item.creator)])
                        })
                        .size(12)
                        .font(crate::core::typography::regular())
                        .style(crate::theme::muted_text_style),
                        raw(tf("resources.character.card_meta", &[("size", &format_size(item.file_size)), ("width", &item.image_width), ("height", &item.image_height), ("count", &item.tags.len())]))
                        .size(12)
                        .font(crate::core::typography::regular())
                        .style(crate::theme::muted_text_style),
                    ]
                    .spacing(5),
                    space::horizontal(),
                    if selected {
                        icons::icon(Icon::ChevronRight, 16, BLUE_600)
                    } else {
                        crate::theme::muted_icon(Icon::ChevronRight, 16)
                    },
                ]
                .spacing(10)
                .align_y(Alignment::Center),
            )
            .on_press(ResourceManageMessage::SelectCharacter(index))
            .width(Fill)
            .padding([9, 11])
            .style(move |theme, status| list_item_style(theme, selected, status))
            .into()
        })
        .collect::<Vec<Element<'_, ResourceManageMessage>>>();
    list_scroll(rows, "resources.list.empty.characters.title", "resources.list.empty.characters.hint")
}

fn world_book_list(state: &ResourceManageState) -> Element<'_, ResourceManageMessage> {
    let query = state.search.to_lowercase();
    let rows = state
        .world_books
        .iter()
        .enumerate()
        .filter(|(_, item)| {
            query.is_empty()
                || item.name.to_lowercase().contains(&query)
                || item.filename.to_lowercase().contains(&query)
                || item.author.to_lowercase().contains(&query)
        })
        .map(|(index, item)| {
            simple_list_item(
                Icon::BookMarked,
                &item.name,
                if item.author.is_empty() {
                    t("resources.unknown_author")
                } else {
                    &item.author
                },
                tf("resources.world_book.list_meta", &[("count", &item.entries.len()), ("size", &format_size(item.file_size))]),
                state.selected_world_book == Some(index),
                ResourceManageMessage::SelectWorldBook(index),
            )
        })
        .collect::<Vec<_>>();
    list_scroll(rows, "resources.list.empty.world_books.title", "resources.list.empty.world_books.hint")
}

fn chat_list(state: &ResourceManageState) -> Element<'_, ResourceManageMessage> {
    let query = state.search.to_lowercase();
    let mut rows = Vec::new();
    for (group_index, group) in state.chat_groups.iter().enumerate() {
        let matching = group
            .files
            .iter()
            .enumerate()
            .filter(|(_, file)| {
                query.is_empty()
                    || group.name.to_lowercase().contains(&query)
                    || file.filename.to_lowercase().contains(&query)
            })
            .collect::<Vec<_>>();
        if matching.is_empty() {
            continue;
        }
        rows.push(
            container(
                row![
                    icons::icon(Icon::UserRound, 13, BLUE_600),
                    raw(&group.name)
                        .size(12)
                        .font(crate::core::typography::medium())
                        .style(crate::theme::text_style),
                    space::horizontal(),
                    raw(tf("resources.chat.session_count", &[("count", &matching.len())]))
                        .size(12)
                        .font(crate::core::typography::regular())
                        .style(crate::theme::muted_text_style),
                ]
                .spacing(7)
                .align_y(Alignment::Center),
            )
            .padding([8, 11])
            .style(group_header_surface)
            .into(),
        );
        for (file_index, file) in matching {
            rows.push(simple_list_item(
                Icon::MessageCircle,
                &file.display_time,
                &file.filename,
                format_size(file.file_size),
                state.selected_chat == Some((group_index, file_index)),
                ResourceManageMessage::SelectChat(group_index, file_index),
            ));
        }
    }
    list_scroll(
        rows,
        "resources.list.empty.chats.title",
        "resources.list.empty.chats.hint",
    )
}

fn preset_list(state: &ResourceManageState) -> Element<'_, ResourceManageMessage> {
    if state.presets_loading {
        return preset_loading_panel();
    }

    let query = state.search.to_lowercase();
    let rows = state
        .presets
        .iter()
        .enumerate()
        .filter(|(_, item)| {
            query.is_empty()
                || item.name.to_lowercase().contains(&query)
                || item.filename.to_lowercase().contains(&query)
                || item.model.to_lowercase().contains(&query)
        })
        .map(|(index, item)| {
            let prompt_meta = if item.requires_tavern_helper {
                tf("resources.preset.prompt_meta_tavern", &[("count", &item.prompt_count), ("size", &format_size(item.file_size)), ("helper", &crate::lang::t("resources.preset.tavern_helper"))])
            } else {
                tf("resources.preset.prompt_meta", &[("count", &item.prompt_count), ("size", &format_size(item.file_size))])
            };
            simple_list_item(
                Icon::ListChecks,
                &item.name,
                if item.model.is_empty() {
                    t("resources.preset.unknown_model")
                } else {
                    &item.model
                },
                prompt_meta,
                state.selected_preset == Some(index),
                ResourceManageMessage::SelectPreset(index),
            )
        })
        .collect::<Vec<_>>();
    list_scroll(rows, "resources.list.empty.presets.title", "resources.list.empty.presets.hint")
}

/// 预设列表正在读取时的占位界面，避免用户误以为没有预设。
fn preset_loading_panel() -> Element<'static, ResourceManageMessage> {
    container(
        column![
            crate::theme::subtle_icon(Icon::LoaderCircle, 28),
            text("resources.preset.loading")
                .size(13)
                .font(crate::core::typography::medium())
                .style(crate::theme::muted_text_style),
            text("resources.preset.loading_hint")
                .size(11)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
        ]
        .spacing(9)
        .align_x(Alignment::Center),
    )
    .width(Fill)
    .height(Fill)
    .align_x(Alignment::Center)
    .align_y(Alignment::Center)
    .into()
}

fn simple_list_item<'a>(
    icon: Icon,
    title: &'a str,
    subtitle: &'a str,
    meta: String,
    selected: bool,
    message: ResourceManageMessage,
) -> Element<'a, ResourceManageMessage> {
    let leading_icon: Element<'a, ResourceManageMessage> = if selected {
        icons::icon(icon, 19, BLUE_600)
    } else {
        crate::theme::muted_icon(icon, 19)
    };
    let trailing_icon: Element<'a, ResourceManageMessage> = if selected {
        icons::icon(Icon::ChevronRight, 16, BLUE_600)
    } else {
        crate::theme::muted_icon(Icon::ChevronRight, 16)
    };

    button(
        row![
            container(leading_icon)
                .width(40)
                .height(40)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(move |theme| item_icon_surface(theme, selected)),
            column![
                raw(title)
                    .size(13)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::text_style),
                raw(truncate(subtitle, 35))
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
                raw(meta)
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(4),
            space::horizontal(),
            trailing_icon,
        ]
        .spacing(10)
        .align_y(Alignment::Center),
    )
    .on_press(message)
    .width(Fill)
    .padding([10, 12])
    .style(move |theme, status| list_item_style(theme, selected, status))
    .into()
}

fn list_scroll<'a>(
    rows: Vec<Element<'a, ResourceManageMessage>>,
    title: &'static str,
    description: &'static str,
) -> Element<'a, ResourceManageMessage> {
    if rows.is_empty() {
        return container(
            column![
                crate::theme::subtle_icon(Icon::Inbox, 25),
                text(title)
                    .size(13)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::muted_text_style),
                text(description)
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(7)
            .align_x(Alignment::Center),
        )
        .width(Fill)
        .height(Fill)
        .align_x(Alignment::Center)
        .align_y(Alignment::Center)
        .into();
    }
    scrollable(column(rows).spacing(2).padding(6))
        .height(Fill)
        .into()
}

fn resource_detail<'a>(
    state: &'a ResourceManageState,
    theme: &Theme,
) -> Element<'a, ResourceManageMessage> {
    let content = match state.tab {
        ResourceTab::Characters => state
            .selected_character
            .and_then(|index| state.characters.get(index))
            .map(character_detail),
        ResourceTab::WorldBooks => state
            .selected_world_book
            .and_then(|index| state.world_books.get(index))
            .map(world_book_detail),
        ResourceTab::Chats => state.selected_chat.and_then(|(group, file)| {
            state.chat_groups.get(group).and_then(|group| {
                group
                    .files
                    .get(file)
                    .map(|item| chat_detail(group, item, state, theme))
            })
        }),
        ResourceTab::Presets => state
            .selected_preset
            .and_then(|index| state.presets.get(index))
            .map(|preset| {
                if state.preset_detail_loading {
                    preset_detail_loading(preset)
                } else if let Some(error) = state.preset_detail_error.as_deref() {
                    preset_detail_error(preset, error)
                } else {
                    preset_detail(
                        preset,
                        state.preset_detail_page,
                        state.preset_pager_progress,
                    )
                }
            }),
    };

    container(content.unwrap_or_else(|| {
        empty_page(
            state.tab.icon(),
            "resources.detail.empty.title",
            match state.tab {
                ResourceTab::Characters => "resources.detail.empty.characters",
                ResourceTab::WorldBooks => "resources.detail.empty.world_books",
                ResourceTab::Chats => "resources.detail.empty.chats",
                ResourceTab::Presets => "resources.detail.empty.presets",
            },
        )
    }))
    .width(Fill)
    .height(Fill)
    .style(panel_surface)
    .into()
}

fn detail_header<'a>(
    icon: Icon,
    title: &'a str,
    subtitle: String,
) -> Element<'a, ResourceManageMessage> {
    container(
        row![
            container(icons::icon(icon, 18, BLUE_600))
                .width(38)
                .height(38)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(page_icon_surface),
            column![
                raw(title)
                    .size(17)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::text_style),
                raw(subtitle)
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(3),
            space::horizontal(),
            tooltip(
                button(icons::icon(Icon::Trash2, 15, DANGER))
                    .on_press(ResourceManageMessage::RequestDelete)
                    .width(34)
                    .height(34)
                    .style(danger_outline_button_style),
                container(text("resources.detail.delete").size(12))
                    .padding([5, 8])
                    .style(tooltip_surface),
                tooltip::Position::Bottom,
            ),
        ]
        .spacing(10)
        .align_y(Alignment::Center),
    )
    .width(Fill)
    .padding([11, 13])
    .style(panel_header_surface)
    .into()
}

fn character_detail(item: &CharacterCardInfo) -> Element<'_, ResourceManageMessage> {
    let tags: Element<'_, ResourceManageMessage> = if item.tags.is_empty() {
        text("resources.character.no_tags")
            .size(12)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style)
            .into()
    } else {
        row(item.tags.iter().take(8).map(|tag| tag_chip(tag, BLUE_600)))
            .spacing(5)
            .into()
    };
    let mut content = column![
        row![
            container(
                image(item.cover.clone())
                    .width(140)
                    .height(196)
                    .content_fit(iced::ContentFit::Cover),
            )
            .width(140)
            .height(196)
            .style(detail_image_surface),
            column![
                info_grid_row(
                    "resources.character.creator",
                    value_or(&item.creator, "resources.unknown"),
                    "workbench.field.version",
                    value_or(&item.version, "resources.unlabeled")
                ),
                info_grid_row(
                    "resources.character.spec",
                    spec_label(item),
                    "resources.character.image_size",
                    format!("{} × {}", item.image_width, item.image_height)
                ),
                info_grid_row(
                    "resources.file_size",
                    format_size(item.file_size),
                    "resources.character.embedded_world",
                    if item.world_entries.is_empty() {
                        t("resources.none").to_owned()
                    } else {
                        tf("resources.world.entries_count", &[("count", &item.world_entries.len())])
                    }
                ),
                text("resources.character.tags")
                    .size(12)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::muted_text_style),
                tags,
            ]
            .spacing(9),
        ]
        .spacing(15),
        detail_section("workbench.character.description", &item.description),
        detail_section("workbench.character.personality", &item.personality),
        detail_section("workbench.character.scenario", &item.scenario),
        detail_section("workbench.character.first_message", &item.first_message),
    ]
    .spacing(14);
    if !item.world_entries.is_empty() {
        content = content.push(section_heading(
            Icon::BookOpenText,
            if item.world_name.is_empty() {
                t("resources.character.embedded_world")
            } else {
                item.world_name.as_str()
            },
            tf("resources.entries_count", &[("count", &item.world_entries.len())]),
        ));
        for entry in item.world_entries.iter().take(30) {
            content = content.push(world_entry_card(entry));
        }
    }

    column![
        detail_header(
            Icon::ContactRound,
            &item.name,
            format!("{} · {}", item.filename, format_size(item.file_size)),
        ),
        scrollable(container(content).padding(14)).height(Fill),
    ]
    .height(Fill)
    .into()
}

fn world_book_detail(item: &WorldBookInfo) -> Element<'_, ResourceManageMessage> {
    let mut content = column![
        row![
            metric_card(
                Icon::ListTree,
                "resources.world_book.entries",
                item.entries.len().to_string(),
                BLUE_600
            ),
            metric_card(
                Icon::CircleCheck,
                "workbench.field.enabled",
                item.entries
                    .iter()
                    .filter(|entry| entry.enabled)
                    .count()
                    .to_string(),
                SUCCESS,
            ),
            metric_card(
                Icon::HardDrive,
                "resources.size",
                format_size(item.file_size),
                Color::from_rgb8(142, 68, 220)
            ),
        ]
        .spacing(9),
        section_heading(
            Icon::ListTree,
            t("resources.world_book.entries_heading"),
            if item.author.is_empty() {
                t("resources.unknown_author").to_owned()
            } else {
                tf("resources.author.label", &[("name", &item.author)])
            },
        ),
    ]
    .spacing(12);
    for entry in item.entries.iter().take(100) {
        content = content.push(world_entry_card(entry));
    }
    if item.entries.is_empty() {
        content = content.push(inline_empty("resources.world_book.no_entries"));
    }
    column![
        detail_header(
            Icon::BookMarked,
            &item.name,
            format!("{} · {}", item.filename, format_size(item.file_size)),
        ),
        scrollable(container(content).padding(14)).height(Fill),
    ]
    .height(Fill)
    .into()
}

fn chat_detail<'a>(
    group: &'a ChatGroup,
    file: &'a ChatFileInfo,
    state: &'a ResourceManageState,
    theme: &Theme,
) -> Element<'a, ResourceManageMessage> {
    let mut messages = column![
        row![
            metric_card(
                Icon::MessagesSquare,
                "resources.chat.messages",
                state.chat_messages.len().to_string(),
                BLUE_600
            ),
            metric_card(
                Icon::UserRound,
                "resources.chat.user_messages",
                state
                    .chat_messages
                    .iter()
                    .filter(|message| message.is_user)
                    .count()
                    .to_string(),
                SUCCESS,
            ),
            metric_card(
                Icon::HardDrive,
                "resources.size",
                format_size(file.file_size),
                Color::from_rgb8(142, 68, 220)
            ),
        ]
        .spacing(9),
        section_heading(
            Icon::MessageCircle,
            t("resources.chat.preview"),
            t("resources.chat.preview_hint").to_owned()
        ),
    ]
    .spacing(11);
    if state.chat_messages.is_empty() {
        messages = messages.push(inline_empty("resources.chat.no_messages"));
    } else {
        for message in &state.chat_messages {
            messages = messages.push(chat_bubble(message, theme));
        }
    }
    column![
        detail_header(
            Icon::MessagesSquare,
            &group.name,
            format!("{} · {}", file.display_time, format_size(file.file_size)),
        ),
        scrollable(container(messages).padding(14)).height(Fill),
    ]
    .height(Fill)
    .into()
}

fn preset_detail_loading(item: &PresetInfo) -> Element<'_, ResourceManageMessage> {
    column![
        detail_header(
            Icon::SlidersHorizontal,
            &item.name,
            format!("{} · {}", item.filename, format_size(item.file_size)),
        ),
        container(
            column![
                crate::theme::subtle_icon(Icon::LoaderCircle, 32),
                text("resources.preset.detail_loading")
                    .size(14)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::muted_text_style),
                text("resources.preset.detail_loading_hint")
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(10)
            .align_x(Alignment::Center),
        )
        .width(Fill)
        .height(Fill)
        .align_x(Alignment::Center)
        .align_y(Alignment::Center),
    ]
    .height(Fill)
    .into()
}

fn preset_detail_error<'a>(
    item: &'a PresetInfo,
    error: &'a str,
) -> Element<'a, ResourceManageMessage> {
    column![
        detail_header(
            Icon::SlidersHorizontal,
            &item.name,
            format!("{} · {}", item.filename, format_size(item.file_size)),
        ),
        container(
            column![
                crate::theme::subtle_icon(Icon::CircleAlert, 28),
                text("resources.preset.detail_load_failed")
                    .size(14)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::text_style),
                raw(error)
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(9)
            .align_x(Alignment::Center),
        )
        .width(Fill)
        .height(Fill)
        .align_x(Alignment::Center)
        .align_y(Alignment::Center),
    ]
    .height(Fill)
    .into()
}

fn preset_detail(
    item: &PresetInfo,
    detail_page: usize,
    pager_progress: f32,
) -> Element<'_, ResourceManageMessage> {
    let page_count = item.prompt_count.div_ceil(PRESET_PROMPTS_PER_PAGE);
    let current_page = detail_page.min(page_count.saturating_sub(1));
    let mut content = column![
        row![
            metric_card(
                Icon::ListChecks,
                "resources.preset.prompts",
                item.prompt_count.to_string(),
                BLUE_600
            ),
            metric_card(
                Icon::CircleCheck,
                "workbench.field.enabled",
                item.enabled_prompt_count.to_string(),
                SUCCESS,
            ),
            // 将预设格式与运行依赖分开显示，避免用户把 SPreset 误认为依赖状态。
            metric_card(
                Icon::PlugZap,
                "resources.preset.format",
                // SPreset 是格式专名（不翻译），标准格式走文案键。
                if item.has_spreset {
                    "SPreset".to_owned()
                } else {
                    t("resources.preset.standard").to_owned()
                },
                if item.has_spreset {
                    TAVERN_HELPER_ACCENT
                } else {
                    INK_MUTED
                },
            ),
            metric_card(
                Icon::Puzzle,
                "resources.preset.tavern_helper",
                t(if item.requires_tavern_helper {
                    "common.yes"
                } else {
                    "common.no"
                })
                .to_owned(),
                if item.requires_tavern_helper {
                    TAVERN_HELPER_ACCENT
                } else {
                    INK_MUTED
                },
            ),
        ]
        .spacing(9),
        container(
            column![
                info_grid_row(
                    "resources.preset.source",
                    value_or(&item.source, "resources.unspecified"),
                    "resources.preset.model",
                    value_or(&item.model, "resources.unspecified")
                ),
                info_grid_row(
                    "resources.preset.context",
                    number_or(item.max_context),
                    "resources.preset.max_tokens",
                    number_or(item.max_tokens)
                ),
                info_grid_row(
                    "resources.preset.stream",
                    bool_label(item.stream).into(),
                    "resources.file_size",
                    format_size(item.file_size)
                ),
            ]
            .spacing(8),
        )
        .padding(12)
        .style(info_surface),
        section_heading(
            Icon::ListChecks,
            t("resources.preset.prompt_structure"),
            tf("resources.entries_count", &[("count", &item.prompt_count)])
        ),
    ]
    .spacing(12);
    if item.prompt_count == 0 {
        content = content.push(inline_empty("resources.preset.no_prompts"));
    } else {
        let start = current_page * PRESET_PROMPTS_PER_PAGE;
        let end = (start + PRESET_PROMPTS_PER_PAGE).min(item.prompts.len());
        for (index, prompt) in item.prompts[start..end].iter().enumerate() {
            content = content.push(prompt_card(start + index, prompt));
        }
    }
    // 分页栏悬浮在右下角：滑到列表底部也能直接翻页，收起时只留一条拉手不挡内容。
    // 它和内容同属普通层，弹窗等上层浮层依旧会盖住它，不需要额外处理层级。
    stack![
        column![
            detail_header(
                Icon::SlidersHorizontal,
                &item.name,
                format!("{} · {}", item.filename, format_size(item.file_size)),
            ),
            scrollable(container(content).padding(14)).height(Fill),
        ]
        .height(Fill),
        crate::pages::pager::floating(
            current_page,
            page_count,
            pager_progress,
            ResourceManageMessage::PresetDetailGoToPage,
            ResourceManageMessage::PresetPagerHover,
        ),
    ]
    .width(Fill)
    .height(Fill)
    .into()
}

fn detail_section<'a>(title: &'static str, value: &'a str) -> Element<'a, ResourceManageMessage> {
    container(
        column![
            text(title)
                .size(12)
                .font(crate::core::typography::medium())
                .style(crate::theme::muted_text_style),
            raw(if value.trim().is_empty() {
                t("resources.empty_value")
            } else {
                value
            })
            .size(12)
            .font(crate::core::typography::regular())
            .style(move |theme| iced::widget::text::Style {
                color: Some(if value.trim().is_empty() {
                    crate::theme::text_subtle(theme)
                } else {
                    crate::theme::text(theme)
                }),
            }),
        ]
        .spacing(6),
    )
    .width(Fill)
    .padding(12)
    .style(info_surface)
    .into()
}

fn info_grid_row<'a>(
    first_label: &'static str,
    first_value: String,
    second_label: &'static str,
    second_value: String,
) -> Element<'a, ResourceManageMessage> {
    row![
        info_pair(first_label, first_value),
        info_pair(second_label, second_value),
    ]
    .spacing(8)
    .into()
}

fn info_pair<'a>(label: &'static str, value: String) -> Element<'a, ResourceManageMessage> {
    container(
        column![
            text(label)
                .size(12)
                .font(crate::core::typography::medium())
                .style(crate::theme::muted_text_style),
            raw(value)
                .size(12)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
        ]
        .spacing(2),
    )
    .width(Fill)
    .padding([7, 9])
    .style(meta_surface)
    .into()
}

fn metric_card(
    icon: Icon,
    label: &'static str,
    value: String,
    accent: Color,
) -> Element<'static, ResourceManageMessage> {
    container(
        row![
            container(icons::icon(icon, 15, accent))
                .width(30)
                .height(30)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(move |_theme| accent_surface(accent)),
            column![
                text(label)
                    .size(12)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::muted_text_style),
                raw(value)
                    .size(14)
                    .font(crate::core::typography::medium())
                    .style(crate::theme::text_style),
            ]
            .spacing(2),
        ]
        .spacing(8)
        .align_y(Alignment::Center),
    )
    .width(Fill)
    .padding([9, 10])
    .style(info_surface)
    .into()
}

fn section_heading<'a>(
    icon: Icon,
    title: &'a str,
    meta: String,
) -> Element<'a, ResourceManageMessage> {
    row![
        icons::icon(icon, 14, BLUE_600),
        raw(title)
            .size(13)
            .font(crate::core::typography::medium())
            .style(crate::theme::text_style),
        space::horizontal(),
        raw(meta)
            .size(12)
            .font(crate::core::typography::regular())
            .style(crate::theme::muted_text_style),
    ]
    .spacing(7)
    .align_y(Alignment::Center)
    .into()
}

fn world_entry_card(entry: &WorldEntry) -> Element<'_, ResourceManageMessage> {
    let keywords = if entry.keys.is_empty() {
        t("resources.world_book.no_keywords").to_owned()
    } else {
        entry.keys.join("、")
    };
    let status_icon: Element<'_, ResourceManageMessage> = if entry.enabled {
        icons::icon(Icon::CircleCheck, 15, SUCCESS)
    } else {
        crate::theme::muted_icon(Icon::CircleOff, 15)
    };
    container(
        column![
            row![
                container(status_icon).width(26),
                raw(if entry.comment.is_empty() {
                    t("resources.world_book.unnamed_entry")
                } else {
                    entry.comment.as_str()
                })
                .size(12)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
                space::horizontal(),
                text(if entry.enabled {
                    "workbench.field.enabled"
                } else {
                    "workbench.field.disabled"
                })
                .size(12)
                .font(crate::core::typography::medium())
                .style(move |theme| iced::widget::text::Style {
                    color: Some(if entry.enabled {
                        SUCCESS
                    } else {
                        crate::theme::text_muted(theme)
                    }),
                }),
            ]
            .align_y(Alignment::Center),
            raw(tf("resources.world_book.keywords", &[("keywords", &truncate(&keywords, 80))]))
                .size(12)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
            raw(if entry.content.trim().is_empty() {
                t("resources.world_book.no_content").to_owned()
            } else {
                truncate(&entry.content, 220)
            })
            .size(12)
            .font(crate::core::typography::regular())
            .style(crate::theme::text_style),
        ]
        .spacing(7),
    )
    .width(Fill)
    .padding(12)
    .style(info_surface)
    .into()
}

fn chat_bubble<'a>(message: &'a ChatMessage, theme: &Theme) -> Element<'a, ResourceManageMessage> {
    let accent = if message.is_user {
        BLUE_600
    } else {
        Color::from_rgb8(142, 68, 220)
    };
    // 解析结果为空（例如整条消息只有被忽略的 HTML 标签）时退回原文，避免气泡完全空白。
    let body: Element<'a, ResourceManageMessage> = if message.markdown.is_empty() {
        raw(&message.content)
            .size(12)
            .font(crate::core::typography::regular())
            .style(crate::theme::text_style)
            .into()
    } else {
        // 正文字号与气泡内的原纯文本保持一致（12px）。
        super::markdown_doc::view(
            &message.markdown,
            theme,
            12.0,
            ResourceManageMessage::OpenMarkdownLink,
        )
    };
    container(
        column![
            row![
                icons::icon(
                    if message.is_user {
                        Icon::UserRound
                    } else {
                        Icon::Bot
                    },
                    13,
                    accent
                ),
                raw(if message.name.is_empty() {
                    if message.is_user {
                        t("resources.chat.user")
                    } else {
                        t("resources.chat.role")
                    }
                } else {
                    message.name.as_str()
                })
                .size(12)
                .font(crate::core::typography::medium())
                .color(accent),
                space::horizontal(),
                raw(&message.send_date)
                    .size(12)
                    .font(crate::core::typography::regular())
                    .style(crate::theme::muted_text_style),
            ]
            .spacing(6)
            .align_y(Alignment::Center),
            body,
        ]
        .spacing(6),
    )
    .width(Fill)
    .padding(11)
    .style(move |_theme| chat_surface(accent))
    .into()
}

fn prompt_card(index: usize, prompt: &PresetPrompt) -> Element<'_, ResourceManageMessage> {
    let role = if prompt.role.is_empty() {
        "marker"
    } else {
        &prompt.role
    };
    let role_chip = if prompt.partial {
        tag_chip(t_in("workbench.preset.partial", current_language()), BLUE_600)
    } else if prompt.enabled {
        tag_chip(role, SUCCESS)
    } else {
        muted_tag_chip(role)
    };
    // 这里只判断字符串是否为空，避免查看详情时再次扫描可能很长的提示词正文。
    let content_is_empty = prompt.content.is_empty();
    container(
        column![
            row![
                container(
                    raw((index + 1).to_string())
                        .size(12)
                        .font(crate::core::typography::medium())
                        .color(BLUE_600),
                )
                .width(26)
                .height(26)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(count_surface),
                raw(if prompt.name.is_empty() {
                    t("resources.preset.unnamed_prompt")
                } else {
                    prompt.name.as_str()
                })
                .size(12)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
                space::horizontal(),
                role_chip,
            ]
            .spacing(8)
            .align_y(Alignment::Center),
            raw(if prompt.marker {
                t("resources.preset.marker_note").to_owned()
            } else if content_is_empty {
                t("resources.world_book.no_content").to_owned()
            } else {
                truncate(&prompt.content, 260)
            })
            .size(12)
            .font(crate::core::typography::regular())
            .style(move |theme| iced::widget::text::Style {
                color: Some(if content_is_empty {
                    crate::theme::text_subtle(theme)
                } else {
                    crate::theme::text(theme)
                }),
            }),
        ]
        .spacing(8),
    )
    .width(Fill)
    .padding(12)
    .style(info_surface)
    .into()
}

fn tag_chip<'a>(label: &'a str, color: Color) -> Element<'a, ResourceManageMessage> {
    container(
        raw(label)
            .size(12)
            .font(crate::core::typography::medium())
            .color(color),
    )
    .padding([3, 7])
    .style(move |_theme| accent_surface(color))
    .into()
}

fn muted_tag_chip(label: &str) -> Element<'_, ResourceManageMessage> {
    container(
        raw(label)
            .size(12)
            .font(crate::core::typography::medium())
            .style(crate::theme::muted_text_style),
    )
    .padding([3, 7])
    .style(|theme| accent_surface(crate::theme::text_muted(theme)))
    .into()
}

fn inline_empty(message: &'static str) -> Element<'static, ResourceManageMessage> {
    container(
        row![
            crate::theme::subtle_icon(Icon::Inbox, 15),
            text(message)
                .size(12)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
        ]
        .spacing(7)
        .align_y(Alignment::Center),
    )
    .width(Fill)
    .padding(13)
    .style(info_surface)
    .into()
}

fn empty_page(
    icon: Icon,
    title: &'static str,
    description: &'static str,
) -> Element<'static, ResourceManageMessage> {
    container(
        column![
            container(icons::icon(icon, 26, BLUE_600))
                .width(58)
                .height(58)
                .align_x(Alignment::Center)
                .align_y(Alignment::Center)
                .style(page_icon_surface),
            text(title)
                .size(15)
                .font(crate::core::typography::medium())
                .style(crate::theme::text_style),
            text(description)
                .size(12)
                .font(crate::core::typography::regular())
                .style(crate::theme::muted_text_style),
        ]
        .spacing(9)
        .align_x(Alignment::Center),
    )
    .width(Fill)
    .height(Fill)
    .align_x(Alignment::Center)
    .align_y(Alignment::Center)
    .into()
}

fn value_or(value: &str, fallback: &str) -> String {
    if value.trim().is_empty() {
        // fallback 是文案键；resolve 命中键表就翻译，未命中（纯数据）原样返回。
        crate::lang::resolve(fallback)
    } else {
        value.into()
    }
}

fn bool_label(value: bool) -> String {
    // 流式输出开关：开启 / 关闭。
    if value {
        t("extensions.on").to_owned()
    } else {
        t("extensions.off").to_owned()
    }
}

fn number_or(value: i64) -> String {
    if value > 0 {
        value.to_string()
    } else {
        t("resources.not_set").to_owned()
    }
}

fn spec_label(item: &CharacterCardInfo) -> String {
    match (item.spec.trim(), item.spec_version.trim()) {
        ("", "") => t("resources.unlabeled").to_owned(),
        (spec, "") => spec.into(),
        ("", version) => version.into(),
        (spec, version) => format!("{spec} {version}"),
    }
}

fn page_icon_surface(_theme: &Theme) -> container::Style {
    accent_surface(BLUE_600)
}

fn panel_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 8.0.into(),
        },
        ..container::Style::default()
    }
}

fn panel_header_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 0.0,
            radius: 8.0.into(),
        },
        ..container::Style::default()
    }
}

fn count_surface(_theme: &Theme) -> container::Style {
    accent_surface(BLUE_600)
}

fn tab_count_surface(theme: &Theme, active: bool) -> container::Style {
    accent_surface(if active {
        theme.palette().primary
    } else {
        crate::theme::text_muted(theme)
    })
}

fn tab_button_style(theme: &Theme, status: button::Status) -> button::Style {
    button::Style {
        background: matches!(status, button::Status::Hovered)
            .then_some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            radius: 6.0.into(),
            ..Border::default()
        },
        ..button::Style::default()
    }
}

fn tab_indicator(active: bool) -> container::Style {
    container::Style {
        background: active.then_some(Background::Color(BLUE_600)),
        border: Border {
            radius: 2.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

fn list_item_style(theme: &Theme, selected: bool, status: button::Status) -> button::Style {
    let background = if selected {
        Some(Background::Color(Color::from_rgba(
            theme.palette().primary.r,
            theme.palette().primary.g,
            theme.palette().primary.b,
            if crate::theme::is_dark(theme) {
                0.24
            } else {
                0.12
            },
        )))
    } else if matches!(status, button::Status::Hovered) {
        Some(Background::Color(crate::theme::surface_alt(theme)))
    } else {
        None
    };
    button::Style {
        background,
        border: Border {
            color: if selected {
                Color::from_rgba(
                    theme.palette().primary.r,
                    theme.palette().primary.g,
                    theme.palette().primary.b,
                    0.45,
                )
            } else {
                Color::TRANSPARENT
            },
            width: 1.0,
            radius: 7.0.into(),
        },
        ..button::Style::default()
    }
}

fn item_icon_surface(theme: &Theme, selected: bool) -> container::Style {
    accent_surface(if selected {
        theme.palette().primary
    } else {
        crate::theme::text_muted(theme)
    })
}

fn thumbnail_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 6.0.into(),
        },
        ..container::Style::default()
    }
}

fn detail_image_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 8.0.into(),
        },
        ..container::Style::default()
    }
}

fn group_header_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 0.0,
            radius: 6.0.into(),
        },
        ..container::Style::default()
    }
}

fn info_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 7.0.into(),
        },
        ..container::Style::default()
    }
}

fn meta_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 6.0.into(),
        },
        ..container::Style::default()
    }
}

fn accent_surface(accent: Color) -> container::Style {
    container::Style {
        background: Some(Background::Color(Color::from_rgba(
            accent.r, accent.g, accent.b, 0.10,
        ))),
        border: Border {
            radius: 6.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

fn chat_surface(accent: Color) -> container::Style {
    container::Style {
        background: Some(Background::Color(Color::from_rgba(
            accent.r, accent.g, accent.b, 0.055,
        ))),
        border: Border {
            color: Color::from_rgba(accent.r, accent.g, accent.b, 0.18),
            width: 1.0,
            radius: 7.0.into(),
        },
        ..container::Style::default()
    }
}

fn delete_modal_icon_surface(_theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(Color::from_rgba(
            DANGER.r, DANGER.g, DANGER.b, 0.10,
        ))),
        border: Border {
            radius: 8.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

fn delete_target_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface_alt(theme))),
        border: Border {
            color: Color::from_rgba(DANGER.r, DANGER.g, DANGER.b, 0.24),
            width: 1.0,
            radius: 8.0.into(),
        },
        ..container::Style::default()
    }
}

fn delete_modal_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::surface(theme))),
        border: Border {
            color: crate::theme::line(theme),
            width: 1.0,
            radius: 8.0.into(),
        },
        shadow: iced::Shadow {
            color: Color::from_rgba(
                0.0,
                0.0,
                0.0,
                if crate::theme::is_dark(theme) {
                    0.46
                } else {
                    0.20
                },
            ),
            offset: iced::Vector::new(0.0, 10.0),
            blur_radius: 30.0,
        },
        ..container::Style::default()
    }
}

fn modal_separator_surface(theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(crate::theme::line(theme))),
        ..container::Style::default()
    }
}

fn delete_modal_backdrop_style(_theme: &Theme, _status: button::Status) -> button::Style {
    button::Style {
        background: Some(Background::Color(Color::from_rgba(0.0, 0.0, 0.0, 0.52))),
        ..button::Style::default()
    }
}

fn tooltip_surface(_theme: &Theme) -> container::Style {
    container::Style {
        background: Some(Background::Color(Color::from_rgb8(38, 38, 42))),
        text_color: Some(WHITE),
        border: Border {
            radius: 5.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

fn danger_outline_button_style(_theme: &Theme, status: button::Status) -> button::Style {
    button::Style {
        background: matches!(status, button::Status::Hovered).then_some(Background::Color(
            Color::from_rgba(DANGER.r, DANGER.g, DANGER.b, 0.08),
        )),
        border: Border {
            color: Color::from_rgba(DANGER.r, DANGER.g, DANGER.b, 0.35),
            width: 1.0,
            radius: 6.0.into(),
        },
        ..button::Style::default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chat_filename_is_presented_without_character_prefix() {
        assert_eq!(
            chat_display_time("Seraphina - 2026-06-15@17h18m44s700ms.jsonl"),
            "2026-06-15@17h18m44s700ms"
        );
    }

    #[test]
    fn transient_notice_is_consumed_once() {
        let mut state = ResourceManageState::default();
        state.notice = Some(TransientNotice::info(
            "notice.refresh_complete",
            "资源目录已重新扫描。",
        ));

        assert!(state.take_notice().is_some());
        assert!(state.take_notice().is_none());
    }

    #[test]
    fn starts_preset_scan_while_viewing_another_resource_tab() {
        let mut state = ResourceManageState::default();
        state.data_root = Some(std::path::PathBuf::from(r"C:\AstraBrew\resources"));

        let _task = state.update(ResourceManageMessage::SearchChanged("Astra".into()));

        assert!(state.presets_loading);
    }

    #[test]
    fn preset_pager_slide_requests_are_coalesced() {
        let mut state = ResourceManageState::default();
        assert!(!state.pager_animating());

        state.set_pager_open(true);
        let slide = state.preset_pager_slide.expect("悬停应启动滑动动画");
        assert_eq!(slide.to, 1.0);
        assert!(state.pager_animating());

        // 同一个目标的重复请求不能把动画重新从起点拉起。
        state.set_pager_open(true);
        assert_eq!(
            state.preset_pager_slide.unwrap().started_at,
            slide.started_at
        );
    }

    #[test]
    fn preset_pager_reverse_slide_starts_from_current_progress() {
        let mut state = ResourceManageState::default();
        state.set_pager_open(true);
        // 模拟滑出进行到一半时指针又移开了。
        state.preset_pager_progress = 0.4;
        state.set_pager_open(false);

        let slide = state.preset_pager_slide.expect("反向动画应存在");
        assert_eq!(slide.from, 0.4, "反向动画必须从当前进度出发，否则会跳变");
        assert_eq!(slide.to, 0.0);
    }

    #[test]
    fn preset_pager_slide_finishes_at_target_and_stops() {
        let mut state = ResourceManageState::default();
        for (from, to) in [(0.0, 1.0), (1.0, 0.0)] {
            state.set_pager_open(to == 1.0);
            // 直接构造一个已经到期的动画帧，验证推进后停在目标值并解除订阅。
            state.preset_pager_slide = Some(PagerSlide {
                from,
                to,
                started_at: Instant::now() - std::time::Duration::from_secs(1),
            });
            state.advance_pager_slide();
            assert_eq!(state.preset_pager_progress, to);
            assert!(!state.pager_animating(), "到位后不应继续订阅每帧推进");
        }

        state.set_pager_open(true);
        state.reset_pager();
        assert_eq!(state.preset_pager_progress, 0.0);
        assert!(!state.pager_animating(), "切换预设后应回到收起状态");
    }

    #[test]
    fn preset_page_jump_clamps_into_range() {
        let mut state = ResourceManageState::default();
        state.presets = vec![PresetInfo {
            filename: "fixture.json".into(),
            filepath: PathBuf::from(r"C:\AstraBrew\fixture.json"),
            name: "fixture".into(),
            source: String::new(),
            model: String::new(),
            max_context: 0,
            max_tokens: 0,
            stream: false,
            // 9 条提示词按每页 4 条切成 3 页。
            prompt_count: 9,
            enabled_prompt_count: 0,
            prompts: Vec::new(),
            has_spreset: false,
            requires_tavern_helper: false,
            file_size: 0,
            modified_secs: 0,
            prompts_loaded: true,
        }];
        state.selected_preset = Some(0);

        let _task = state.update(ResourceManageMessage::PresetDetailGoToPage(1));
        assert_eq!(state.preset_detail_page, 1);
        let _task = state.update(ResourceManageMessage::PresetDetailGoToPage(99));
        assert_eq!(state.preset_detail_page, 2, "越界页码应被夹到末页");
    }
}
