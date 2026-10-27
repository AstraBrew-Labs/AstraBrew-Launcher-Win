using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>检测项状态。</summary>
    public enum CheckState
    {
        /// <summary>正在检测。</summary>
        Pending,

        /// <summary>已就绪。</summary>
        Ready,

        /// <summary>已安装但可更新。</summary>
        Update,

        /// <summary>未安装 / 未检测到。</summary>
        Missing,
    }

    /// <summary>环境检测行：图标 + 名称 + 版本 + 状态胶囊。</summary>
    public sealed partial class CheckRow : UserControl
    {
        public CheckRow()
        {
            InitializeComponent();
        }

        /// <summary>Segoe Fluent Icons 字形。</summary>
        public string Glyph
        {
            get => IconGlyph.Glyph;
            set => IconGlyph.Glyph = value;
        }

        /// <summary>检测项名称。</summary>
        public string Title
        {
            get => TitleText.Text;
            set => TitleText.Text = value;
        }

        /// <summary>副标题，通常是版本号或安装路径。</summary>
        public string Detail
        {
            get => DetailText.Text;
            set => DetailText.Text = value;
        }

        /// <summary>切换状态；传入 text 可覆盖默认文案（如「未检测到」）。</summary>
        public void SetState(CheckState state, string? text = null)
        {
            PendingPanel.Visibility = state == CheckState.Pending ? Visibility.Visible : Visibility.Collapsed;
            ReadyPill.Visibility = state == CheckState.Ready ? Visibility.Visible : Visibility.Collapsed;
            UpdatePill.Visibility = state == CheckState.Update ? Visibility.Visible : Visibility.Collapsed;
            MissingPill.Visibility = state == CheckState.Missing ? Visibility.Visible : Visibility.Collapsed;

            if (text is null)
            {
                return;
            }

            switch (state)
            {
                case CheckState.Pending:
                    PendingText.Text = text;
                    break;
                case CheckState.Ready:
                    ReadyText.Text = text;
                    break;
                case CheckState.Update:
                    UpdateText.Text = text;
                    break;
                case CheckState.Missing:
                    MissingText.Text = text;
                    break;
            }
        }
    }
}
