use eframe::egui;
use egui::{FontData, FontDefinitions, FontFamily};

fn main() -> eframe::Result {

    let options = eframe::NativeOptions::default();

    eframe::run_native(
        "中文测试",
        options,
        Box::new(|cc| {

            setup_fonts(&cc.egui_ctx);

            Ok(Box::new(MyApp))
        }),
    )
}

fn setup_fonts(ctx: &egui::Context) {

    let mut fonts = FontDefinitions::default();

    // 加载中文字体
    fonts.font_data.insert(
        "MiSans".to_owned(),
        FontData::from_static(include_bytes!(
            "../assets/fonts/MiSans-Regular.ttf"
        ))
        .into(),
    );

    // 放到最前面
    fonts
        .families
        .entry(FontFamily::Proportional)
        .or_default()
        .insert(0, "MiSans".to_owned());

    fonts
        .families
        .entry(FontFamily::Monospace)
        .or_default()
        .insert(0, "MiSans".to_owned());

    ctx.set_fonts(fonts);
}

struct MyApp;

impl eframe::App for MyApp {

    fn update(
        &mut self,
        ctx: &egui::Context,
        _frame: &mut eframe::Frame,
    ) {

        egui::CentralPanel::default().show(ctx, |ui| {

            ui.heading("酒馆启动器");
            ui.label("中文已经正常显示");
        });
    }
}