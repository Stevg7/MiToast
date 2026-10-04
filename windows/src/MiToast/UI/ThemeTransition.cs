using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace MiToast.UI;

/// <summary>
/// 深色/浅色切换的过场动画：先把内容淡出（<see cref="FadeOutDuration"/>），
/// 在看不见的时候换掉整套色板，再淡入（<see cref="FadeInDuration"/>）。
/// 配色在一帧内硬切会显得很跳，淡出到透明再淡入让眼睛跟得上这次变化
/// （Fluent 的 fade through 做法）。
///
/// 用法：把"应用配色"的动作交给 <see cref="Apply"/>，要不要播过场由它判断——
/// 主题标志没变（例如同时改了卡片宽度、边距）直接换色不播；元素还没进可视树
/// （窗口构造、卡片首次装载）也直接换色；过场途中又切换一次主题则记下最新一次请求，
/// 本轮淡入结束后再走一轮，因此连点开关只会一轮轮过渡到最终状态，不会叠加出闪烁。
///
/// 动画跑在传入元素自身的 Opacity 上，通知卡片内部用的是 RootBorder 的 Opacity
/// （进出场动画），两者互不干扰。
/// </summary>
public static class ThemeTransition
{
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration FadeInDuration = new(TimeSpan.FromMilliseconds(240));

    private sealed class State
    {
        /// <summary>是否记录过一次主题；没记录过说明是首次装载，不该播过场。</summary>
        public bool Registered;

        /// <summary>最近一次请求的深浅色，用来判断主题是否真的变了。</summary>
        public bool Dark;

        /// <summary>过场进行中。</summary>
        public bool Running;

        /// <summary>过场途中收到的最新换色请求，本轮淡入结束后接着跑。</summary>
        public Action? Pending;
    }

    // 弱表持有状态：元素被回收后条目自动消失，长期运行的托盘程序不会积累
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    /// <summary>应用一次主题；主题确实变了且元素已加载时带渐出渐入过场。</summary>
    public static void Apply(FrameworkElement target, bool dark, Action applyTheme)
    {
        var state = States.GetValue(target, _ => new State());

        bool themeChanged = state.Registered && state.Dark != dark;
        state.Registered = true;
        state.Dark = dark;

        if (state.Running)
        {
            // 本轮的淡入结束时会用最新设置换色，所以只需记下这次请求；
            // 主题没变的那次调用（例如同一时刻改了别的设置）不必排队。
            if (themeChanged) state.Pending = applyTheme;
            return;
        }

        // 首次装载（窗口构造、卡片刚创建）或主题没变：直接换色，不播过场
        if (!themeChanged || !target.IsLoaded)
        {
            applyTheme();
            return;
        }

        RunRound(target, state, applyTheme);
    }

    /// <summary>一轮过场：淡出 → 换色 → 淡入；期间又有新主题请求就接着再来一轮。</summary>
    private static void RunRound(FrameworkElement target, State state, Action applyTheme)
    {
        state.Running = true;

        var fadeOut = new DoubleAnimation(1, 0, FadeOutDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            // 此刻内容全透明，换色板看不见硬切
            applyTheme();

            var fadeIn = new DoubleAnimation(0, 1, FadeInDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += (_, _) =>
            {
                var pending = state.Pending;
                state.Pending = null;
                if (pending != null)
                {
                    // 过场期间又切换了主题：再跑一轮，最终停在最新主题上
                    RunRound(target, state, pending);
                    return;
                }

                state.Running = false;
                // 清掉动画时钟，Opacity 交还给属性系统（否则会留下一个持有 1 的时钟）
                target.BeginAnimation(UIElement.OpacityProperty, null);
            };
            target.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };
        target.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }
}
