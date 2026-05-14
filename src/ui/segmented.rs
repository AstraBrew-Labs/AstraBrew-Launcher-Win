use eframe::egui;

pub fn segmented_control<T: PartialEq + Clone>(
    ui: &mut egui::Ui,
    current_value: &mut T,
    options: &[(T, impl Into<egui::WidgetText> + Clone)],
) -> egui::Response {
    let frame = egui::Frame::NONE
        .fill(ui.visuals().faint_bg_color)
        .corner_radius(6.0)
        .inner_margin(2.0);

    let mut changed = false;
    let mut final_response: Option<egui::Response> = None;

    frame.show(ui, |ui| {
        ui.horizontal(|ui| {
            ui.spacing_mut().item_spacing.x = 2.0;
            for (value, label) in options {
                let is_selected = *current_value == *value;

                let mut button = egui::Button::new(label.clone().into());

                if is_selected {
                    button = button.fill(ui.visuals().widgets.inactive.bg_fill);
                } else {
                    button = button.fill(egui::Color32::TRANSPARENT);
                }

                let response = ui.add(button);
                if response.clicked() && !is_selected {
                    *current_value = value.clone();
                    changed = true;
                }

                if let Some(ref mut final_res) = final_response {
                    *final_res = final_res.union(response.clone());
                } else {
                    final_response = Some(response.clone());
                }
            }
        });
    });

    let mut response = final_response.unwrap();
    if changed {
        response.mark_changed();
    }
    response
}
