using MixCut.Models;

namespace MixCut.Views;

/// <summary>项目相关内容视图接口：导航到该视图时加载指定项目的数据。</summary>
public interface IProjectView
{
    /// <summary>加载指定项目的数据（导航切换时调用，对齐 macOS 版 onAppear + onChange 模式）。</summary>
    void LoadProject(Project project);
}
