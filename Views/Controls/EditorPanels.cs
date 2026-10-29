using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AstraBrew_Launcher.Views.Controls
{
    /// <summary>
    /// 横向铺开、到边换行的面板。WinUI 3 没有 FlexLayout，标签与关键词的换行靠这个最小实现。
    /// </summary>
    public sealed class WrapPanel : Panel
    {
        public double HorizontalSpacing { get; set; }

        public double VerticalSpacing { get; set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            double limit = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
            double x = 0;
            double y = 0;
            double lineHeight = 0;
            double widest = 0;

            foreach (UIElement child in Children)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Size desired = child.DesiredSize;

                if (x > 0 && x + desired.Width > limit)
                {
                    x = 0;
                    y += lineHeight + VerticalSpacing;
                    lineHeight = 0;
                }

                x += desired.Width + HorizontalSpacing;
                widest = Math.Max(widest, x - HorizontalSpacing);
                lineHeight = Math.Max(lineHeight, desired.Height);
            }

            double width = double.IsInfinity(limit) ? widest : Math.Min(widest, limit);
            return new Size(width, y + lineHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            double y = 0;
            double lineHeight = 0;

            foreach (UIElement child in Children)
            {
                Size desired = child.DesiredSize;
                if (x > 0 && x + desired.Width > finalSize.Width)
                {
                    x = 0;
                    y += lineHeight + VerticalSpacing;
                    lineHeight = 0;
                }

                child.Arrange(new Rect(x, y, desired.Width, desired.Height));
                x += desired.Width + HorizontalSpacing;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }

            return finalSize;
        }
    }

    /// <summary>
    /// 等宽多列面板，列数由外部按窗口宽度写入。每行高度取本行最高的一张卡，
    /// 这样同排卡片底边始终对齐。
    /// </summary>
    public sealed class ColumnGrid : Panel
    {
        private int _columns = 2;

        public int Columns
        {
            get => _columns;
            set
            {
                if (_columns == value)
                {
                    return;
                }

                _columns = value;
                InvalidateMeasure();
            }
        }

        public double ColumnSpacing { get; set; }

        public double RowSpacing { get; set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            int columns = Math.Max(1, Columns);
            double cell = CellWidth(availableSize.Width, columns);

            double y = 0;
            double rowHeight = 0;
            double widest = 0;
            int index = 0;

            foreach (UIElement child in Children)
            {
                child.Measure(new Size(cell, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                widest = Math.Max(widest, child.DesiredSize.Width);

                if (index % columns == columns - 1)
                {
                    y += rowHeight + RowSpacing;
                    rowHeight = 0;
                }

                index++;
            }

            if (index % columns != 0 || index == 0)
            {
                y += rowHeight;
            }
            else if (index > 0)
            {
                y -= RowSpacing;
            }

            double width = double.IsInfinity(availableSize.Width) ? widest : availableSize.Width;
            return new Size(width, Math.Max(0, y));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int columns = Math.Max(1, Columns);
            double cell = CellWidth(finalSize.Width, columns);

            double y = 0;
            for (int start = 0; start < Children.Count; start += columns)
            {
                int end = Math.Min(start + columns, Children.Count);
                double rowHeight = 0;
                for (int i = start; i < end; i++)
                {
                    rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
                }

                for (int i = start; i < end; i++)
                {
                    double x = (i - start) * (cell + ColumnSpacing);
                    Children[i].Arrange(new Rect(x, y, cell, rowHeight));
                }

                y += rowHeight + RowSpacing;
            }

            return finalSize;
        }

        private double CellWidth(double available, int columns)
        {
            if (double.IsInfinity(available))
            {
                return 320;
            }

            return Math.Max(0, (available - ((columns - 1) * ColumnSpacing)) / columns);
        }
    }
}
