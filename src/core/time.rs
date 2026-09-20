//! 时间戳格式化工具。
//!
//! macOS 自带 `date` 命令可以按任意格式打印时间，Windows 上只有 `cmd` 的
//! `%DATE%` / `%TIME%`（输出格式随系统区域设置变化，中文系统上还带「年月日」字样），
//! 都不适合用来生成稳定的文件名或展示文本。
//!
//! 因此这里自行做「Unix 时间戳 → 民用年月日」的换算：算法是固定的整数运算，
//! 不受系统区域与语言影响，也不需要为此引入日期库依赖。

/// 读取本机相对 UTC 的偏移秒数（含夏令时修正）；读取失败时按 UTC 处理。
pub fn local_utc_offset_seconds() -> i64 {
    use windows_sys::Win32::System::Time::{GetTimeZoneInformation, TIME_ZONE_INFORMATION};

    // `Bias` 的单位是分钟，且符号与常识相反：UTC = 本地时间 + Bias，所以这里取负。
    let mut zone = TIME_ZONE_INFORMATION::default();
    let state = unsafe { GetTimeZoneInformation(&mut zone) };
    // TIME_ZONE_ID_DAYLIGHT = 2；此时要叠加夏令时修正值。
    let bias = if state == 2 {
        zone.Bias + zone.DaylightBias
    } else {
        zone.Bias + zone.StandardBias
    };
    -(bias as i64) * 60
}

/// 把 Unix 时间戳拆成「本地时间的年月日时分秒」。
///
/// 返回 `(年, 月, 日, 时, 分, 秒)`，均为本地时区。
pub fn local_parts(timestamp: u64) -> (i64, u32, u32, u32, u32, u32) {
    let local = timestamp as i64 + local_utc_offset_seconds();
    let days = local.div_euclid(86_400);
    let time_of_day = local.rem_euclid(86_400);
    let (year, month, day) = civil_from_days(days);
    let hour = (time_of_day / 3600) as u32;
    let minute = ((time_of_day % 3600) / 60) as u32;
    let second = (time_of_day % 60) as u32;
    (year, month, day, hour, minute, second)
}

/// 把「1970-01-01 起的天数」换算成公历年月日。
///
/// 采用 Howard Hinnant 的 `civil_from_days` 算法：以 0000-03-01 为纪元起点，
/// 把闰年规则统一成每 4 年一闰的线性关系，整个过程没有分支。
pub fn civil_from_days(days: i64) -> (i64, u32, u32) {
    let shifted = days + 719_468;
    let era = shifted.div_euclid(146_097);
    let day_of_era = shifted.rem_euclid(146_097);
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let shifted_month = (5 * day_of_year + 2) / 153;
    let day = (day_of_year - (153 * shifted_month + 2) / 5 + 1) as u32;
    let month = if shifted_month < 10 {
        shifted_month + 3
    } else {
        shifted_month - 9
    } as u32;
    // 一月和二月被算进了上一年的第 13、14 月，需要把年份补回来。
    (if month <= 2 { year + 1 } else { year }, month, day)
}

/// 当前本地时间戳（秒）。
pub fn now_timestamp() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|elapsed| elapsed.as_secs())
        .unwrap_or_default()
}

/// 生成 `YYYYMMDD-HHMMSS` 形式的本地时间戳，适合放进文件名。
pub fn compact_stamp() -> String {
    let (year, month, day, hour, minute, second) = local_parts(now_timestamp());
    format!("{year:04}{month:02}{day:02}-{hour:02}{minute:02}{second:02}")
}

/// 生成 `YYYY-MM-DD HH:MM` 形式的本地时间文本，适合展示给用户。
pub fn readable_stamp(timestamp: u64) -> String {
    let (year, month, day, hour, minute, _) = local_parts(timestamp);
    format!("{year:04}-{month:02}-{day:02} {hour:02}:{minute:02}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn epoch_lands_on_1970_01_01() {
        // 时区偏移会平移结果，因此这里直接验证纯换算部分。
        assert_eq!(civil_from_days(0), (1970, 1, 1));
    }

    #[test]
    fn leap_days_are_accounted_for() {
        // 1972-02-29 是闰日；前一日应是 02-28，后一日应是 03-01。
        let leap = civil_from_days(365 * 2 + 31 + 28);
        assert_eq!(leap, (1972, 2, 29));
        assert_eq!(civil_from_days(365 * 2 + 31 + 28 - 1), (1972, 2, 28));
        assert_eq!(civil_from_days(365 * 2 + 31 + 28 + 1), (1972, 3, 1));
    }

    #[test]
    fn century_non_leap_year_is_handled() {
        // 1900 不是闰年（能被 100 整除但不能被 400 整除），2000 是。
        let start_1900 = civil_from_days(-25_567);
        assert_eq!(start_1900, (1900, 1, 1));
        // 1900 年 2 月只有 28 天，所以第 59 天（0 基）应落到 3 月 1 日。
        assert_eq!(civil_from_days(-25_567 + 59), (1900, 3, 1));
    }

    #[test]
    fn stamps_are_well_formed() {
        let stamp = compact_stamp();
        assert_eq!(stamp.len(), 15, "{stamp} 应是 YYYYMMDD-HHMMSS");
        assert_eq!(&stamp[8..9], "-");
        assert!(stamp.chars().filter(|ch| ch.is_ascii_digit()).count() == 14);

        let readable = readable_stamp(1_700_000_000);
        assert_eq!(readable.len(), 16, "{readable} 应是 YYYY-MM-DD HH:MM");
        assert_eq!(&readable[4..5], "-");
    }
}
