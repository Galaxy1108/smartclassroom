using Xunit;

// Avalonia headless 的 Application/Styles/FontManager 是进程级共享状态，
// 并行跑会互相踩（表现为 fonts:SystemFonts 键缺失）。串行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
