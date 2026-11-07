using System;
using System.Threading;
using System.Threading.Tasks;

namespace AstraBrew_Launcher.Views.Onboarding
{
    /// <summary>
    /// 引导中一步的契约。外壳只认这五个成员，不关心每步具体在做什么：
    /// 它负责切面板、算页脚按钮能不能点、以及在用户离开 / 重播时把任务取消掉。
    ///
    /// 三条约定：
    /// · <see cref="EnterAsync"/> 里不要吞异常——外壳只 await，不 catch（面板自己该兜的兜住）；
    /// · 任何会改变 <see cref="CanContinue"/> 的地方都要发 <see cref="StateChanged"/>，
    ///   否则页脚按钮会停在旧状态（这是最容易漏的一处）；
    /// · <see cref="Leave"/> 必须能安全地重复调用：用户可能连点「上一步」。
    /// </summary>
    public interface IOnboardingStep
    {
        /// <summary>面板状态变了（可以继续 / 不能继续 / 忙完了），外壳据此重算页脚。</summary>
        event EventHandler? StateChanged;

        /// <summary>当前能否进入下一步。</summary>
        bool CanContinue { get; }

        /// <summary>是否有长任务在跑（安装、扫描、测速）。跑着时外壳不给切步骤。</summary>
        bool IsBusy { get; }

        /// <summary>不能继续时的原因，写进按钮 ToolTip。</summary>
        string? BlockingReason { get; }

        /// <summary>进入本步：启动检测 / 读取磁盘现状。</summary>
        Task EnterAsync(CancellationToken token);

        /// <summary>离开本步：取消本步内还在跑的任务。</summary>
        void Leave();

        /// <summary>重播引导时清空状态，回到刚进入本步的样子。</summary>
        void Reset();
    }
}
